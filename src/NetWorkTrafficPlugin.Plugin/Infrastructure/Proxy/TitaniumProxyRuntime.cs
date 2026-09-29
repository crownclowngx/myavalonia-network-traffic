using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Application.Capture;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace NetWorkTrafficPlugin.Infrastructure.Proxy;

/// <summary>
/// 唯一接触 Titanium 的适配器。每次运行创建独立库实例、证书及记录器；请求携带不可变路由，
/// 不修改全局上游目的地。所有回调只做有界内存工作，磁盘由记录器后台完成。
/// </summary>
public sealed class TitaniumProxyRuntime(ILoggerFactory? diagnostics = null, CaptureSessionStore? captures = null) : IProxyRuntime
{
    private sealed class Observation(Guid id, string method, RouteDecision route, string protocol, long connection, BoundedBodyRecorder.Entry entry)
    {
        public readonly Guid Id = id;
        public readonly string Method = method, ClientProtocol = protocol;
        public readonly RouteDecision Route = route;
        public readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
        public readonly long Connection = connection;
        public readonly BoundedBodyRecorder.Entry Entry = entry;
        public DateTimeOffset? Completed;
        public string? OriginProtocol, Failure;
        public int StatusCode;
        public long OriginConnection;
        public SessionEventArgs? Session;
        public long LastBodyActivity;
        public bool ResponseStarted, IdleExpired, IdleUnsupportedReported;
    }
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Observation> _active = [];
    private readonly Queue<Observation> _recent = [];
    private ProxyServer? _server;
    private X509Certificate2? _root, _upstreamRoot;
    private BoundedBodyRecorder? _recorder;
    private ProxyOptions _options = new();
    private int _port;
    private bool _stopping;
    private long _rejected, _evicted;
    private Task? _stop, _dispose;
    private readonly CancellationTokenSource _idleCancellation = new();
    private Task? _idleTask;
    private LiveCaptureSession? _capture;
    private FileStream? _rootLease;
    public LiveCaptureSession? Capture => _capture;

    public Task<ProxyRuntimeInfo> StartAsync(ProxyOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        if (_server is not null || _dispose is not null) throw new InvalidOperationException("运行实例不能重复启动。");
        _options = options;
        _root = ProxyCertificates.CreateRoot();
        if (options.UpstreamTestRoot is not null)
            _upstreamRoot = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(options.UpstreamTestRoot));
        var directory = options.RecordDirectory is null ? null : Path.Combine(Path.GetFullPath(options.RecordDirectory), Guid.NewGuid().ToString("N"));
        CaptureDiskBudget? disk = null;
        if (options.EffectiveLimits.Profile == CaptureProfile.M1)
        {
            var root = Path.GetFullPath(options.RecordDirectory ?? throw new ArgumentException("M1 需要独立的会话目录。"));
            Directory.CreateDirectory(root);
            CaptureDiskBudget.EnsureRegular(root);
            CaptureDiskBudget.EnsureRegular(Path.Combine(root, ".capture.lock"));
            var marker = Path.Combine(root, ".network-traffic-root"); CaptureDiskBudget.EnsureRegular(marker);
            _rootLease = new FileStream(Path.Combine(root, ".capture.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(marker) && (new FileInfo(marker).Length > 16 || File.ReadAllText(marker) != "V3/M1")) throw new IOException("工作根所有权标记冲突。");
            if (!File.Exists(marker)) File.WriteAllText(marker, "V3/M1");
            disk = new(root, options.EffectiveLimits.DiskBytes);
        }
        _recorder = new(options.EffectiveLimits, new FileBodyRecordSink(directory ?? Path.GetTempPath(), disk is not null), directory, disk);
        if (disk is not null)
        {
            _capture = new(directory!, options.EffectiveLimits, _recorder, disk, captures?.ContentBudget);
            captures?.Publish(_capture);
        }
        // 库构造器默认允许安装用户根，这里必须显式关闭。根直接赋值，Start 不再读取或生成持久 PFX。
        var server = _server = new ProxyServer(null, null, userTrustRootCertificate: false, machineTrustRootCertificate: false)
        {
            ForwardToUpstreamGateway = false,
            NetworkFailureRetryAttempts = 0,
            // 7.0.13 的原生 h2 连接不能安全重定向到另一 authority。映射运行明确只协商 h1，
            // exact h2 客户端会失败；允许回退的客户端可用，实际策略随运行信息返回给界面。
            EnableHttp2 = options.EnableHttp2 && options.Mapping is null,
            MaxBufferedBodyBytes = 64 * 1024,
            MaxConcurrentClientConnections = options.EffectiveLimits.MaxActiveRequests + 4,
            ConnectTimeOutSeconds = 5,
            ResponseHeaderTimeoutSeconds = 10,
            // 7.0.13 的响应复制只创建一次 idle deadline，没有随 Body 块重置。
            // 禁用这个会截断活跃长流的计时器，由本适配器按最后数据活动做空闲检查。
            IdleReadTimeoutSeconds = 0,
            IdleWriteTimeoutSeconds = 30,
            RequestTimeoutSeconds = 0,
            EnableRequestTimingCapture = true
        };
        server.Logging.LoggerFactory = diagnostics ?? NullLoggerFactory.Instance;
        server.ApplyLoggingConfiguration();
        server.CertificateManager.RootCertificate = _root;
        server.CertificateManager.SaveFakeCertificates = false;
        server.CertificateManager.DisableWildCardCertificates = true;
        server.BeforeRequest += BeforeRequest;
        server.BeforeResponse += BeforeResponse;
        server.OnRequestBodyWrite += OnRequestBody;
        server.OnResponseBodyWrite += OnBody;
        server.AfterResponse += AfterResponse;
        if (_upstreamRoot is not null) server.ServerCertificateValidationCallback += ValidateOrigin;
        var endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, options.Port, true);
        server.AddEndPoint(endpoint);
        server.Start(changeSystemProxySettings: false);
        _port = endpoint.Port;
        _idleTask = Task.Run(CheckIdleResponsesAsync);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProxyRuntimeInfo(_port, Convert.ToBase64String(_root.Export(X509ContentType.Cert)), server.EnableHttp2,
            options.Mapping is not null ? "目标映射仅支持 HTTP/1.1；exact HTTP/2 请求会被拒绝。" : server.EnableHttp2 ? "HTTP/1.1 与 HTTP/2（无目标映射）" : "HTTP/1.1"));
    }

    private Task ValidateOrigin(object sender, CertificateValidationEventArgs args)
    {
        args.IsValid = ProxyCertificates.ValidateWithRoot(args.Certificate, args.SslPolicyErrors, _upstreamRoot!);
        return Task.CompletedTask;
    }

    private Task BeforeRequest(object sender, SessionEventArgs args)
    {
        lock (_sync)
        {
            if (_stopping || _active.Count >= _options.EffectiveLimits.MaxActiveRequests)
            {
                _rejected++;
                args.GenericResponse("代理已停止接纳或活动请求达到上限。", HttpStatusCode.ServiceUnavailable);
                return Task.CompletedTask;
            }
            var request = args.HttpClient.Request;
            RouteDecision route;
            try { route = RouteTargetResolver.Resolve(request.Url, _options.Mapping, request.Headers.GetFirstHeader("X-M0-Route")?.Value, _port); }
            catch (ArgumentException)
            {
                _rejected++;
                args.GenericResponse("目标映射无效或指向当前代理。", HttpStatusCode.BadGateway);
                return Task.CompletedTask;
            }
            if (route.Matched)
            {
                request.Url = route.EffectiveUri;
                request.Host = new Uri(route.EffectiveUri).Authority;
            }
            request.KeepBody = false;
            args.NetworkFailureRetryAttempts = 0;
            var id = Guid.NewGuid();
            var entry = _recorder!.Begin(id, null, _options.Capture);
            var observation = new Observation(id, request.Method, route, request.HttpVersion.ToString(), args.ClientConnectionId, entry);
            observation.Session = args;
            args.UserData = observation;
            _active.Add(id, observation);
            _capture?.Begin(id, request.Method, route, request.HttpVersion.ToString(),
                request.Headers.GetAllHeaders().Select(h => new HeaderField(h.Name, h.Value)).ToArray(),
                !request.HasBody ? 0 : request.ContentLength >= 0 ? request.ContentLength : null, _options.Capture, entry);
        }
        return Task.CompletedTask;
    }

    private Task BeforeResponse(object sender, SessionEventArgs args)
    {
        if (args.UserData is Observation observation)
        {
            var response = args.HttpClient.Response;
            response.KeepBody = false;
            _recorder!.SetExpected(observation.Entry, !response.HasBody || observation.Method == "HEAD" ? 0 :
                response.ContentLength >= 0 ? response.ContentLength : null);
            lock (_sync)
            {
                observation.StatusCode = response.StatusCode;
                observation.OriginProtocol = response.HttpVersion.ToString();
                observation.OriginConnection = args.ServerConnectionId;
                observation.ResponseStarted = true;
                observation.LastBodyActivity = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            _capture?.Response(observation.Id, response.StatusCode, response.HttpVersion.ToString(),
                response.Headers.GetAllHeaders().Select(h => new HeaderField(h.Name, h.Value)).ToArray());
        }
        return Task.CompletedTask;
    }

    private Task OnBody(object sender, BeforeBodyWriteEventArgs args)
    {
        if (args.Session.UserData is Observation observation)
        {
            lock (_sync) observation.LastBodyActivity = System.Diagnostics.Stopwatch.GetTimestamp();
            _recorder!.Observe(observation.Entry, args.BodyBytes);
            _capture?.ResponseBytes(observation.Id, args.BodyBytes);
        }
        return Task.CompletedTask;
    }

    private Task OnRequestBody(object sender, BeforeBodyWriteEventArgs args)
    {
        if (args.Session.UserData is Observation observation) _capture?.RequestBytes(observation.Id, args.BodyBytes);
        return Task.CompletedTask;
    }

    private Task AfterResponse(object sender, SessionEventArgs args)
    {
        if (args.UserData is Observation observation)
        {
            lock (_sync)
            {
                if (!_active.Remove(observation.Id)) return Task.CompletedTask;
                _recorder!.Complete(observation.Entry, _stopping ? TransportState.Cancelled :
                    args.Exception is null && !observation.IdleExpired ? TransportState.Completed : TransportState.Failed);
                observation.Completed = DateTimeOffset.UtcNow;
                observation.Failure = observation.IdleExpired ? "响应读取空闲超时。" :
                    args.Exception is null ? null : $"传输失败（{args.Exception.GetType().Name}）。";
                observation.Session = null; // 最近记录不能继续持有库会话/连接对象。
                _capture?.Complete(observation.Id, _recorder.Snapshot(observation.Entry).TransportState, observation.Failure);
                Retain(observation);
                // 固定库版本在 h1 短读后仍可能保留客户端连接。显式结束这个会话，
                // 让客户端立即看到不完整响应，不能一直等待声明长度。h2 不终止整条多流连接。
                if (observation.ClientProtocol == "1.1" && _recorder.Snapshot(observation.Entry).TransportState == TransportState.Failed)
                    args.TerminateSession();
            }
        }
        return Task.CompletedTask;
    }

    // 调用者持有 _sync；只保留有限元信息，停止时的部分记录也要可见，不能静默丢失。
    private void Retain(Observation observation)
    {
        if (_options.Capture == CaptureMode.None) return;
        _recent.Enqueue(observation);
        var keep = _capture is null ? _options.EffectiveLimits.RetainedRequests : 8;
        while (_recent.Count > keep) { _recent.Dequeue(); _evicted++; }
    }

    /// <summary>单个运行一个检查任务；只检查已经开始响应的会话，活跃长流不会被总时长终止。</summary>
    private async Task CheckIdleResponsesAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(_idleCancellation.Token).ConfigureAwait(false))
            {
                List<SessionEventArgs> expired = [];
                lock (_sync)
                {
                    foreach (var observation in _active.Values)
                        if (observation.ResponseStarted && !observation.IdleExpired && !observation.IdleUnsupportedReported && observation.Session is { } session &&
                            System.Diagnostics.Stopwatch.GetElapsedTime(observation.LastBodyActivity).TotalSeconds >= _options.ResponseIdleSeconds)
                        {
                            // 固定 7.0.13 的 TerminateSession 取消源属于连接，而非 h2 stream，且可能已经释放。
                            // 不能为结束一条沉默流破坏同连接其他请求。能力不足必须披露，不能把仍在传输的流伪装成 Failed。
                            if (observation.ClientProtocol == "2.0")
                            {
                                observation.IdleUnsupportedReported = true;
                                observation.Failure = ProxyProtocolCapabilities.Http2IdleNotice;
                                _capture?.ReportIssue(observation.Id, new("IdleRead", "UnsupportedH2Cancellation", observation.Failure));
                                continue;
                            }
                            observation.IdleExpired = true;
                            expired.Add(session);
                        }
                }
                // 取消可能同步触发库回调，必须离开状态锁后调用。
                foreach (var session in expired)
                    try { session.TerminateSession(); }
                    catch (ObjectDisposedException) { /* 检查与正常完成竞争：库已经释放的会话无需再次取消。 */ }
            }
        }
        catch (OperationCanceledException) when (_idleCancellation.IsCancellationRequested) { }
    }

    public ProxyMetrics GetMetrics()
    {
        lock (_sync)
        {
            var totals = _recorder?.Metrics ?? default;
            var items = _recent.Concat(_active.Values).Select(o => new RequestObservation(o.Id, "Captured", o.Method, o.Route,
                o.Started, o.Completed, o.ClientProtocol, o.OriginProtocol, o.StatusCode, _recorder!.Snapshot(o.Entry),
                o.Failure, o.Connection, o.OriginConnection)).ToArray();
            return new(_active.Count, totals.Observed, totals.Stored, totals.Queued, _rejected, _evicted, items);
        }
    }

    public Task StopAsync()
    {
        lock (_sync) { _stopping = true; return _stop ??= Task.Run(StopCoreAsync); }
    }
    private async Task StopCoreAsync()
    {
        try
        {
            _idleCancellation.Cancel();
            if (_idleTask is not null) await _idleTask.ConfigureAwait(false);
            if (_server?.ProxyRunning == true)
                await _server.StopAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                foreach (var observation in _active.Values)
                {
                    _recorder!.Complete(observation.Entry, TransportState.Cancelled);
                    observation.Completed = DateTimeOffset.UtcNow;
                    observation.Session = null;
                    _capture?.Complete(observation.Id, TransportState.Cancelled, "代理停止时仍在接收。");
                    Retain(observation);
                }
                _active.Clear();
            }
            try
            {
                if (_recorder is not null) await _recorder.DisposeAsync().ConfigureAwait(false);
                if (_capture is not null) await _capture.DisposeAsync().ConfigureAwait(false);
            }
            finally { await CloseServerConnectionsAsync().ConfigureAwait(false); }
        }
        if (_server is { ClientConnectionCount: > 0 }) throw new InvalidOperationException("代理仍有客户端连接未释放。");
    }

    private async Task CloseServerConnectionsAsync()
    {
        if (_server is null) return;
        _server.Dispose();
        // 7.0.13 的上游 Dispose 内部另起任务并延迟一秒关 socket。使用公开连接计数
        // 等待异步尾部；总退出仍受协调器十秒截止保护。计数更新略早于流关闭，留出尾部调度，
        // 真正的释放还由隔离进程句柄、端口重绑与文件独占打开共同验证。
        using var released = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (_server.ServerConnectionCount > 0)
            await Task.Delay(20, released.Token).ConfigureAwait(false);
        await Task.Delay(25, released.Token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_dispose ??= Task.Run(async () =>
        {
            try { await StopAsync().ConfigureAwait(false); }
            finally
            {
                if (_server is not null)
                {
                    _server.BeforeRequest -= BeforeRequest;
                    _server.BeforeResponse -= BeforeResponse;
                    _server.OnResponseBodyWrite -= OnBody;
                    _server.OnRequestBodyWrite -= OnRequestBody;
                    _server.AfterResponse -= AfterResponse;
                    _server.ServerCertificateValidationCallback -= ValidateOrigin;
                }
                _root?.Dispose();
                _upstreamRoot?.Dispose();
                _idleCancellation.Dispose();
                _rootLease?.Dispose();
            }
        }));
    }
}
