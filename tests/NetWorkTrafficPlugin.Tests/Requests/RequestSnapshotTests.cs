using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Requests;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Requests;

public sealed class RequestSnapshotTests
{
    private sealed class Source(TrafficRecord record, byte[] bytes) : ICaptureSession, ICaptureBodyStreamReader
    {
        public bool IsArchive => true;
        public int Leases;
        public CaptureSnapshot Snapshot() => new(record.SessionId, 1, false, 1, 0, [], null);
        public IDisposable AcquireReadLease() { Leases++; return new CaptureReadLease(() => Leases--); }
        public Task<CaptureLease> FreezeAsync(CancellationToken token) { var lease = AcquireReadLease(); return Task.FromResult(new CaptureLease(Snapshot(), [record], lease.Dispose)); }
        public Task<TrafficRecord?> ReadAsync(Guid id, CancellationToken token) => throw new InvalidOperationException("提取不允许重新读取另一代事实。");
        public Task<TrafficSummary[]> QueryAsync(CaptureQuery query, CancellationToken token) => Task.FromResult<TrafficSummary[]>([]);
        public Task<byte[]> ReadBodyAsync(BodyDescriptor body, int maximumBytes, CancellationToken token) => Task.FromResult(bytes.Take(maximumBytes).ToArray());
        public Task<Stream> OpenBodyAsync(BodyDescriptor body, CancellationToken token) { Assert.Equal(1, Leases); return Task.FromResult<Stream>(new MemoryStream(bytes, false)); }
        public StreamPreview? Stream(Guid id) => null;
    }
    [Fact]
    public async Task 冻结事实去协议头和身份值同时保留来源修订()
    {
        var bytes = "{\"message\":\"中文\"}"u8.ToArray(); var record = RequestComparisonTests.Record(bytes, []);
        record = record with { RequestHeaders = [new("Host", "old"), new("Authorization", "SECRET"), new("Connection", "X-Hop"), new("X-Hop", "remove"), new("X-Keep", "same")] };
        var source = new Source(record, bytes); var result = await RequestSnapshotFactory.ExtractWithSnapshotAsync(source, record.RecordId, null, default);
        Assert.Equal(0, source.Leases); Assert.Equal(record, result.Record); Assert.Equal(RequestSnapshotKind.LegacyObserved, result.Draft.Origin!.Kind);
        Assert.Equal(record.Revision, result.Draft.Origin.Revision); Assert.Equal(2, result.Draft.Headers.Length);
        Assert.DoesNotContain(result.Draft.Headers, h => h.Value == "SECRET");
        using var prepared = await new RequestPreparation(new()).PrepareAsync(result.Draft with { Variables = [new("secret_0", "replacement", true)] }, null, default);
        Assert.True(prepared.Sensitive);
        var literal = "{\"${captured}\":\"${captured}\"}"u8.ToArray(); var literalRecord = RequestComparisonTests.Record(literal, []);
        var extracted = await RequestSnapshotFactory.ExtractAsync(new Source(literalRecord, literal), literalRecord.RecordId, default);
        using var literalPreview = await new RequestPreparation(new()).PrepareAsync(extracted with { Variables = [new("captured", "UNEXPECTED")] }, null, default);
        Assert.Contains("${captured}", literalPreview.BodyPreview); Assert.DoesNotContain("UNEXPECTED", literalPreview.BodyPreview);
        var invalidForm = "x=%FF"u8.ToArray(); var formRecord = RequestComparisonTests.Record(invalidForm, [], "application/x-www-form-urlencoded");
        await Assert.ThrowsAsync<RequestFailure>(() => RequestSnapshotFactory.ExtractAsync(new Source(formRecord, invalidForm), formRecord.RecordId, default));
    }
    [Theory]
    [InlineData("unknown-empty")] [InlineData("partial")] [InlineData("mapped")] [InlineData("metadata")] [InlineData("encoded")]
    public async Task 不完整来源或不可靠有效快照阻止提取(string kind)
    {
        var record = RequestComparisonTests.Record([], []);
        record = kind switch
        {
            "unknown-empty" => record with { RequestBody = record.RequestBody with { ExpectedBytes = null, Capture = CaptureState.MetadataOnly } },
            "partial" => record with { RequestBody = record.RequestBody with { ObservedBytes = 10 } },
            "mapped" => record with { EffectiveUri = "https://example.test/other" },
            "metadata" => record with { MetadataPartial = true },
            _ => record with { RequestBody = record.RequestBody with { ContentEncoding = "gzip" } }
        };
        var source = new Source(record, []);
        await Assert.ThrowsAsync<RequestFailure>(() => RequestSnapshotFactory.ExtractAsync(source, record.RecordId, default)); Assert.Equal(0, source.Leases);
    }
    [Fact]
    public async Task 完整二进制大于编辑上限以文件资产提取且源流关闭后仍可读取()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var bytes = Enumerable.Range(0, 300000).Select(i => (byte)(i % 251)).ToArray();
            var record = RequestComparisonTests.Record(bytes, [], "application/octet-stream"); var source = new Source(record, bytes);
            using var bodies = new FileRequestBodyStore(root);
            var result = await RequestSnapshotFactory.ExtractWithSnapshotAsync(source, record.RecordId, bodies, default);
            Assert.Equal(RequestBodyKind.RawFile, result.Draft.BodyKind); Assert.Equal(0, source.Leases);
            using var lease = await bodies.AcquireAsync(result.Draft.BodyAssetId!.Value, default);
            await using var stream = await lease.OpenAsync(default); using var copied = new MemoryStream(); await stream.CopyToAsync(copied);
            Assert.Equal(bytes, copied.ToArray());
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
}
