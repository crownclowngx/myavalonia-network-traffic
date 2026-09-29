namespace NetWorkTrafficPlugin.Application.Proxy;

/// <summary>运行状态只由协调器发布；页面关闭不属于停止运行的原因。</summary>
public enum ProxyState { Stopped, Starting, Running, Stopping, Faulted, Disposed }
public enum CaptureMode { None, MetadataOnly, BoundedBody, Disk }
public enum CaptureProfile { M0, M1 }
public enum CaptureState { MetadataOnly, Partial, Complete, Failed }
public enum TransportState { Receiving, Completed, Cancelled, Failed }

/// <summary>
/// 首版公开协议边界。固定正式依赖不提供安全的 HTTP/2 单流主动取消入口，
/// 因此空闲只提示，不取消整条多路复用连接。页面在启动前就披露同一产品决定。
/// </summary>
public static class ProxyProtocolCapabilities
{
    public const string Http2IdleNotice = "HTTP/2 空闲流仅提示，不自动终止；可在客户端取消，或停止整个代理。需要自动空闲超时请使用 HTTP/1.1。";
}

/// <summary>探针保护预算，不是产品容量承诺。队列按字节计费，避免可变大小块绕过数量限制。</summary>
public sealed record CaptureLimits(
    int PreviewBytes = 64 * 1024,
    int BodyBytes = 1024 * 1024,
    int QueueBytes = 4 * 1024 * 1024,
    long DiskBytes = 64L * 1024 * 1024,
    int RetainedRequests = 1000,
    int MaxActiveRequests = 20,
    CaptureProfile Profile = CaptureProfile.M0)
{
    public static CaptureLimits M1(CaptureMode mode = CaptureMode.BoundedBody, long diskBytes = 1L << 30) =>
        new(BodyBytes: mode == CaptureMode.Disk ? 1 << 30 : 1 << 20, DiskBytes: diskBytes, RetainedRequests: 10000, Profile: CaptureProfile.M1);
    public void Validate()
    {
        if (!Enum.IsDefined(Profile) || PreviewBytes is < 0 or > 65536 || BodyBytes < 0 || BodyBytes > (Profile == CaptureProfile.M0 ? 1048576 : 1 << 30) ||
            QueueBytes is < 1 or > 4194304 || DiskBytes < 0 || DiskBytes > (Profile == CaptureProfile.M0 ? 67108864 : 2L << 30) ||
            RetainedRequests < 1 || RetainedRequests > (Profile == CaptureProfile.M0 ? 1000 : 10000) || MaxActiveRequests is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(CaptureLimits), "预算超出所选开发 profile 的边界。");
    }
}

/// <summary>只替换目标源，不修改方法、路径、查询和正文；条件头仅用于受控探针。</summary>
public sealed record RouteMapping(string SourceHost, string TargetOrigin, string? RequiredHeaderValue = null, long Revision = 1);

/// <summary>
/// 启动配置为不可变快照。上游测试根只包含公开 DER；不接受跳过证书验证的配置。
/// 记录目录由调用者指定，运行在该目录下建立自己的子目录，不扫描或清理用户文件。
/// </summary>
public sealed record ProxyOptions(
    int Port = 0,
    bool EnableHttp2 = true,
    CaptureMode Capture = CaptureMode.MetadataOnly,
    string? RecordDirectory = null,
    RouteMapping? Mapping = null,
    string? UpstreamTestRoot = null,
    CaptureLimits? Limits = null,
    int ResponseIdleSeconds = 30)
{
    public CaptureLimits EffectiveLimits => Limits ?? new();
    public void Validate()
    {
        if (Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (ResponseIdleSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(ResponseIdleSeconds));
        if (!Enum.IsDefined(Capture)) throw new ArgumentOutOfRangeException(nameof(Capture));
        EffectiveLimits.Validate();
        if (Capture is CaptureMode.BoundedBody or CaptureMode.Disk && string.IsNullOrWhiteSpace(RecordDirectory))
            throw new ArgumentException("正文记录需要独立的记录目录。");
        if (Capture == CaptureMode.Disk && EffectiveLimits.Profile != CaptureProfile.M1)
            throw new ArgumentException("受配额落盘只适用于 M1 配置。");
        if (Mapping is not null) RouteTargetResolver.Validate(Mapping);
    }
}

public sealed record ProxyRuntimeInfo(int Port, string PublicRootCertificate, bool Http2Enabled = false,
    string ProtocolPolicy = "HTTP/1.1");
public sealed record ProxyStatus(ProxyState State, long Generation, ProxyRuntimeInfo? Runtime = null, string? Failure = null);
public sealed record RouteDecision(string OriginalUri, string EffectiveUri, bool Matched, long Revision);
public sealed record BodyCaptureSummary(long ObservedBytes, long StoredBytes, long? ExpectedLength,
    CaptureState CaptureState, TransportState TransportState, string? Reason, string? FilePath);
public sealed record RequestObservation(Guid RequestId, string Source, string Method, RouteDecision Route,
    DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc, string ClientProtocol, string? OriginProtocol,
    int StatusCode, BodyCaptureSummary Body, string? Failure, long ClientConnectionId, long OriginConnectionId);
public sealed record ProxyMetrics(int ActiveRequests, long ReceivedBytes, long StoredBytes, int QueueBytes,
    long RejectedRequests, long EvictedObservations, IReadOnlyList<RequestObservation> Recent);

/// <summary>第三方代理的唯一 I/O 边界。停止与释放幂等，完成后不再拥有监听器或正文任务。</summary>
public interface IProxyRuntime : IAsyncDisposable
{
    Task<ProxyRuntimeInfo> StartAsync(ProxyOptions options, CancellationToken cancellationToken);
    Task StopAsync();
    ProxyMetrics GetMetrics();
}
