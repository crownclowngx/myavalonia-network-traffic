using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Application.Capture;

public enum BodyDirection { Request, Response }
public enum BodyRepresentation { EncodedEntity, DecodedEntity, Unknown }
public enum TrafficSource { Captured }
public sealed record HeaderField(string Name, string Value);
public sealed record CaptureFailure(string Stage, string Kind, string Message);

/// <summary>
/// 正文描述符保存事实，不持有流或第三方会话。网络成功、文件完整和预览完整是三个不同结论。
/// FilePath 只由记录器产生；存档读取会重新验证路径，不能直接信任外部 JSON 中的路径。
/// </summary>
public sealed record BodyDescriptor(Guid BodyId, BodyDirection Direction, BodyRepresentation Representation,
    long ObservedBytes, long CommittedBytes, long? ExpectedBytes, TransportState Transport,
    CaptureState Capture, string? Reason, string? FilePath, string? ContentType, string? ContentEncoding)
{
    public static BodyDescriptor Empty(Guid id, BodyDirection direction) =>
        new(id, direction, BodyRepresentation.EncodedEntity, 0, 0, null, TransportState.Receiving,
            CaptureState.Partial, null, null, null, null);
}

public sealed record TrafficRecord(Guid RecordId, Guid SessionId, long Sequence, long Revision,
    TrafficSource Source, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc, double ElapsedMilliseconds,
    string Method, string OriginalUri, string EffectiveUri, string ClientProtocol, string? OriginProtocol,
    int? StatusCode, HeaderField[] RequestHeaders, HeaderField[] ResponseHeaders,
    BodyDescriptor RequestBody, BodyDescriptor ResponseBody, bool MetadataPartial, CaptureFailure? Failure,
    Guid? OriginRecordId = null);

/// <summary>列表只使用短摘要，不为一万条记录各创建正文、JSON 树或图片；详情由 ID 按需读取。</summary>
public sealed record TrafficSummary(Guid RecordId, Guid SessionId, long Sequence, long Revision,
    DateTimeOffset StartedUtc, string Method, string Host, string Path, int? StatusCode, double ElapsedMilliseconds,
    long ObservedBytes, long StoredBytes, TransportState Transport, CaptureState Capture, bool MetadataPartial)
{
    public string StatusText => StatusCode?.ToString() ?? (Transport == TransportState.Receiving ? "接收中" : "无响应");
    public string SizeText => $"{ObservedBytes:N0} / {StoredBytes:N0} B";
    public string StateText => $"{Transport} / {Capture}" + (MetadataPartial ? " · 元信息不全" : "");
    public string TimingText => $"#{Sequence} · {StartedUtc.LocalDateTime:HH:mm:ss.fff} · {ElapsedMilliseconds:N0} ms · Captured";
    // 只为可读性还原路径里的中文；请求和导出继续使用原始 URI。换行等控制字符不进入双行摘要。
    public string DisplayPath => Uri.UnescapeDataString(Path).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    public string CompactTiming => $"{ElapsedMilliseconds:N0} ms";
    public string CompactSize => StoredBytes >= 1048576 ? $"已存 {StoredBytes / 1048576d:N1} MiB" :
        StoredBytes >= 1024 ? $"已存 {StoredBytes / 1024d:N1} KiB" : $"已存 {StoredBytes:N0} B";
    // 摘要字节来自双向合计。HTTP 状态与传输/保存事实分开，200 不能掩盖正文只有前缀。
    public string CompactState => (Transport switch
    {
        TransportState.Receiving => "接收中", TransportState.Failed => "传输失败",
        TransportState.Cancelled => "已取消", _ => ""
    }) + (Capture switch
    {
        CaptureState.Partial => " · 部分保存", CaptureState.MetadataOnly => " · 仅元信息",
        CaptureState.Failed => " · 保存失败", _ => ""
    }) + (MetadataPartial ? " · 元信息不全" : "");
}

public sealed record CaptureSnapshot(Guid SessionId, long Revision, bool IsRunning, long Total,
    long Evicted, TrafficSummary[] Records, string? Diagnostic);
public sealed record CaptureQuery(string Keyword = "", string[]? Methods = null, int? StatusClass = null,
    TransportState? Transport = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    long AfterSequence = 0, bool NewestFirst = true, TrafficSource? Source = null, int? StatusCode = null)
{
    public void Validate()
    {
        if (Keyword.Length > 1024 || StatusClass is < 1 or > 5 || StatusCode is < 100 or > 599 || From > To)
            throw new ArgumentException("筛选条件超出范围或开始时间晚于结束时间。");
    }
    public bool Matches(TrafficRecord record)
    {
        Validate();
        return (record.Sequence > AfterSequence || record.ResponseBody.Transport == TransportState.Receiving) &&
            (Methods is null || Methods.Length == 0 || Methods.Contains(record.Method, StringComparer.OrdinalIgnoreCase)) &&
            (StatusClass is null || record.StatusCode / 100 == StatusClass) &&
            (StatusCode is null || record.StatusCode == StatusCode) &&
            (Transport is null || record.ResponseBody.Transport == Transport) &&
            (Source is null || record.Source == Source) && (From is null || record.StartedUtc >= From) &&
            (To is null || record.StartedUtc < To) &&
            (Keyword.Length == 0 || record.EffectiveUri.Contains(Keyword, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>页面只获得只读捕获能力；筛选、加载与重开记录不能隐式启动代理或发送请求。</summary>
public interface ICaptureReader
{
    CaptureSnapshot Snapshot();
    Task<TrafficRecord?> ReadAsync(Guid recordId, CancellationToken token);
    Task<TrafficSummary[]> QueryAsync(CaptureQuery query, CancellationToken token);
}

public interface ICaptureSession : ICaptureReader
{
    IDisposable AcquireReadLease();
    Task<byte[]> ReadBodyAsync(BodyDescriptor body, int maximumBytes, CancellationToken token);
    NetWorkTrafficPlugin.Application.Content.StreamPreview? Stream(Guid recordId);
    Task<CaptureLease> FreezeAsync(CancellationToken token);
    bool IsArchive { get; }
}

/// <summary>租约只负责一次归还，不承担停止或删除会话的职责。</summary>
public sealed class CaptureReadLease(Action release) : IDisposable
{
    private Action? _release = release;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>快照拥有源文件读取租约；保存取消/失败同样需要归还，防止删除正在复制的文件。</summary>
public sealed class CaptureLease(CaptureSnapshot snapshot, TrafficRecord[] records, Action release) : IDisposable
{
    private Action? _release = release;
    public CaptureSnapshot Snapshot { get; } = snapshot;
    public TrafficRecord[] Records { get; } = records;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>每容器一个捕获入口，只发布当前只读数据源；页面关闭不拥有运行停止权。</summary>
public sealed class CaptureSessionStore
{
    public NetWorkTrafficPlugin.Application.Content.ContentMemoryBudget ContentBudget { get; } = new();
    private ICaptureSession? _current;
    public ICaptureSession? Current => Volatile.Read(ref _current);
    public void Publish(ICaptureSession session) => Volatile.Write(ref _current, session);
}
