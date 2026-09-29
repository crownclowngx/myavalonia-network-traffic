using System.Threading.Channels;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Infrastructure.Capture;

/// <summary>
/// 网络回调只执行有界复制，单写入者负责 I/O。第一次额度不足后永久停止该请求的正文记录，
/// 保证文件是连续前缀；观察字节仍持续累加，完整传输与完整记录分别表达。
/// </summary>
public sealed class BoundedBodyRecorder : IAsyncDisposable
{
    public sealed class Entry(Guid id, long? expected, CaptureMode mode, string? file)
    {
        internal readonly Guid Id = id;
        internal long Observed, Stored, Scheduled;
        internal long? Expected = expected;
        internal readonly CaptureMode Mode = mode;
        internal string? Reason;
        internal bool Failed, Truncated, Finished, Closed, Admitted;
        internal TransportState Transport = TransportState.Receiving;
        internal readonly string? File = file;
    }
    private sealed record Item(Entry Entry, byte[]? Bytes);
    private readonly object _sync = new();
    private readonly CaptureLimits _limits;
    private readonly IBodyRecordSink _sink;
    private readonly string? _directory;
    // 字节上限以外再限制块数，避免大量单字节块的对象开销突破实际内存预算。
    private readonly Channel<Item> _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(256) { SingleReader = true });
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _writer;
    private int _queuedBytes;
    private int _pendingEntries;
    private long _reserved, _observed, _stored;
    private Task? _dispose;
    private readonly List<Task> _completionWrites = [];

    public BoundedBodyRecorder(CaptureLimits limits, IBodyRecordSink sink, string? directory = null)
    {
        limits.Validate();
        (_limits, _sink, _directory) = (limits, sink, directory);
        _writer = Task.Run(WriteLoopAsync);
    }
    public Entry Begin(Guid id, long? expected, CaptureMode mode)
    {
        lock (_sync)
        {
            var admitted = mode == CaptureMode.BoundedBody && _pendingEntries < _limits.MaxActiveRequests && _dispose is null;
            var entry = new Entry(id, expected, mode, admitted && _directory is not null ? Path.Combine(_directory, id.ToString("N") + ".body") : null)
            { Admitted = admitted, Truncated = mode == CaptureMode.BoundedBody && !admitted };
            if (admitted) _pendingEntries++;
            else if (entry.Truncated) entry.Reason = "后台记录数量达到上限，仅保留元信息。";
            return entry;
        }
    }
    public void SetExpected(Entry entry, long? length) { lock (_sync) entry.Expected = length; }
    public (long Observed, long Stored, int Queued) Metrics { get { lock (_sync) return (_observed, _stored, _queuedBytes); } }

    public void Observe(Entry entry, ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            if (entry.Finished || _dispose is not null) return;
            entry.Observed += bytes.Length;
            _observed += bytes.Length;
            if (bytes.IsEmpty || entry.Mode != CaptureMode.BoundedBody || entry.Truncated || entry.Failed) return;
            var allowed = (int)Math.Min(bytes.Length, Math.Min(_limits.BodyBytes - entry.Scheduled, _limits.DiskBytes - _reserved));
            if (allowed > _limits.QueueBytes - _queuedBytes) allowed = 0;
            if (allowed > 0)
            {
                // 内存所有权转移给队列；从第三方借来的块绝不跨回调保存。
                var owned = bytes[..allowed].ToArray();
                if (_queue.Writer.TryWrite(new(entry, owned)))
                {
                    _queuedBytes += allowed;
                    _reserved += allowed;
                    entry.Scheduled += allowed;
                }
                else allowed = 0;
            }
            if (allowed < bytes.Length)
            {
                entry.Truncated = true;
                entry.Reason = "记录预算已耗尽，保留连续前缀。";
            }
        }
    }

    public void Complete(Entry entry, TransportState state)
    {
        lock (_sync)
        {
            if (entry.Finished) return;
            entry.Finished = true;
            entry.Transport = state;
            if (state == TransportState.Completed && entry.Expected is { } length && length != entry.Observed)
            {
                entry.Transport = TransportState.Failed;
                entry.Reason = "实际字节数与声明长度不一致。";
            }
            if (!entry.Admitted) entry.Closed = true;
            else if (_dispose is null && !_queue.Writer.TryWrite(new(entry, null)))
            {
                _completionWrites.RemoveAll(t => t.IsCompleted);
                _completionWrites.Add(QueueCompletionAsync(entry));
            }
        }
    }

    private async Task QueueCompletionAsync(Entry entry)
    {
        // 最多 MaxActiveRequests 个待完成记录，因此控制消息等待也有界；不阻塞网络回调。
        try { await _queue.Writer.WriteAsync(new(entry, null)).ConfigureAwait(false); }
        catch (ChannelClosedException) { lock (_sync) { entry.Failed = true; entry.Reason = "记录器关闭时正文未完全落盘。"; } }
    }

    public BodyCaptureSummary Snapshot(Entry entry)
    {
        lock (_sync)
        {
            var capture = entry.Mode != CaptureMode.BoundedBody ? CaptureState.MetadataOnly :
                entry.Failed ? CaptureState.Failed : entry.Closed && entry.Transport == TransportState.Completed &&
                !entry.Truncated && entry.Stored == entry.Observed ? CaptureState.Complete : CaptureState.Partial;
            return new(entry.Observed, entry.Stored, entry.Expected, capture, entry.Transport, entry.Reason, entry.File);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (item.Bytes is null)
                    {
                        await _sink.CompleteAsync(item.Entry.Id).ConfigureAwait(false);
                        lock (_sync) { item.Entry.Closed = true; _pendingEntries--; }
                    }
                    else if (!item.Entry.Failed)
                    {
                        await _sink.WriteAsync(item.Entry.Id, item.Bytes, _closing.Token).ConfigureAwait(false);
                        lock (_sync) { item.Entry.Stored += item.Bytes.Length; _stored += item.Bytes.Length; }
                    }
                }
                catch (Exception)
                {
                    // 不透传文件路径、底层异常正文或凭据；保留明确的失败完整性。
                    lock (_sync) { item.Entry.Failed = true; item.Entry.Reason = "正文记录写入失败。"; }
                }
                finally { if (item.Bytes is not null) lock (_sync) _queuedBytes -= item.Bytes.Length; }
            }
        }
        finally { await _sink.DisposeAsync().ConfigureAwait(false); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_dispose ??= Task.Run(CloseAsync));
    }
    private async Task CloseAsync()
    {
        _closing.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            Task[] pending;
            lock (_sync) pending = _completionWrites.ToArray();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            _queue.Writer.TryComplete();
            await _writer.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally { if (_writer.IsCompleted) _closing.Dispose(); }
    }
}
