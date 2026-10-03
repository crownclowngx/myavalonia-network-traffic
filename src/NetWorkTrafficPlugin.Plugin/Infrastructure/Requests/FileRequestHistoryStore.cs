using System.Text.Json;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Capture;

namespace NetWorkTrafficPlugin.Infrastructure.Requests;

/// <summary>
/// 调度前持久化发送意图，崩溃恢复时保守保留未知。没有可信回执时不把恢复解释成一次新的发送机会。
/// 单文件原子替换；进程内串行和跨进程锁防止四条并发请求的结果互相覆盖。
/// </summary>
public sealed class FileRequestHistoryStore(string directory) : IRequestHistoryStore
{
    private sealed record HistoryFile(int SchemaVersion, RequestHistoryEntry[] Entries);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Guid> _active = [];
    private readonly string _root = Path.GetFullPath(directory);
    private const int MaximumBytes = 8 * 1024 * 1024;
    private RequestHistoryEntry Restore(RequestHistoryEntry entry) => !_active.Contains(entry.AttemptId) &&
        entry.Result.State is (RequestExecutionState.Accepted or RequestExecutionState.Sending or RequestExecutionState.Receiving)
        ? entry with { Result = entry.Result with { State = RequestExecutionState.Interrupted,
            Message = entry.Result.Dispatch == RequestDispatchState.NotSent ? "上次执行中断，尚未发送；不会自动恢复。" : "上次执行中断，远端结果可能未知；不会自动重发。" } } : entry;
    private async Task<HistoryFile> ReadFile(CancellationToken token)
    {
        var path = Path.Combine(_root, "history.json"); CaptureDiskBudget.EnsureRegular(path);
        if (!File.Exists(path)) return new(1, []);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, true);
        if (file.Length > MaximumBytes) throw new RequestFailure("RQ012", "历史超过8MiB，保留原文件。");
        var bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try
        {
            var result = JsonSerializer.Deserialize<HistoryFile>(bytes);
            if (result is null || result.SchemaVersion != 1 || result.Entries is null || result.Entries.Length > 1000 ||
                result.Entries.Any(e => e is null || e.Result is null || e.AttemptId != e.Result.AttemptId || !Enum.IsDefined(e.Source) ||
                    !Enum.IsDefined(e.Result.State) || !Enum.IsDefined(e.Result.Dispatch) || e.Address is null || e.Method is null || e.Result.Message is null ||
                    e.Address.Length > RequestLimits.UriCharacters || e.Method.Length > 256 || e.Result.Message.Length > 1024) ||
                result.Entries.Select(e => e.AttemptId).Distinct().Count() != result.Entries.Length)
                throw new RequestFailure("RQ012", "历史版本、内容或身份无效，保留原文件。");
            return result;
        }
        catch (JsonException) { throw new RequestFailure("RQ012", "历史文件损坏，保留原文件。"); }
    }
    public async Task<IReadOnlyList<RequestHistoryEntry>> ReadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var value = await ReadFile(token).ConfigureAwait(false);
            return value.Entries.Select(Restore).ToArray();
        }
        finally { _gate.Release(); }
    }
    public async Task WriteAsync(RequestHistoryEntry entry, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false); string? temporary = null;
        try
        {
            Directory.CreateDirectory(_root); CaptureDiskBudget.EnsureRegular(_root);
            var lockPath = Path.Combine(_root, "history.lock"); CaptureDiskBudget.EnsureRegular(lockPath);
            using var processLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = await ReadFile(token).ConfigureAwait(false);
            var rows = current.Entries.Where(e => e.AttemptId != entry.AttemptId).Select(Restore).Append(entry).OrderByDescending(e => e.StartedUtc).ToList();
            // 历史满时只淘汰终态；本轮最多四个在途项，不能删除调度中的执行意图。
            while (rows.Count > 1000)
            {
                var index = rows.FindLastIndex(e => e.Result.State is not (RequestExecutionState.Accepted or RequestExecutionState.Sending or RequestExecutionState.Receiving));
                if (index < 0) throw new RequestFailure("RQ012", "历史在途项异常，无法安全淘汰。"); rows.RemoveAt(index);
            }
            // 在序列化前按每UTF-16字符至多六个JSON字节预留，不能先分配巨型byte[]再判断越界。
            long Cost(RequestHistoryEntry row) => 1024L + 6L * (row.Address.Length + row.Method.Length + row.Result.Message.Length);
            var cost = rows.Sum(Cost);
            while (cost > MaximumBytes - 128)
            {
                var index = rows.FindLastIndex(e => e.AttemptId != entry.AttemptId && e.Result.State is not (RequestExecutionState.Accepted or RequestExecutionState.Sending or RequestExecutionState.Receiving));
                if (index < 0) throw new RequestFailure("RQ012", "历史索引已达预算，未提交新内容。"); cost -= Cost(rows[index]); rows.RemoveAt(index);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new HistoryFile(1, rows.ToArray()));
            if (bytes.Length > MaximumBytes) throw new RequestFailure("RQ012", "历史序列化超出预算，未提交。");
            temporary = Path.Combine(_root, ".history-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
            { await file.WriteAsync(bytes, token).ConfigureAwait(false); await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true); }
            token.ThrowIfCancellationRequested(); var path = Path.Combine(_root, "history.json");
            CaptureDiskBudget.EnsureRegular(path); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            temporary = null;
            if (entry.Result.State is RequestExecutionState.Accepted or RequestExecutionState.Sending or RequestExecutionState.Receiving) _active.Add(entry.AttemptId);
            else _active.Remove(entry.AttemptId);
        }
        finally { if (temporary is not null) File.Delete(temporary); _gate.Release(); }
    }
}
