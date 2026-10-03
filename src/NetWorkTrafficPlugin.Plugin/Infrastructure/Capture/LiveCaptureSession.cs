using System.Diagnostics;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Infrastructure.Capture;

/// <summary>
/// 单次运行的捕获事实与持久日志适配。第三方对象不进入本类；网络传来的头已是不可变副本。
/// 活动记录最多二十条，已结束记录保留短元信息；完整头缓存按字节淘汰，再按日志偏移读取。
/// 正文仍由共享记录器落盘，预览与正文磁盘额度互不等待。
/// </summary>
public sealed class LiveCaptureSession : ICaptureSession, ICaptureBodyStreamReader, IAsyncDisposable
{
    private sealed class Entry(TrafficRecord record, BoundedBodyRecorder.Entry request, BoundedBodyRecorder.Entry response)
    {
        public TrafficRecord Record = record;
        public readonly BoundedBodyRecorder.Entry Request = request, Response = response;
        public readonly long Started = Stopwatch.GetTimestamp();
        public bool Finished, RequiresRequestTerminator, RequestTerminatorObserved;
        public long Cost;
    }
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Queue<Guid> _finished = [];
    private readonly Dictionary<Guid, TrafficRecord> _details = [];
    private readonly Queue<Guid> _detailOrder = [];
    private readonly BoundedBodyRecorder _recorder;
    private readonly CaptureMetadataJournal _journal;
    private readonly BoundedStreamObserver _streams;
    private readonly CaptureLimits _limits;
    private readonly string _directory;
    private long _sequence, _revision, _evicted, _summaryBytes, _detailBytes;
    private int _leases;
    private bool _running = true, _deleted;
    private string? _diagnostic;
    public Guid SessionId { get; } = Guid.NewGuid();
    public bool IsArchive => false;
    public string DirectoryPath => _directory;
    public LiveCaptureSession(string directory, CaptureLimits limits, BoundedBodyRecorder recorder, CaptureDiskBudget budget, ContentMemoryBudget? contentBudget = null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".network-traffic-session"), SessionId.ToString());
        _directory = directory; _limits = limits; _recorder = recorder;
        _streams = new(contentBudget ?? new());
        _journal = new(directory, budget);
    }
    public Guid Begin(Guid id, string method, RouteDecision route, string protocol, HeaderField[] headers, long? expected,
        CaptureMode mode, BoundedBodyRecorder.Entry response, bool requiresRequestTerminator = false,
        TrafficSource source = TrafficSource.Captured, Guid? originRecordId = null, bool metadataPartial = false)
    {
        lock (_sync)
        {
            var requestId = Guid.NewGuid();
            var request = _recorder.Begin(requestId, expected, mode);
            var bounded = BoundHeaders(headers, out var partial);
            partial |= metadataPartial;
            var record = new TrafficRecord(id, SessionId, ++_sequence, ++_revision, source,
                DateTimeOffset.UtcNow, null, 0, method, route.OriginalUri, route.EffectiveUri, protocol, null, null,
                bounded, [], BodyDescriptor.Empty(requestId, BodyDirection.Request) with
                { ContentType = Header(bounded, "Content-Type"), ContentEncoding = Header(bounded, "Content-Encoding") },
                BodyDescriptor.Empty(id, BodyDirection.Response), partial, null, originRecordId);
            var entry = new Entry(record, request, response) { Cost = 512 + 2L * (route.OriginalUri.Length + route.EffectiveUri.Length), RequiresRequestTerminator = requiresRequestTerminator };
            _entries.Add(id, entry); _summaryBytes += entry.Cost;
            if (!_journal.TryAppend(Current(entry))) entry.Record = record with { MetadataPartial = true };
            return requestId;
        }
    }
    public void Response(Guid id, int code, string protocol, HeaderField[] headers)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var e) || e.Finished) return;
            var bounded = BoundHeaders(headers, out var partial);
            var mime = Header(bounded, "Content-Type"); var encoding = Header(bounded, "Content-Encoding");
            e.Record = e.Record with { StatusCode = code, OriginProtocol = protocol, ResponseHeaders = bounded,
                MetadataPartial = e.Record.MetadataPartial || partial, Revision = ++_revision,
                ResponseBody = e.Record.ResponseBody with { ContentType = mime, ContentEncoding = encoding } };
            // 编码流不能把压缩字节直接当文本。压缩响应保留文件，详情通过有限解压读取。
            if (string.IsNullOrEmpty(encoding) || encoding == "identity")
            {
                var type = mime?.Split(';')[0].Trim();
                if (type is "text/event-stream" or "application/x-ndjson" or "application/ndjson")
                    _streams.Register(id, type != "text/event-stream");
            }
            // 响应头不保证未知长度上传完整（客户端中断时上游也可能发错误响应）。
            // 只有声明长度已全部观察到才提前结束请求方向；其余等待会话权威终态。
            var request = _recorder.Snapshot(e.Request);
            if (request.ExpectedLength is { } length && request.ObservedBytes == length)
                _recorder.Complete(e.Request, TransportState.Completed);
        }
    }
    public void RequestBytes(Guid id, ReadOnlySpan<byte> bytes, bool isLastChunk = false)
    {
        BoundedBodyRecorder.Entry? request;
        lock (_sync)
        {
            request = _entries.TryGetValue(id, out var e) && !e.Finished ? e.Request : null;
            if (request is not null && isLastChunk) e!.RequestTerminatorObserved = true;
        }
        if (request is not null) _recorder.Observe(request, bytes);
    }
    public void ResponseBytes(Guid id, ReadOnlySpan<byte> bytes)
    {
        _streams.Observe(id, bytes);
    }
    public void ReportIssue(Guid id, CaptureFailure failure)
    {
        lock (_sync) if (_entries.TryGetValue(id, out var e) && !e.Finished)
            e.Record = e.Record with { Failure = failure, Revision = ++_revision };
    }
    public void Complete(Guid id, TransportState state, string? failure)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var e) || e.Finished) return;
            // 固定库在 h1 chunked 的块边界遇到客户端 EOF 时可能向上游补写结束块，
            // 即使客户端没有发送 0 块也产生成功响应。上游成功不能证明原始上传完整；
            // 只采信公开 BodyWrite 回调观测到的真实末块，不伪造预期长度或修改请求内容。
            var missingTerminator = e.RequiresRequestTerminator && !e.RequestTerminatorObserved;
            const string framingReason = "未观察到 chunked 请求结束块，仅保留已收到的前缀；客户端可能提前断开。";
            e.Finished = true;
            _recorder.Complete(e.Request, missingTerminator && state == TransportState.Completed ? TransportState.Failed : state,
                missingTerminator ? framingReason : null);
            _streams.Complete(id, state == TransportState.Completed);
            e.Record = e.Record with { CompletedUtc = DateTimeOffset.UtcNow, ElapsedMilliseconds = Stopwatch.GetElapsedTime(e.Started).TotalMilliseconds,
                Failure = missingTerminator ? new("RequestBody", "IncompleteFraming", framingReason) :
                    failure is null ? null : new("Transport", state.ToString(), failure), Revision = ++_revision };
            _finished.Enqueue(id); CacheAndJournal(e); Evict();
        }
    }
    private void CacheAndJournal(Entry e)
    {
        var record = Current(e);
        if (!_journal.TryAppend(record)) record = record with { MetadataPartial = true };
        _details[record.RecordId] = record; _detailOrder.Enqueue(record.RecordId);
        _detailBytes += HeaderCost(record);
        // 已结束记录只在有限缓存里持有完整头；摘要中的 URI 有独立总额。
        e.Record = record with { RequestHeaders = [], ResponseHeaders = [] };
        while (_detailOrder.Count > 128 || _detailBytes > 16 << 20)
            if (_details.Remove(_detailOrder.Dequeue(), out var old)) _detailBytes -= HeaderCost(old);
    }
    private static long HeaderCost(TrafficRecord record) => record.RequestHeaders.Concat(record.ResponseHeaders).Sum(h => 64L + (h.Name.Length + h.Value.Length) * 2L);
    private void Evict()
    {
        while ((_finished.Count > _limits.RetainedRequests || _summaryBytes > 16 << 20) && _finished.TryDequeue(out var id))
        {
            if (!_entries.Remove(id, out var old)) continue;
            _summaryBytes -= old.Cost; _journal.Forget(id); _evicted++;
            if (_details.Remove(id, out var cached)) _detailBytes -= HeaderCost(cached);
            // 不在网络回调里删除磁盘文件；文件继续计入根配额，可在停止后的独立清理操作回收。
        }
    }
    private TrafficRecord Current(Entry entry)
    {
        var request = _recorder.Snapshot(entry.Request); var response = _recorder.Snapshot(entry.Response);
        return entry.Record with { RequestBody = Body(entry.Record.RequestBody, request), ResponseBody = Body(entry.Record.ResponseBody, response),
            ElapsedMilliseconds = entry.Record.CompletedUtc is null ? Stopwatch.GetElapsedTime(entry.Started).TotalMilliseconds : entry.Record.ElapsedMilliseconds };
    }
    private static BodyDescriptor Body(BodyDescriptor body, BodyCaptureSummary state) => body with
    { ObservedBytes = state.ObservedBytes, CommittedBytes = state.StoredBytes, ExpectedBytes = state.ExpectedLength,
        Transport = state.TransportState, Capture = state.CaptureState, Reason = state.Reason, FilePath = state.FilePath };
    public static TrafficSummary Summary(TrafficRecord record)
    {
        Uri.TryCreate(record.EffectiveUri, UriKind.Absolute, out var uri);
        var path = uri?.AbsolutePath ?? record.EffectiveUri;
        return new(record.RecordId, record.SessionId, record.Sequence, record.Revision, record.StartedUtc, record.Method,
            uri?.Host ?? "未知", path.Length > 180 ? path[..180] + "…" : path, record.StatusCode, record.ElapsedMilliseconds,
            record.RequestBody.ObservedBytes + record.ResponseBody.ObservedBytes, record.RequestBody.CommittedBytes + record.ResponseBody.CommittedBytes,
            record.ResponseBody.Transport, record.ResponseBody.Capture, record.MetadataPartial, record.Source);
    }
    public CaptureSnapshot Snapshot()
    {
        lock (_sync) return new(SessionId, _revision, _running, _sequence, _evicted,
            _entries.Values.Select(e => Summary(Current(e))).OrderByDescending(e => e.Sequence).ToArray(),
            _journal.Failed ? "元信息写入失败，部分内容只能在本次运行查看。" : _diagnostic);
    }
    public async Task<TrafficRecord?> ReadAsync(Guid id, CancellationToken token)
    {
        using var lease = AcquireReadLease();
        token.ThrowIfCancellationRequested();
        TrafficRecord current;
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry)) return null;
            current = Current(entry);
            if (!entry.Finished) return current;
            if (_details.TryGetValue(id, out var cached)) return current with { RequestHeaders = cached.RequestHeaders, ResponseHeaders = cached.ResponseHeaders };
        }
        var persisted = await _journal.ReadAsync(id, token).ConfigureAwait(false);
        return persisted is null ? current with { MetadataPartial = true } : current with { RequestHeaders = persisted.RequestHeaders, ResponseHeaders = persisted.ResponseHeaders };
    }
    public Task<TrafficSummary[]> QueryAsync(CaptureQuery query, CancellationToken token)
    {
        query.Validate();
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            TrafficRecord[] records;
            lock (_sync) records = _entries.Values.Select(Current).ToArray();
            var matches = records.Where(r => { token.ThrowIfCancellationRequested(); return query.Matches(r); });
            return (query.NewestFirst ? matches.OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.Sequence) : matches.OrderBy(r => r.StartedUtc).ThenBy(r => r.Sequence)).Select(Summary).ToArray();
        }, token);
    }
    public StreamPreview? Stream(Guid id) => _streams.Snapshot(id);
    public IDisposable AcquireReadLease()
    {
        lock (_sync)
        {
            if (_deleted) throw new IOException("会话已删除。");
            _leases++;
            return new CaptureReadLease(() => { lock (_sync) _leases--; });
        }
    }
    public async Task<byte[]> ReadBodyAsync(BodyDescriptor body, int maximumBytes, CancellationToken token)
    {
        using var lease = AcquireReadLease();
        return await CaptureBodyReader.ReadAsync(_directory, body, maximumBytes, token).ConfigureAwait(false);
    }
    public Task<Stream> OpenBodyAsync(BodyDescriptor body, CancellationToken token) => CaptureBodyReader.OpenAsync(_directory, body, token);
    public async Task<CaptureLease> FreezeAsync(CancellationToken token)
    {
        var lease = AcquireReadLease();
        TrafficRecord[] frozen; CaptureSnapshot snapshot; DateTimeOffset frozenUtc;
        // 在同一锁下固定头缓存和日志位置；后续摘要淘汰不会使已冻结的记录消失。
        Dictionary<Guid, (long Offset, int Length)> positions;
        lock (_sync)
        {
            frozenUtc = DateTimeOffset.UtcNow;
            frozen = _entries.Values.Select(e => _details.TryGetValue(e.Record.RecordId, out var cached)
                ? Current(e) with { RequestHeaders = cached.RequestHeaders, ResponseHeaders = cached.ResponseHeaders } : Current(e)).ToArray();
            snapshot = Snapshot(); positions = _journal.Positions();
        }
        try
        {
            for (var i = 0; i < frozen.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (frozen[i].RequestHeaders.Length != 0 || frozen[i].ResponseHeaders.Length != 0) continue;
                var details = positions.TryGetValue(frozen[i].RecordId, out var position) ? await _journal.ReadAtAsync(position, token) : null;
                // 正文和终态必须保留冻结时事实；这里只补不可变头信息，不接纳读取期间新提交的字节。
                if (details is not null) frozen[i] = frozen[i] with { RequestHeaders = details.RequestHeaders, ResponseHeaders = details.ResponseHeaders };
                else frozen[i] = frozen[i] with { MetadataPartial = true };
            }
            return new(snapshot, frozen, lease.Dispose, frozenUtc);
        }
        catch { lease.Dispose(); throw; }
    }
    public void DeleteStoppedSession()
    {
        lock (_sync)
        {
            if (_running || _leases != 0) throw new InvalidOperationException("会话正在运行或被保存读取，不能删除。");
            CaptureDiskBudget.EnsureRegular(_directory);
            foreach (var file in Directory.EnumerateFiles(_directory)) CaptureDiskBudget.EnsureRegular(file);
            // 目录由构造函数创建并带所有权标记；只允许平面会话文件，不递归跟随未知子目录。
            if (!File.Exists(Path.Combine(_directory, ".network-traffic-session"))) throw new IOException("缺少会话所有权标记。");
            foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory); _deleted = true; _entries.Clear(); _details.Clear(); _diagnostic = "会话已删除。"; _revision++;
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (!_running) return; _running = false;
            foreach (var entry in _entries.Values.ToArray()) if (!entry.Finished) Complete(entry.Record.RecordId, TransportState.Cancelled, "代理停止时仍在接收。");
            // 记录器先关闭之后重写最终描述符，避免日志把已成功 flush 的正文永远记成 Partial。
            foreach (var entry in _entries.Values)
                if (_details.TryGetValue(entry.Record.RecordId, out var cached))
                    _journal.TryAppend(Current(entry) with { RequestHeaders = cached.RequestHeaders, ResponseHeaders = cached.ResponseHeaders });
            _revision++;
        }
        await _streams.DisposeAsync().ConfigureAwait(false);
        await _journal.DisposeAsync().ConfigureAwait(false);
    }
    public static string? Header(IEnumerable<HeaderField> headers, string name) => headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
    private static HeaderField[] BoundHeaders(HeaderField[] headers, out bool partial)
    {
        var result = new List<HeaderField>(); long cost = 0;
        foreach (var h in headers)
        {
            cost += 64L + 2L * (h.Name.Length + h.Value.Length);
            if (result.Count == 256 || cost > 65536) break;
            result.Add(h);
        }
        partial = result.Count != headers.Length; return result.ToArray();
    }
}

internal static class CaptureBodyReader
{
    public static Task<Stream> OpenAsync(string root, BodyDescriptor body, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (body.FilePath is null) throw new IOException("完整正文文件不存在。");
        var path = Path.GetFullPath(body.FilePath);
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("正文路径超出会话。");
        CaptureDiskBudget.EnsureRegular(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != body.CommittedBytes) { file.Dispose(); throw new IOException("完整正文长度与冻结事实不一致。"); }
        return Task.FromResult<Stream>(file);
    }
    public static async Task<byte[]> ReadAsync(string root, BodyDescriptor body, int maximumBytes, CancellationToken token)
    {
        if (maximumBytes is < 0 or > 4194304) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (body.CommittedBytes == 0 || body.FilePath is null) return [];
        var path = Path.GetFullPath(body.FilePath);
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("正文路径超出会话。");
        CaptureDiskBudget.EnsureRegular(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous);
        var result = new byte[(int)Math.Min(maximumBytes, body.CommittedBytes)];
        await file.ReadExactlyAsync(result, token).ConfigureAwait(false); return result;
    }
}
