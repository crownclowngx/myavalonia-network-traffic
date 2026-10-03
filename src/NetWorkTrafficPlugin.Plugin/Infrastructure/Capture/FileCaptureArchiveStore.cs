using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Infrastructure.Capture;

public sealed record ArchiveFile(string Path, long Length, string Sha256);
public sealed record ArchiveManifest(int SchemaVersion, string ProducerVersion, Guid SessionId, Guid SnapshotId,
    long SnapshotRevision, DateTimeOffset CreatedUtc, long Total, long Evicted, int RecordCount, ArchiveFile[] Files);

/// <summary>
/// 原生目录存档使用“同父目录临时目录 → 完成文件及摘要 → 最后写 manifest → 原子改名”。
/// 不覆盖已有目标，取消只清理本次生成的临时目录；保存冻结前缀，不等待长流结束，不补发请求。
/// checkpoint 是测试故障边界，生产不注入；它不替代真实文件系统，也不产生伪成功证据。
/// </summary>
public sealed class FileCaptureArchiveStore(Action<string>? checkpoint = null) : ICaptureArchive, IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _save = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _operations = [];
    private Task? _dispose;
    public Task<ArchiveSaveResult> SaveAsync(ICaptureSession session, string targetDirectory, CancellationToken token) =>
        Track(ct => SaveCoreAsync(session, targetDirectory, ct), token);
    private Task<T> Track<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token);
            var task = Task.Run(async () => { using (linked) return await operation(linked.Token).ConfigureAwait(false); });
            _operations.Add(task);
            _ = task.ContinueWith(t => { lock (_sync) _operations.Remove(t); _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    private async Task<ArchiveSaveResult> SaveCoreAsync(ICaptureSession session, string targetDirectory, CancellationToken token)
    {
        if (!await _save.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("已有存档任务正在保存。");
        string? staging = null;
        try
        {
            var target = Path.GetFullPath(targetDirectory);
            var parent = Path.GetDirectoryName(target) ?? throw new IOException("存档目标不能是磁盘根目录。");
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("存档目标已存在，请使用新目录名。");
            Directory.CreateDirectory(parent); EnsureAncestors(parent);
            RejectManagedRoot(parent);
            using var lease = await session.FreezeAsync(token).ConfigureAwait(false);
            var snapshotId = Guid.NewGuid();
            staging = Path.Combine(parent, ".capture-save-" + snapshotId.ToString("N"));
            Directory.CreateDirectory(staging); Directory.CreateDirectory(Path.Combine(staging, "bodies"));
            checkpoint?.Invoke("snapshot");
            var records = new List<TrafficRecord>(); var files = new List<ArchiveFile>(); long total = 0;
            foreach (var record in lease.Records)
            {
                token.ThrowIfCancellationRequested();
                var request = await CopyBodyAsync(record.RequestBody, record.RecordId, staging, files, token);
                var response = await CopyBodyAsync(record.ResponseBody, record.RecordId, staging, files, token);
                total = checked(total + request.CommittedBytes + response.CommittedBytes);
                if (total > 2L << 30) throw new IOException("存档正文超过 2 GiB。");
                records.Add(record with { RequestBody = request, ResponseBody = response });
            }
            var recordsPath = Path.Combine(staging, "records.jsonl");
            await using (var output = new FileStream(recordsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                foreach (var record in records)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
                    if (bytes.Length > 262144 || output.Position + bytes.Length + 1 > 64L << 20) throw new IOException("元信息超过存档预算。");
                    await output.WriteAsync(bytes, token); await output.WriteAsync(new byte[] { 10 }, token);
                }
                await output.FlushAsync(token); output.Flush(true);
            }
            files.Add(await InspectAsync(staging, "records.jsonl", token)); checkpoint?.Invoke("records");
            var manifest = new ArchiveManifest(1, "V3/M1", lease.Snapshot.SessionId, snapshotId, lease.Snapshot.Revision,
                DateTimeOffset.UtcNow, lease.Snapshot.Total, lease.Snapshot.Evicted, records.Count, files.ToArray());
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (manifestBytes.Length > 4 << 20) throw new IOException("存档清单超过 4 MiB。");
            await File.WriteAllBytesAsync(Path.Combine(staging, "manifest.json"), manifestBytes, token);
            using (var file = File.Open(Path.Combine(staging, "manifest.json"), FileMode.Open, FileAccess.Write, FileShare.None)) file.Flush(true);
            checkpoint?.Invoke("manifest"); token.ThrowIfCancellationRequested(); checkpoint?.Invoke("commit"); token.ThrowIfCancellationRequested();
            Directory.Move(staging, target); staging = null;
            return new(target, snapshotId, records.Count, total);
        }
        finally
        {
            if (staging is not null) DeleteStaging(staging);
            _save.Release();
        }
    }
    private async Task<BodyDescriptor> CopyBodyAsync(BodyDescriptor body, Guid recordId, string directory, List<ArchiveFile> files, CancellationToken token)
    {
        if (body.CommittedBytes == 0) return body with { FilePath = null };
        if (body.FilePath is null) throw new IOException("正文有提交长度但缺少源文件。");
        var name = $"bodies/{recordId:N}.{(body.Direction == BodyDirection.Request ? "request" : "response")}.bin";
        await CopyPrefixAsync(body.FilePath, SafePath(directory, name, false), body.CommittedBytes, token);
        checkpoint?.Invoke("body"); files.Add(await InspectAsync(directory, name, token));
        return body with { FilePath = name };
    }
    public Task<ICaptureSession> OpenAsync(string directory, CancellationToken token) => Track(ct => OpenCoreAsync(directory, ct), token);
    private static async Task<ICaptureSession> OpenCoreAsync(string directory, CancellationToken token)
    {
        var root = Path.GetFullPath(directory); EnsureAncestors(root);
        var manifestPath = SafePath(root, "manifest.json");
        if (new FileInfo(manifestPath).Length > 4 << 20) throw new InvalidDataException("存档清单超过预算。");
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(await File.ReadAllBytesAsync(manifestPath, token)) ?? throw new InvalidDataException("存档清单为空。");
        if (manifest.SchemaVersion != 1 || manifest.SessionId == Guid.Empty || manifest.SnapshotId == Guid.Empty || manifest.RecordCount is < 0 or > 10020 || manifest.Files is null || manifest.Files.Length > 20041)
            throw new InvalidDataException("存档版本或结构不受支持。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long size = 0;
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file is null || file.Length < 0 || file.Length > 2L << 30 || !names.Add(file.Path) || file.Sha256 is null || file.Sha256.Length != 64) throw new InvalidDataException("存档有重复路径或无效长度/摘要。");
            size = checked(size + file.Length); if (size > (2L << 30) + (64L << 20)) throw new InvalidDataException("存档总量超限。");
            var path = SafePath(root, file.Path, false);
            if (!File.Exists(path)) { invalid.Add(file.Path); continue; }
            EnsureAncestors(path);
            // 先校验物理长度再读哈希，恶意清单不能用很小的声明诱使读取超大文件。
            if (new FileInfo(path).Length != file.Length) { invalid.Add(file.Path); continue; }
            var actual = await InspectAsync(root, file.Path, token);
            if (actual.Length != file.Length || !actual.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) invalid.Add(file.Path);
        }
        if (!names.Contains("records.jsonl") || invalid.Contains("records.jsonl")) throw new InvalidDataException("元信息索引缺失或已改变。");
        var records = await ReadRecordsAsync(SafePath(root, "records.jsonl"), false, token);
        if (records.Length != manifest.RecordCount || records.Any(r => r.SessionId != manifest.SessionId)) throw new InvalidDataException("记录数量或会话身份不匹配。");
        for (var i = 0; i < records.Length; i++)
            records[i] = records[i] with
            {
                RequestBody = Relocate(records[i].RequestBody, root, names, invalid),
                ResponseBody = Relocate(records[i].ResponseBody, root, names, invalid)
            };
        return new ArchiveCaptureSession(root, manifest, records, invalid.Count > 0 ? "部分正文缺失或校验失败，未标记完整。" : null);
    }
    private static BodyDescriptor Relocate(BodyDescriptor body, string root, HashSet<string> names, HashSet<string> invalid)
    {
        ValidateBody(body);
        if (body.FilePath is null) return body.CommittedBytes == 0 ? body : body with { Capture = CaptureState.Failed, Reason = "已保存正文文件缺失。", CommittedBytes = 0 };
        var path = SafePath(root, body.FilePath, false);
        if (!names.Contains(body.FilePath) || invalid.Contains(body.FilePath) || !File.Exists(path) || new FileInfo(path).Length != body.CommittedBytes)
            return body with { Capture = CaptureState.Failed, Reason = "正文缺失或摘要/长度不一致。", CommittedBytes = 0, FilePath = null };
        return body with { FilePath = path };
    }
    private static void ValidateBody(BodyDescriptor body)
    {
        if (body is null || body.BodyId == Guid.Empty || body.ObservedBytes < 0 || body.CommittedBytes < 0 || body.CommittedBytes > body.ObservedBytes || body.CommittedBytes > 1L << 30 || body.ExpectedBytes < 0 ||
            !Enum.IsDefined(body.Direction) || !Enum.IsDefined(body.Representation) || !Enum.IsDefined(body.Transport) || !Enum.IsDefined(body.Capture)) throw new InvalidDataException("正文描述符不合法。");
        if (body.Capture == CaptureState.Complete && (body.Transport != TransportState.Completed || body.CommittedBytes != body.ObservedBytes || body.ExpectedBytes is { } length && length != body.ObservedBytes))
            throw new InvalidDataException("存档把不完整正文标成完整。");
    }
    public Task<ICaptureSession> RecoverAsync(string directory, CancellationToken token) => Track(async ct =>
    {
        var root = Path.GetFullPath(directory); EnsureAncestors(root);
        var marker = SafePath(root, ".network-traffic-session");
        if (new FileInfo(marker).Length > 128 || !Guid.TryParse(await File.ReadAllTextAsync(marker, ct), out var sessionId)) throw new InvalidDataException("不是本插件拥有的会话。");
        var records = await ReadRecordsAsync(SafePath(root, "records.jsonl"), true, ct);
        if (records.Any(r => r.SessionId != sessionId)) throw new InvalidDataException("恢复记录的会话身份不一致。");
        BodyDescriptor Recover(BodyDescriptor body)
        {
            ValidateBody(body);
            if (body.FilePath is null) return body;
            var expected = Path.Combine(root, body.BodyId.ToString("N") + ".body");
            if (!string.Equals(Path.GetFullPath(body.FilePath), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("恢复正文路径不属于会话。");
            var stored = File.Exists(expected) ? Math.Min(body.CommittedBytes, new FileInfo(SafePath(root, Path.GetFileName(expected))).Length) : 0;
            return body with { CommittedBytes = stored, FilePath = stored == 0 ? null : expected,
                Capture = body.Transport == TransportState.Receiving || stored != body.CommittedBytes ? CaptureState.Partial : body.Capture,
                Reason = body.Transport == TransportState.Receiving ? "异常结束时仍在接收；恢复不会联网。" : body.Reason };
        }
        records = records.Select(r => r with { RequestBody = Recover(r.RequestBody), ResponseBody = Recover(r.ResponseBody) }).ToArray();
        var manifest = new ArchiveManifest(1, "V3/M1-recovered", sessionId, Guid.NewGuid(), records.Select(r => r.Revision).DefaultIfEmpty().Max(), DateTimeOffset.UtcNow, records.Length, 0, records.Length, []);
        return (ICaptureSession)new ArchiveCaptureSession(root, manifest, records, "从工作日志恢复，只读；原接收中状态不代表正在联网。");
    }, token);
    public Task ExportBodyAsync(BodyDescriptor body, string targetFile, CancellationToken token) => Track(async ct =>
    {
        if (body.FilePath is null || body.CommittedBytes <= 0) throw new InvalidOperationException("没有可导出的已保存正文。");
        var target = Path.GetFullPath(targetFile); var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent); EnsureAncestors(parent);
        RejectManagedRoot(parent);
        if (File.Exists(target)) throw new IOException("目标文件已存在。");
        var temporary = Path.Combine(parent, ".body-export-" + Guid.NewGuid().ToString("N"));
        try { await CopyPrefixAsync(body.FilePath, temporary, body.CommittedBytes, ct); ct.ThrowIfCancellationRequested(); File.Move(temporary, target); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }, token);
    private static async Task CopyPrefixAsync(string source, string target, long length, CancellationToken token)
    {
        EnsureAncestors(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous);
        if (input.Length < length) throw new IOException("源正文短于冻结提交水位。");
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        var buffer = new byte[65536];
        for (long remaining = length; remaining > 0;)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
            if (read == 0) throw new EndOfStreamException("源正文在复制期间变短。");
            await output.WriteAsync(buffer.AsMemory(0, read), token); remaining -= read;
        }
        await output.FlushAsync(token); output.Flush(true);
    }
    private static async Task<ArchiveFile> InspectAsync(string root, string name, CancellationToken token)
    {
        var path = SafePath(root, name);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var length = file.Length; var digest = await SHA256.HashDataAsync(file, token);
        if (file.Length != length) throw new IOException("校验期间文件发生变化。");
        return new(name, length, Convert.ToHexString(digest));
    }
    public static string SafePath(string root, string relative, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("存档路径必须是根目录内的普通相对路径。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("存档路径越界。");
        if (mustExist || File.Exists(full) || Directory.Exists(full)) EnsureAncestors(full);
        else EnsureAncestors(Path.GetDirectoryName(full)!);
        return full;
    }
    private static void EnsureAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current)) CaptureDiskBudget.EnsureRegular(current);
    }
    private static void RejectManagedRoot(string path)
    {
        // 工作根由运行服务统一计费，不能把任意嵌套存档或导出写进去绕过会话预留。
        // 用户存档使用独立目录，失败临时文件仍保留在这个显式选择的目录内供排查。
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (File.Exists(Path.Combine(current, ".network-traffic-root"))) throw new IOException("存档和导出请使用捕获工作根之外的目录。");
    }
    private static async Task<TrafficRecord[]> ReadRecordsAsync(string path, bool journal, CancellationToken token)
    {
        if (new FileInfo(path).Length > 64L << 20) throw new InvalidDataException("元信息文件超过 64 MiB。");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous);
        var records = new Dictionary<Guid, TrafficRecord>(); using var line = new MemoryStream(); var chunk = new byte[65536];
        long cachedBytes = 0;
        int read;
        while ((read = await file.ReadAsync(chunk, token)) > 0)
            for (var i = 0; i < read; i++)
            {
                if (chunk[i] != 10)
                {
                    if (line.Length >= 262144) throw new InvalidDataException("单条元信息超过预算。");
                    line.WriteByte(chunk[i]); continue;
                }
                var record = JsonSerializer.Deserialize<TrafficRecord>(line.GetBuffer().AsSpan(0, (int)line.Length)) ?? throw new InvalidDataException("元信息条目为空。");
                line.SetLength(0);
                if (record.RecordId == Guid.Empty || record.SessionId == Guid.Empty || record.Sequence < 1 || record.Revision < 1 ||
                    record.RequestHeaders is null || record.ResponseHeaders is null || !Enum.IsDefined(record.Source) ||
                    string.IsNullOrWhiteSpace(record.Method) || string.IsNullOrWhiteSpace(record.OriginalUri) || string.IsNullOrWhiteSpace(record.EffectiveUri) ||
                    !double.IsFinite(record.ElapsedMilliseconds) || record.ElapsedMilliseconds < 0 ||
                    record.RequestHeaders.Concat(record.ResponseHeaders).Any(h => h is null || h.Name is null || h.Value is null)) throw new InvalidDataException("记录身份或头信息无效。");
                ValidateBody(record.RequestBody); ValidateBody(record.ResponseBody);
                if (record.RequestBody.Direction != BodyDirection.Request || record.ResponseBody.Direction != BodyDirection.Response || record.RequestBody.BodyId == record.ResponseBody.BodyId)
                    throw new InvalidDataException("双向正文身份或方向无效。");
                if (!journal && records.ContainsKey(record.RecordId)) throw new InvalidDataException("存档包含重复记录。");
                if (records.TryGetValue(record.RecordId, out var old)) cachedBytes -= RecordCost(old);
                cachedBytes = checked(cachedBytes + RecordCost(record));
                if (cachedBytes > 32L << 20) throw new InvalidDataException("存档元信息内存超过 32 MiB，请拆分会话。");
                records[record.RecordId] = record;
                if (records.Count > 10020) throw new InvalidDataException("存档记录数超过上限。");
            }
        if (line.Length > 0 && !journal) throw new InvalidDataException("元信息尾行不完整。");
        return records.Values.OrderBy(r => r.Sequence).ToArray();
    }
    private static long RecordCost(TrafficRecord r) => 2048L + 2L * (r.OriginalUri.Length + r.EffectiveUri.Length) +
        r.RequestHeaders.Concat(r.ResponseHeaders).Sum(h => 64L + 2L * (h.Name.Length + h.Value.Length));
    private static void DeleteStaging(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Path.GetFileName(full).StartsWith(".capture-save-", StringComparison.Ordinal)) throw new IOException("拒绝清理未知目录。");
        EnsureAncestors(full);
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_dispose ??= Task.Run(async () =>
        {
            _closing.Cancel(); Task[] tasks; lock (_sync) tasks = _operations.ToArray();
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { if (tasks.All(t => t.IsCompleted)) { _closing.Dispose(); _save.Dispose(); } }
        }));
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

internal sealed class ArchiveCaptureSession(string directory, ArchiveManifest manifest, TrafficRecord[] records, string? diagnostic) : ICaptureSession, ICaptureBodyStreamReader
{
    public IDisposable AcquireReadLease() => new CaptureReadLease(() => { });
    public bool IsArchive => true;
    public CaptureSnapshot Snapshot() => new(manifest.SessionId, manifest.SnapshotRevision, false, manifest.Total, manifest.Evicted,
        records.Select(LiveCaptureSession.Summary).OrderByDescending(r => r.Sequence).ToArray(), diagnostic ?? "只读快照；不会自动抓包或补下载。");
    public Task<TrafficRecord?> ReadAsync(Guid id, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(records.FirstOrDefault(r => r.RecordId == id)); }
    public Task<TrafficSummary[]> QueryAsync(CaptureQuery query, CancellationToken token) => Task.Run(() =>
    {
        query.Validate(); token.ThrowIfCancellationRequested(); var selected = records.Where(query.Matches);
        return (query.NewestFirst ? selected.OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.Sequence) : selected.OrderBy(r => r.StartedUtc).ThenBy(r => r.Sequence)).Select(LiveCaptureSession.Summary).ToArray();
    }, token);
    public Task<byte[]> ReadBodyAsync(BodyDescriptor body, int maximumBytes, CancellationToken token) => CaptureBodyReader.ReadAsync(directory, body, maximumBytes, token);
    public Task<Stream> OpenBodyAsync(BodyDescriptor body, CancellationToken token) => CaptureBodyReader.OpenAsync(directory, body, token);
    public StreamPreview? Stream(Guid id) => null;
    public Task<CaptureLease> FreezeAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(new CaptureLease(Snapshot(), records.ToArray(), () => { })); }
}
