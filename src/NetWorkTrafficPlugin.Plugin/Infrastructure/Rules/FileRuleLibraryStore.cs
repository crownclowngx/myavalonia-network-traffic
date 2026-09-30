using System.Text.Json;
using NetWorkTrafficPlugin.Application.Rules;

namespace NetWorkTrafficPlugin.Infrastructure.Rules;

/// <summary>
/// 库文件是一个提交单元，修订和视图不分开写。进程内串行、跨进程独占锁以及 generation
/// 共同防止两个页面/宿主静默覆盖；临时文件在同目录原子替换，取消不留下半份正文。
/// </summary>
public sealed class FileRuleLibraryStore(string directory, Action<string>? checkpoint = null) : IRuleLibraryStore
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _knownGeneration;
    public long KnownGeneration => Interlocked.Read(ref _knownGeneration);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, MaxDepth = 32 };
    public async Task<RuleLibrary> LoadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await ReadAsync(token); }
        finally { _gate.Release(); }
    }
    private async Task<RuleLibrary> ReadAsync(CancellationToken token)
    {
        var path = Path.Combine(_directory, "rules.json");
        if (!File.Exists(path)) return RuleLibrary.Empty;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, true);
        if (stream.Length > RuleLibraryValidation.MaximumBytes) throw new InvalidDataException("规则库超过8MiB，保留原文件。");
        // 有界读入固定长度，外部追加不能让流式反序列化无限增长。
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token);
        RuleLibrary library;
        try { library = JsonSerializer.Deserialize<RuleLibrary>(bytes, _json) ?? throw new InvalidDataException("规则库为空。"); }
        catch (JsonException ex) { throw new InvalidDataException("规则库JSON损坏，未覆盖原文件。", ex); }
        RuleLibraryValidation.Validate(library, token);
        Interlocked.Exchange(ref _knownGeneration, library.Generation);
        return library;
    }
    public async Task<RuleLibrary> CommitAsync(RuleLibrary candidate, long expectedGeneration, CancellationToken token)
    {
        await _gate.WaitAsync(token); string? temporary = null;
        try
        {
            Directory.CreateDirectory(_directory);
            FileStream processLock;
            try { processLock = new FileStream(Path.Combine(_directory, "rules.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw new RuleLibraryConflictException("规则库正在被另一个宿主使用：" + ex.GetType().Name); }
            using (processLock)
            {
                var current = await ReadAsync(token);
                if (current.Generation != expectedGeneration || candidate.Generation != expectedGeneration) throw new RuleLibraryConflictException("规则库已更新，请重新加载后另存或重试。");
                foreach (var prior in current.Revisions)
                    if (!candidate.Revisions.Contains(prior)) throw new InvalidDataException("不能修改或删除既有修订。");
                var committed = candidate with { Generation = checked(expectedGeneration + 1) };
                RuleLibraryValidation.Validate(committed, token);
                // 用有界流阻止序列化先扩张为巨大的 byte[] 再检查限额。
                using var output = new LimitedMemoryStream(); await JsonSerializer.SerializeAsync(output, committed, _json, token);
                temporary = Path.Combine(_directory, ".rules-" + Guid.NewGuid().ToString("N") + ".tmp");
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                { output.Position = 0; await output.CopyToAsync(file, token); await file.FlushAsync(token); file.Flush(true); }
                checkpoint?.Invoke("written"); token.ThrowIfCancellationRequested(); checkpoint?.Invoke("before-commit"); token.ThrowIfCancellationRequested();
                var target = Path.Combine(_directory, "rules.json");
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                temporary = null;
                // 到达提交点后即使令牌取消，返回的也必须是已提交事实；页面自行丢弃迟到的激活意图。
                Interlocked.Exchange(ref _knownGeneration, committed.Generation);
                checkpoint?.Invoke("committed"); return committed;
            }
        }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { /* 仅留下本次临时文件供诊断，绝不覆盖原库。 */ } }
            _gate.Release();
        }
    }
    private sealed class LimitedMemoryStream : MemoryStream
    {
        private void Check(int count) { if (Position + count > RuleLibraryValidation.MaximumBytes) throw new InvalidDataException("规则库超过8MiB。"); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { Check(buffer.Length); return base.WriteAsync(buffer, token); }
    }
}
