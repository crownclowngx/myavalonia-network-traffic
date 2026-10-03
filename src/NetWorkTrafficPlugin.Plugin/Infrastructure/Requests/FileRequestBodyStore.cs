using System.Security.Cryptography;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Capture;

namespace NetWorkTrafficPlugin.Infrastructure.Requests;

/// <summary>
/// 文件选择只产生不可变资产，不保存用户路径。复制时使用独占写保护，并对长度/摘要逐项校验；
/// 读取租约使模板删除与在途发送解耦。资产目录为平面 GUID 文件，路径永远不由模板 JSON 提供。
/// </summary>
public sealed class FileRequestBodyStore : IRequestBodyStore, IRequestFileImporter, IDisposable
{
    private sealed record Asset(long Length, string Digest, bool Persistent);
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, (long Length, string Digest, int Readers)> _assets = [];
    private readonly HashSet<Guid> _persistent = [];
    private readonly HashSet<Guid> _retired = [];
    private readonly FileStream _owner;
    private long _bytes;
    private bool _disposed;
    private readonly ManagedDiskBudget? _disk;
    public long Bytes { get { lock (_assets) return _bytes; } }
    public FileRequestBodyStore(string root, ManagedDiskBudget? disk = null)
    {
        _disk = disk;
        _root = Path.GetFullPath(root); Directory.CreateDirectory(_root); CaptureDiskBudget.EnsureRegular(_root);
        // 一个资产根只有一个活动所有者，防止两个Host分别计算256MiB后同时超额提交。
        var lockPath = Path.Combine(_root, ".request-bodies.lock"); CaptureDiskBudget.EnsureRegular(lockPath);
        _owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var metadata in Directory.EnumerateFiles(_root, "*.asset.json"))
            {
                CaptureDiskBudget.EnsureRegular(metadata);
                var name = Path.GetFileName(metadata)[..^11];
                if (!Guid.TryParseExact(name, "N", out var id)) continue;
                if (new FileInfo(metadata).Length > 1024) throw new IOException("正文资产元信息超限。");
                var asset = System.Text.Json.JsonSerializer.Deserialize<Asset>(File.ReadAllText(metadata)) ?? throw new IOException("正文资产元信息损坏。");
                var path = PathFor(id); CaptureDiskBudget.EnsureRegular(path);
                if (!asset.Persistent) { File.Delete(path); File.Delete(metadata); continue; }
                if (asset.Length is < 0 or > RequestLimits.FileBytes || asset.Digest.Length != 64 || !File.Exists(path) || new FileInfo(path).Length != asset.Length)
                    throw new IOException("正文资产缺失或长度不符，保留现场。");
                _assets.Add(id, (asset.Length, asset.Digest, 0)); _persistent.Add(id); _bytes += asset.Length;
                if (_assets.Count > 200 || _bytes > RequestLimits.StagingBytes) throw new IOException("正文资产总额超过预算。");
            }
            // 只有符合本存储器GUID命名的未提交资产才可清理，不递归进入任何子目录。
            foreach (var path in Directory.EnumerateFiles(_root))
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(".body", StringComparison.Ordinal) && Guid.TryParseExact(name[..^5], "N", out var id) && !_assets.ContainsKey(id) ||
                    name.EndsWith(".body.tmp", StringComparison.Ordinal) && Guid.TryParseExact(name[..^9], "N", out _))
                { CaptureDiskBudget.EnsureRegular(path); File.Delete(path); }
            }
            _disk?.Register(_root, _bytes);
        }
        catch { _owner.Dispose(); throw; }
    }
    public async Task<Guid> ImportAsync(string source, CancellationToken token)
    {
        CaptureDiskBudget.EnsureRegular(source);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return await ImportAsync(input, input.Length, token).ConfigureAwait(false);
    }
    public async Task<Guid> ImportAsync(Stream input, long length, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null; var reserved = 0; var registered = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_assets)
                if (length < 0 || length > RequestLimits.FileBytes || _bytes + length > RequestLimits.StagingBytes || _assets.Count >= 200)
                    throw new RequestFailure("RQ008", "文件超过 64 MiB，或暂存资产合计超过 256 MiB。");
            if (_disk is not null && (reserved = _disk.Reserve(_root, (int)length)) != length)
                throw new RequestFailure("RQ008", "共享4GiB磁盘额度不足，未复制正文。");
            var id = Guid.NewGuid(); var target = PathFor(id); temporary = target + ".tmp";
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long copied = 0; int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    copied += read;
                    if (copied > length) throw new IOException("文件在复制期间变长，请重新选择。");
                    hash.AppendData(buffer.AsSpan(0, read)); await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (copied != length) throw new IOException("文件在复制期间改变，请重新选择。");
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, target); temporary = null;
            lock (_assets) { _assets.Add(id, (length, Convert.ToHexString(hash.GetHashAndReset()), 0)); _bytes += length; }
            registered = true;
            try { WriteAsset(id, false); }
            catch { lock (_assets) Delete(id); throw; }
            return id;
        }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); if (!registered && reserved > 0) _disk?.Release(_root, reserved); }
            finally { _gate.Release(); }
        }
    }
    public async Task<RequestBodyLease> AcquireAsync(Guid assetId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (long Length, string Digest, int Readers) asset;
            lock (_assets)
                if (!_assets.TryGetValue(assetId, out asset)) throw new RequestFailure("RQ004", "正文资产不可用，请重新选择完整文件。");
            var path = PathFor(assetId); CaptureDiskBudget.EnsureRegular(path);
            // 持有同一个句柄直至发送结束；校验后不再次按路径打开，避免校验和发送之间替换文件。
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            try
            {
                if (stream.Length != asset.Length || Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != asset.Digest)
                    throw new IOException("正文资产长度或摘要不符，拒绝发送。");
                stream.Position = 0;
                lock (_assets) _assets[assetId] = asset with { Readers = asset.Readers + 1 };
                return new FileLease(stream, asset.Length, asset.Digest, () => Release(assetId));
            }
            catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally { _gate.Release(); }
    }
    private void Release(Guid id)
    {
        lock (_assets)
        {
            var asset = _assets[id]; _assets[id] = asset with { Readers = asset.Readers - 1 };
            if ((_disposed || _retired.Contains(id)) && asset.Readers == 1 && !_persistent.Contains(id)) Delete(id);
            if (_disposed && _assets.Values.All(a => a.Readers == 0)) _owner.Dispose();
        }
    }
    /// <summary>模板索引提交前先固定资产持久性；返回旧状态，索引失败时由调用方恢复，不留下悬空引用。</summary>
    public async Task<bool> SetPersistentAsync(Guid id, bool persistent, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_assets)
            {
                if (!_assets.ContainsKey(id)) throw new RequestFailure("RQ004", "正文资产不存在。");
                var previous = _persistent.Contains(id); WriteAsset(id, persistent);
                if (persistent) _persistent.Add(id); else _persistent.Remove(id);
                return previous;
            }
        }
        finally { _gate.Release(); }
    }
    private void WriteAsset(Guid id, bool persistent)
    {
        var asset = _assets[id]; var target = Path.Combine(_root, id.ToString("N") + ".asset.json");
        CaptureDiskBudget.EnsureRegular(target); var temporary = target + ".tmp";
        CaptureDiskBudget.EnsureRegular(temporary);
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                System.Text.Json.JsonSerializer.Serialize(file, new Asset(asset.Length, asset.Digest, persistent));
                file.Flush(true);
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>索引成功替换后回收失去模板引用的资产。在途租约仍持有同一文件，最后一位读取者离开才删除。</summary>
    public async Task ReconcileTemplatesAsync(IReadOnlySet<Guid> references, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_assets)
                foreach (var id in _persistent.Where(id => !references.Contains(id)).ToArray())
                {
                    WriteAsset(id, false); _persistent.Remove(id); _retired.Add(id);
                    if (_assets[id].Readers == 0) Delete(id);
                }
        }
        finally { _gate.Release(); }
    }
    public void Remove(Guid id)
    {
        _gate.Wait();
        try
        {
            lock (_assets)
            {
                if (!_assets.TryGetValue(id, out var asset)) return;
                if (asset.Readers != 0 || _persistent.Contains(id)) throw new RequestFailure("RQ009", "正文正在使用或由模板持有，不能删除。");
                Delete(id);
            }
        }
        finally { _gate.Release(); }
    }
    private string PathFor(Guid id) => Path.Combine(_root, id.ToString("N") + ".body");
    private void Delete(Guid id)
    {
        var path = PathFor(id); CaptureDiskBudget.EnsureRegular(path); File.Delete(path);
        var metadata = Path.Combine(_root, id.ToString("N") + ".asset.json"); CaptureDiskBudget.EnsureRegular(metadata); File.Delete(metadata);
        _disk?.Release(_root, _assets[id].Length); _bytes -= _assets[id].Length; _assets.Remove(id); _retired.Remove(id);
    }
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            lock (_assets)
            {
                if (_disposed) return; _disposed = true;
                foreach (var id in _assets.Where(a => a.Value.Readers == 0 && !_persistent.Contains(a.Key)).Select(a => a.Key).ToArray()) Delete(id);
                if (_assets.Values.All(a => a.Readers == 0)) _owner.Dispose();
            }
        }
        finally { _gate.Release(); }
    }
    private sealed class FileLease(FileStream stream, long length, string digest, Action release) : RequestBodyLease
    {
        private Action? _release = release;
        private int _opened;
        public override long Length => length;
        public override string Digest => digest;
        public override ValueTask<Stream> OpenAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_release is null, this);
            if (Interlocked.Exchange(ref _opened, 1) != 0) throw new InvalidOperationException("正文租约只允许打开一次发送流。");
            return ValueTask.FromResult<Stream>(stream);
        }
        public override void Dispose() { var action = Interlocked.Exchange(ref _release, null); if (action is null) return; stream.Dispose(); action(); }
    }
}
