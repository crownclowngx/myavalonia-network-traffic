using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Capture;

namespace NetWorkTrafficPlugin.Infrastructure.Requests;

/// <summary>主动请求使用独立会话和唯一记录写入者，复用生产记录器与流解析，绝不覆盖正在抓包的Current。</summary>
public sealed class DebugCaptureStore : IDebugRecordWriter, IAsyncDisposable, IDisposable
{
    private readonly BoundedBodyRecorder _recorder;
    private readonly FileStream _owner;
    private readonly bool _deleteOnDispose;
    private readonly object _sync = new();
    private Task? _dispose;
    public LiveCaptureSession Session { get; }
    public CaptureSessionStore ViewStore { get; }
    public DebugCaptureStore(string root, ContentMemoryBudget memory, bool deleteOnDispose = true, ManagedDiskBudget? diskBudget = null)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root); CaptureDiskBudget.EnsureRegular(root);
        var lockPath = Path.Combine(root, ".debug.lock"); CaptureDiskBudget.EnsureRegular(lockPath);
        _owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); _deleteOnDispose = deleteOnDispose;
        try
        {
        // 仅清理当前独占根下、命名和所有权标记均匹配的平面会话。模板与显式存档从不进入这个根。
        foreach (var old in Directory.EnumerateDirectories(root, "debug-*"))
        {
            var name = Path.GetFileName(old);
            if (!Guid.TryParseExact(name[6..], "N", out _) || !File.Exists(Path.Combine(old, ".network-traffic-session"))) continue;
            CaptureDiskBudget.EnsureRegular(old);
            if (Directory.EnumerateDirectories(old).Any()) continue;
            foreach (var file in Directory.EnumerateFiles(old)) CaptureDiskBudget.EnsureRegular(file);
            foreach (var file in Directory.EnumerateFiles(old)) File.Delete(file);
            Directory.Delete(old);
        }
        ViewStore = new(memory);
        var limits = CaptureLimits.M1(); var directory = Path.Combine(root, "debug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var budget = new CaptureDiskBudget(root, limits.DiskBytes, shared: diskBudget);
        _recorder = new(limits, new FileBodyRecordSink(directory, true), directory, budget);
        Session = new(directory, limits, _recorder, budget, memory); ViewStore.Publish(Session);
        }
        catch { _owner.Dispose(); throw; }
    }
    public IDebugRequestRecord Begin(Guid attemptId, PreparedRequest request)
    {
        var response = _recorder.Begin(attemptId, null, CaptureMode.BoundedBody);
        var safeHeaders = request.Wire.Headers.Select(h => new HeaderField(h.Name,
            request.Sensitive || RequestValidation.IsSensitive(h.Name) ? "[敏感值未保留]" : h.Value)).ToList();
        if (request.Wire.MediaType is { } type) safeHeaders.Add(new("Content-Type", type));
        Session.Begin(attemptId, request.Wire.Method, new(request.SafeAddress, request.SafeAddress, false, 0), "1.1", safeHeaders.ToArray(),
            request.Wire.Body?.Length ?? 0, request.Sensitive ? CaptureMode.MetadataOnly : CaptureMode.BoundedBody, response,
            source: request.Origin is null ? TrafficSource.Manual : TrafficSource.Replay, originRecordId: request.Origin?.RecordId,
            metadataPartial: request.Sensitive || request.SafeAddress != request.Wire.Address.AbsoluteUri);
        return new Record(this, attemptId, response, request.Wire.Method);
    }
    private sealed class Record(DebugCaptureStore owner, Guid id, BoundedBodyRecorder.Entry response, string method) : IDebugRequestRecord
    {
        public void RequestBytes(ReadOnlyMemory<byte> bytes) => owner.Session.RequestBytes(id, bytes.Span);
        public void Headers(RequestTransportResponse value)
        {
            var headers = value.Headers.Select(h => h.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                ? new HeaderField(h.Name, "[敏感值未保留]") : h).ToArray();
            var length = value.Headers.FirstOrDefault(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))?.Value;
            var noBody = method == "HEAD" || value.StatusCode is 204 or 304;
            owner._recorder.SetExpected(response, noBody ? 0 : long.TryParse(length, out var size) ? size : null);
            owner.Session.Response(id, value.StatusCode, value.Protocol, headers);
        }
        public void ResponseBytes(ReadOnlyMemory<byte> bytes) { owner._recorder.Observe(response, bytes.Span); owner.Session.ResponseBytes(id, bytes.Span); }
        public void Complete(TransportState state, string? failure) { owner._recorder.Complete(response, state, failure); owner.Session.Complete(id, state, failure); }
    }
    public ValueTask DisposeAsync() { lock (_sync) return new(_dispose ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        try
        {
            await _recorder.DisposeAsync().ConfigureAwait(false); await Session.DisposeAsync().ConfigureAwait(false);
            if (_deleteOnDispose)
            {
                try { Session.DeleteStoppedSession(); }
                catch (InvalidOperationException) { /* 尚有外部只读租约时保留，下一次持根锁启动再清理，不删除正在保存的正文。 */ }
            }
        }
        finally { _owner.Dispose(); }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
