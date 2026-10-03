using System.Text.Json;
using NetWorkTrafficPlugin.Application.Rewrite;

namespace NetWorkTrafficPlugin.Infrastructure.Rewrite;

/// <summary>
/// 独立改写库沿用 M2 的提交协议，但不把分析视图和代理配置混入通用仓储。
/// 进程内串行、跨进程独占锁和代次比较各解决一层竞争；同目录替换是唯一提交点。
/// </summary>
public sealed class FileRewriteLibraryStore(string directory, Action<string>? checkpoint = null) : IRewriteLibraryStore
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, MaxDepth = 32 };
    private string Target => Path.Combine(_directory, "rewrites.json");
    public async Task<RewriteLibrary> LoadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await ReadAsync(token); }
        finally { _gate.Release(); }
    }
    private async Task<RewriteLibrary> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(Target)) return RewriteLibrary.Empty;
        await using var stream = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, true);
        if (stream.Length > RewriteLibraryValidation.MaximumBytes) throw new InvalidDataException("改写库超过8 MiB，原文件保留。");
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token);
        RewriteLibrary library;
        try { library = JsonSerializer.Deserialize<RewriteLibrary>(bytes, _json) ?? throw new InvalidDataException("改写库为空。"); }
        catch (JsonException ex) { throw new InvalidDataException("改写库 JSON 损坏，原文件保留。", ex); }
        RewriteLibraryValidation.Validate(library, token); return library;
    }
    public async Task<RewriteLibrary> CommitAsync(RewriteLibrary candidate, long expectedGeneration, CancellationToken token)
    {
        await _gate.WaitAsync(token); string? temporary = null;
        try
        {
            Directory.CreateDirectory(_directory);
            FileStream processLock;
            try { processLock = new FileStream(Path.Combine(_directory, "rewrites.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new RewriteGenerationConflictException("改写库正由另一个宿主提交，请重新加载后重试。"); }
            using (processLock)
            {
                var current = await ReadAsync(token);
                if (current.Generation != expectedGeneration || candidate.Generation != expectedGeneration)
                    throw new RewriteGenerationConflictException("改写库代次已变化，请重新加载后另存或重试。");
                RewriteLibraryValidation.Validate(candidate, token);
                foreach (var prior in current.Revisions)
                    if (!candidate.Revisions.Contains(prior)) throw new InvalidDataException("首版不允许删除或修改既有改写修订。");
                var committed = candidate with { Generation = checked(expectedGeneration + 1) };
                // Utf8JsonWriter 分段写入有界流。写入前检查，避免先产生无界序列化结果再拒绝。
                using var output = new LimitedOutput(); await JsonSerializer.SerializeAsync(output, committed, _json, token);
                temporary = Path.Combine(_directory, ".rewrites-" + Guid.NewGuid().ToString("N") + ".tmp");
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                { output.CopyToFile(file); await file.FlushAsync(token); file.Flush(true); }
                checkpoint?.Invoke("written"); token.ThrowIfCancellationRequested();
                checkpoint?.Invoke("before-commit"); token.ThrowIfCancellationRequested();
                if (File.Exists(Target)) File.Replace(temporary, Target, null); else File.Move(temporary, Target);
                temporary = null;
                // 此后返回提交事实；取消页面不会撤销磁盘已提交的修订，更不应自动激活它。
                checkpoint?.Invoke("committed"); return committed;
            }
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { /* 只可能遗留本次临时文件，不改原库。 */ }
            _gate.Release();
        }
    }
    private sealed class LimitedOutput : Stream
    {
        private readonly MemoryStream _inner = new();
        public void CopyToFile(Stream target) { _inner.Position = 0; _inner.CopyTo(target); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > RewriteLibraryValidation.MaximumBytes) throw new InvalidDataException("改写库超过8 MiB。");
            _inner.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
