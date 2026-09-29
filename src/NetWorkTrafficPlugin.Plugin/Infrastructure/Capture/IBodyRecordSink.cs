namespace NetWorkTrafficPlugin.Infrastructure.Capture;

/// <summary>连续前缀写入边界。测试可替换为慢盘/失败实现；只有后台写入者调用它。</summary>
public interface IBodyRecordSink : IAsyncDisposable
{
    ValueTask WriteAsync(Guid requestId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    ValueTask CompleteAsync(Guid requestId);
}

/// <summary>每次运行独立目录，只创建本次请求 GUID 文件；不截断或清理已有用户文件。</summary>
public sealed class FileBodyRecordSink(string directory, bool publishWrites = false) : IBodyRecordSink
{
    private readonly Dictionary<Guid, FileStream> _files = [];
    public async ValueTask WriteAsync(Guid id, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (!_files.TryGetValue(id, out var file))
        {
            Directory.CreateDirectory(directory);
            file = new(Path.Combine(directory, id.ToString("N") + ".body"), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            _files.Add(id, file);
        }
        await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        // M1 的 CommittedBytes 用作实时读取与存档快照水位，必须先把 FileStream 缓冲交给文件。
        // M0 保持原路径；本操作不要求每块强制物理落盘，存档提交时另执行耐久 flush。
        if (publishWrites) await file.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask CompleteAsync(Guid id)
    {
        if (_files.Remove(id, out var stream)) await stream.DisposeAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var id in _files.Keys.ToArray()) await CompleteAsync(id).ConfigureAwait(false);
    }
}
