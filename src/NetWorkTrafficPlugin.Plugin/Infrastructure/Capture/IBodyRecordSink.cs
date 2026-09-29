namespace NetWorkTrafficPlugin.Infrastructure.Capture;

/// <summary>连续前缀写入边界。测试可替换为慢盘/失败实现；只有后台写入者调用它。</summary>
public interface IBodyRecordSink : IAsyncDisposable
{
    ValueTask WriteAsync(Guid requestId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    ValueTask CompleteAsync(Guid requestId);
}

/// <summary>每次运行独立目录，只创建本次请求 GUID 文件；不截断或清理已有用户文件。</summary>
public sealed class FileBodyRecordSink(string directory) : IBodyRecordSink
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
