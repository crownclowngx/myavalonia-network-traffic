using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Application.Analysis;

public interface IAnalysisSource
{
    Guid InstanceId { get; }
    bool IsArchive { get; }
    Task<AnalysisSnapshot> FreezeAsync(CancellationToken token);
}

/// <summary>读取端口拥有快照租约；分析只获得当前记录提取器，无法启动代理或任意打开文件。</summary>
public abstract class AnalysisSnapshot(CaptureSnapshot metadata, IReadOnlyList<TrafficRecord> records, DateTimeOffset anchor) : IDisposable
{
    public CaptureSnapshot Metadata { get; } = metadata;
    public IReadOnlyList<TrafficRecord> Records { get; } = records;
    public DateTimeOffset Anchor { get; } = anchor;
    public virtual string Source(TrafficRecord record) => record.Source switch
    { TrafficSource.Captured => "capture", TrafficSource.Manual => "manual", TrafficSource.Replay => "replay", _ => "unknown" };
    public abstract IFieldSource OpenFields(TrafficRecord record, EvaluationBudget budget);
    public abstract void Dispose();
}
public sealed record AnalysisOptions(AnalysisWindow? Window = null, string[]? Sources = null, bool IncludeContext = false,
    bool SortByCount = false, string[]? Columns = null);
public sealed record Coverage(int Retained, int SourceExcluded, int OutsideWindow, int FilterFalse, int FilterUnknown, int FilterTrue,
    int GroupedRecords, int UnresolvedGroupRecords, long Total, long Evicted)
{
    public int Evaluated => FilterFalse + FilterUnknown + FilterTrue;
    public string Description => $"保留 {Retained} / 累计 {Total} / 淘汰 {Evicted}；来源排除 {SourceExcluded}，窗口外 {OutsideWindow}；条件否 {FilterFalse} / 未知 {FilterUnknown}；分组 {GroupedRecords} / 未确定 {UnresolvedGroupRecords}";
}
public sealed record AnalyzedRequest(Guid RecordId, long Sequence, DateTimeOffset StartedUtc, double ElapsedMilliseconds,
    string Method, string Host, string Path, int? StatusCode, TransportState Transport,
    IReadOnlyDictionary<string, FieldValue> Fields, string? Unresolved, bool IsContext, DateTimeOffset? CompletedUtc = null)
{
    public string Description => $"#{Sequence} {StartedUtc:HH:mm:ss.fff} {Method} {Path} · {StatusCode?.ToString() ?? "无响应"} · {Transport}";
    public bool Error => StatusCode >= 400 || Transport == TransportState.Failed;
    public string TimingWarning => CompletedUtc is { } end && double.IsFinite(ElapsedMilliseconds) &&
        Math.Abs((end - StartedUtc).TotalMilliseconds - ElapsedMilliseconds) > 1000
        ? "墙钟完成时间与单调耗时不一致；条带仍按已观察耗时显示。" : "";
}
public sealed record TrafficGroup(string Id, string Alias, IReadOnlyList<FieldValue> Key, IReadOnlyList<AnalyzedRequest> Requests)
{
    public int Count => Requests.Count;
    public int Interfaces => Requests.Select(r => (r.Method, r.Host, r.Path)).Distinct().Count();
    public int Errors => Requests.Count(r => r.Error);
    public int Receiving => Requests.Count(r => r.Transport == TransportState.Receiving);
    public int Cancelled => Requests.Count(r => r.Transport == TransportState.Cancelled);
    public DateTimeOffset FirstStarted => Requests[0].StartedUtc;
    public DateTimeOffset LastStarted => Requests[^1].StartedUtc;
    public string Description => $"{Alias} · {Count} 次 / {Interfaces} 个路径接口 · 错误 {Errors} / 接收中 {Receiving} / 取消 {Cancelled}";
}
public sealed class AnalysisResult(Guid analysisId, Guid sourceId, Guid sessionId, long snapshotRevision, Guid ruleRevisionId,
    string ruleHash, DateTimeOffset anchor, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<TrafficGroup> groups,
    IReadOnlyList<AnalyzedRequest> unresolved, IReadOnlyList<AnalyzedRequest> context, Coverage coverage,
    EvaluationBudget work, IDisposable reservation) : IDisposable
{
    private IDisposable? _reservation = reservation;
    public Guid AnalysisId { get; } = analysisId;
    public Guid SourceId { get; } = sourceId;
    public Guid SessionId { get; } = sessionId;
    public long SnapshotRevision { get; } = snapshotRevision;
    public Guid RuleRevisionId { get; } = ruleRevisionId;
    public string RuleHash { get; } = ruleHash;
    public DateTimeOffset Anchor { get; } = anchor;
    public DateTimeOffset From { get; } = from;
    public DateTimeOffset To { get; } = to;
    public IReadOnlyList<TrafficGroup> Groups { get; } = groups;
    public IReadOnlyList<AnalyzedRequest> Unresolved { get; } = unresolved;
    public IReadOnlyList<AnalyzedRequest> Context { get; } = context;
    public Coverage Coverage { get; } = coverage;
    public long Operations { get; } = work.Operations;
    public long EncodedBytes { get; } = work.EncodedBytes;
    public long DecodedBytes { get; } = work.DecodedBytes;
    public void Dispose() => Interlocked.Exchange(ref _reservation, null)?.Dispose();
}

/// <summary>两个并发名额和全部页面结果共用额度；没有后台等待队列，不把每页限额叠成无界总量。</summary>
public sealed class AnalysisResources
{
    private readonly SemaphoreSlim _slots = new(2, 2);
    public ContentMemoryBudget Memory { get; } = new(DslLimits.AnalysisBytes);
    public int Active => 2 - _slots.CurrentCount;
    public IDisposable Enter() => _slots.Wait(0) ? new CaptureReadLease(() => _slots.Release()) : throw new AnalysisLimitException("已有两项分析，请稍后重试。");
}

internal sealed class AnalysisReservations(ContentMemoryBudget memory) : IDisposable
{
    private readonly List<IDisposable> _leases = [];
    public void Add(long bytes) => _leases.Add(memory.TryReserve(bytes) ?? throw new AnalysisLimitException("分析结果总额度不足；可以清除旧结果后重算。"));
    public void Dispose() { foreach (var lease in _leases) lease.Dispose(); _leases.Clear(); }
}
