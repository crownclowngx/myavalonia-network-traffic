using System.Text;
using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Analysis;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Fixtures;

/// <summary>合成输入只负责控制已记录事实；语言和聚合始终调用生产实现，期望成员手工列出。</summary>
public sealed class M2Fixture : ICaptureSession
{
    public static readonly DateTimeOffset Anchor = DateTimeOffset.Parse("2026-09-30T12:30:00Z");
    public Guid SessionId { get; } = Guid.NewGuid();
    public List<TrafficRecord> Records { get; } = [];
    public Dictionary<Guid, byte[]> Bodies { get; } = [];
    public ContentMemoryBudget Memory { get; } = new();
    public bool IsArchive { get; set; }
    public int BodyReads { get; private set; }
    public int Leases { get; private set; }
    public Exception? ReadFailure { get; set; }
    public static string Program(string fields = "field user = first(original.query[\"user\"]);", string filter = "true", string group = "user", string window = "last(30m)", string columns = "user") =>
        $"dsl 1; {fields} view \"合成调用\" {{ where {filter}; within {window}; group by {group}; {(columns.Length == 0 ? "" : "columns " + columns + ";")} timeline by startedAt; }}";
    public static DslProgram Compile(string text)
    { var result = DslCompiler.Compile(text); Assert.True(result.Success, string.Join(";", result.Diagnostics.Select(d => d.Code + d.Message))); return result.Program!; }
    public TrafficRecord Add(string uri = "https://example.test/api?user=u1", string? body = null, string? contentType = null,
        HeaderField[]? headers = null, int minute = 1, ValueState bodyState = ValueState.Value, long? sequence = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body ?? ""); var requestId = Guid.NewGuid(); Bodies[requestId] = bytes;
        var request = new BodyDescriptor(requestId, BodyDirection.Request, BodyRepresentation.EncodedEntity, bytes.Length, bytes.Length, bytes.Length,
            bodyState == ValueState.NotReady ? TransportState.Receiving : TransportState.Completed,
            bodyState == ValueState.Unavailable ? CaptureState.Partial : CaptureState.Complete, null, "synthetic.bin", contentType, null);
        var response = request with { BodyId = Guid.NewGuid(), Direction = BodyDirection.Response, ContentType = "text/plain" };
        var r = new TrafficRecord(Guid.NewGuid(), SessionId, sequence ?? Records.Count + 1, 1, TrafficSource.Captured, Anchor.AddMinutes(-30 + minute), Anchor.AddMinutes(-30 + minute).AddMilliseconds(100),
            100, "GET", uri, uri, "1.1", "1.1", 200, headers ?? [], [], request, response, false, null);
        Records.Add(r); return r;
    }
    public async Task<FieldValue> Value(string expression, TrafficRecord record)
    {
        var p = Compile(Program($"field value = {expression};", group: "original.host", columns: "value"));
        using var reader = new RequestFieldReader(record, this, Memory, new(default));
        return await new FieldEvaluator(p, reader, new(default), default).FieldAsync("value");
    }
    public CaptureSnapshot Snapshot() => new(SessionId, 1, !IsArchive, Records.Count, 0, [], null);
    public Task<TrafficRecord?> ReadAsync(Guid id, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Records.FirstOrDefault(r => r.RecordId == id)); }
    public Task<TrafficSummary[]> QueryAsync(CaptureQuery query, CancellationToken token) => throw new NotSupportedException("M2不能依赖UI摘要查询。");
    public IDisposable AcquireReadLease() { Leases++; return new CaptureReadLease(() => Leases--); }
    public Task<byte[]> ReadBodyAsync(BodyDescriptor body, int maximumBytes, CancellationToken token)
    { token.ThrowIfCancellationRequested(); BodyReads++; if (ReadFailure is not null) throw ReadFailure; return Task.FromResult(Bodies[body.BodyId].Take((int)Math.Min(maximumBytes, body.CommittedBytes)).ToArray()); }
    public StreamPreview? Stream(Guid recordId) => null;
    public Task<CaptureLease> FreezeAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); var lease = AcquireReadLease(); return Task.FromResult(new CaptureLease(Snapshot(), Records.ToArray(), lease.Dispose, Anchor)); }
    public IAnalysisSource Source() => new CaptureAnalysisSource(this, Memory);
    public static void Evidence(string group, string id, object data) => ProbeEvidence.Write("m2-" + group, "V5-" + id, data);
}
