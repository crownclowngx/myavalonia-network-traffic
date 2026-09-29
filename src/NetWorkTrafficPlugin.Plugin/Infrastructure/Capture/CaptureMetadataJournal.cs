using System.Text.Json;
using System.Threading.Channels;
using NetWorkTrafficPlugin.Application.Capture;

namespace NetWorkTrafficPlugin.Infrastructure.Capture;

/// <summary>
/// 元信息只追加到本次会话日志；后台单写入者维护最新位置。输入队列按序列化后的真实字节计费，
/// 满队列只降低元信息完整性，不等待网络回调。读取按偏移取一条，不把整个日志读进内存。
/// </summary>
internal sealed class CaptureMetadataJournal : IAsyncDisposable
{
    private readonly string _path;
    private readonly CaptureDiskBudget _budget;
    private readonly Channel<(Guid Id, byte[] Bytes)> _queue = Channel.CreateBounded<(Guid, byte[])>(128);
    private readonly Dictionary<Guid, (long Offset, int Length)> _index = [];
    private readonly object _sync = new();
    private readonly Task _writer;
    private long _length, _queued;
    private int _failed;
    public bool Failed => Volatile.Read(ref _failed) != 0;
    public CaptureMetadataJournal(string directory, CaptureDiskBudget budget)
    {
        _path = Path.Combine(directory, "records.jsonl"); _budget = budget;
        _writer = Task.Run(WriteAsync);
    }
    public bool TryAppend(TrafficRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        lock (_sync)
        {
            if (Failed || _length + _queued + bytes.Length + 1 > 64L << 20 || _queued + bytes.Length > 4 << 20) return false;
            var reserved = _budget.Reserve(bytes.Length + 1);
            if (reserved != bytes.Length + 1) { _budget.ReleaseReservation(reserved); return false; }
            if (!_queue.Writer.TryWrite((record.RecordId, bytes))) { _budget.ReleaseReservation(reserved); return false; }
            _queued += bytes.Length + 1; return true;
        }
    }
    private async Task WriteAsync()
    {
        try
        {
            await using var file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
            await foreach (var item in _queue.Reader.ReadAllAsync())
            {
                var offset = file.Position;
                await file.WriteAsync(item.Bytes); await file.WriteAsync(new byte[] { 10 }); await file.FlushAsync();
                lock (_sync)
                {
                    _index[item.Id] = (offset, item.Bytes.Length);
                    _length += item.Bytes.Length + 1; _queued -= item.Bytes.Length + 1;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref _failed, 1);
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) { }
            lock (_sync) _queued = 0;
        }
    }
    public async Task<TrafficRecord?> ReadAsync(Guid id, CancellationToken token)
    {
        (long Offset, int Length) position;
        lock (_sync) if (!_index.TryGetValue(id, out position)) return null;
        return await ReadAtAsync(position, token).ConfigureAwait(false);
    }
    public Dictionary<Guid, (long Offset, int Length)> Positions() { lock (_sync) return new(_index); }
    public async Task<TrafficRecord?> ReadAtAsync((long Offset, int Length) position, CancellationToken token)
    {
        await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        file.Position = position.Offset;
        var bytes = new byte[position.Length]; await file.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<TrafficRecord>(bytes);
    }
    public void Forget(Guid id) { lock (_sync) _index.Remove(id); }
    public async ValueTask DisposeAsync() { _queue.Writer.TryComplete(); await _writer.ConfigureAwait(false); }
}
