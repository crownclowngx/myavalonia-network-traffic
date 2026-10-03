using System.Net;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Requests;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Requests;

[Collection("M4Resources")]
public sealed class RequestExecutionTests
{
    private sealed class FastTimeoutClock : TimeProvider
    {
        // 只加速执行器的定时器；测试验证30/60秒两种期限的选择，真实流取消由回环集成探针另行证明。
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : dueTime / 1000, period);
    }
    private sealed class SlowBody : MemoryStream
    {
        private bool _first = true;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_first) { _first = false; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0;
        }
    }
    private sealed class HeadersThenWait : IRequestTransport
    {
        public Task<RequestTransportResponse> SendAsync(RequestWireInput request, CancellationToken token, Action<ReadOnlyMemory<byte>>? observe = null) =>
            Task.FromResult(new RequestTransportResponse(206, "1.1", [], new SlowBody(), new CaptureReadLease(() => { })));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 收头与正文空闲期限进入超时并保留已知状态(bool body)
    {
        var memory = new ContentMemoryBudget(); var record = new RecordSpy();
        await using var owner = new RequestExecutionCoordinator(body ? new HeadersThenWait() : new BlockingTransport(), record, new FastTimeoutClock());
        var draft = RequestPreparationTests.Draft(); using var prepared = await new RequestPreparation(memory).PrepareAsync(draft, null, default);
        var attempt = owner.Start(prepared, draft.Id, draft.Revision, null); await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RequestExecutionState.TimedOut, attempt.Snapshot.State); Assert.Equal(body ? 206 : (int?)null, attempt.Snapshot.StatusCode);
        Assert.Equal(body ? 1 : 0, attempt.Snapshot.ResponseBytes); Assert.Equal(0, memory.Reserved); Assert.Equal(1, record.Completes);
    }
    private sealed class FailedHistory : IRequestHistoryStore
    {
        public Task WriteAsync(RequestHistoryEntry entry, CancellationToken token) => throw new UnauthorizedAccessException("synthetic");
        public Task<IReadOnlyList<RequestHistoryEntry>> ReadAsync(CancellationToken token) => throw new NotSupportedException();
    }
    [Fact]
    public async Task 发送意图持久化失败保持未发送并释放后台所有权()
    {
        var memory = new ContentMemoryBudget(); var transport = new BlockingTransport();
        await using var owner = new RequestExecutionCoordinator(transport, new RecordSpy(), history: new FailedHistory());
        var draft = RequestPreparationTests.Draft(); using var prepared = await new RequestPreparation(memory).PrepareAsync(draft, null, default);
        var attempt = owner.Start(prepared, draft.Id, draft.Revision, null); await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(RequestExecutionState.Failed, attempt.Snapshot.State); Assert.Equal(RequestDispatchState.NotSent, attempt.Snapshot.Dispatch);
        Assert.Equal(0, transport.Sends); Assert.Equal(0, owner.Active); Assert.Equal(0, memory.Reserved);
    }
    private sealed class RecordSpy : IDebugRecordWriter, IDebugRequestRecord
    {
        public int Begins, Completes; public long Bytes;
        public IDebugRequestRecord Begin(Guid id, PreparedRequest request) { Begins++; return this; }
        public void RequestBytes(ReadOnlyMemory<byte> bytes) { }
        public void Headers(RequestTransportResponse response) { }
        public void ResponseBytes(ReadOnlyMemory<byte> bytes) => Bytes += bytes.Length;
        public void Complete(TransportState state, string? failure) => Completes++;
    }
    private sealed class BlockingTransport : IRequestTransport
    {
        public int Sends;
        public async Task<RequestTransportResponse> SendAsync(RequestWireInput request, CancellationToken token, Action<ReadOnlyMemory<byte>>? requestBytes = null)
        { Interlocked.Increment(ref Sends); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }
    }
    private sealed class Clock : TimeProvider
    {
        public long Value; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Value;
    }
    [Fact]
    public async Task 同令牌不能重复接纳取消后释放真实任务和预算()
    {
        var memory = new ContentMemoryBudget(); var transport = new BlockingTransport(); var records = new RecordSpy();
        await using var owner = new RequestExecutionCoordinator(transport, records); var draft = RequestPreparationTests.Draft();
        using var prepared = await new RequestPreparation(memory).PrepareAsync(draft, null, default);
        var attempt = owner.Start(prepared, draft.Id, draft.Revision, null);
        Assert.Throws<RequestFailure>(() => owner.Start(prepared, draft.Id, draft.Revision, null));
        prepared.Dispose(); Assert.True(memory.Reserved > 0); // 页面失效不清除在途正文或内存。
        attempt.Cancel(); await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, transport.Sends); Assert.Equal(1, records.Begins); Assert.Equal(1, records.Completes);
        Assert.Equal(RequestExecutionState.Cancelled, attempt.Snapshot.State); Assert.Contains("未知", attempt.Snapshot.Message);
        Assert.Equal(0, memory.Reserved); Assert.Equal(0, owner.Active);
    }
    [Fact]
    public async Task 四个并发独立意图允许但第五个立即拒绝无后台排队()
    {
        var transport = new BlockingTransport(); var memory = new ContentMemoryBudget();
        await using var owner = new RequestExecutionCoordinator(transport, new RecordSpy());
        var preparation = new RequestPreparation(memory); var attempts = new List<RequestAttempt>();
        for (var i = 0; i < 4; i++)
        {
            var draft = RequestPreparationTests.Draft(); var prepared = await preparation.PrepareAsync(draft, null, default);
            attempts.Add(owner.Start(prepared, draft.Id, draft.Revision, null));
        }
        var fifth = RequestPreparationTests.Draft(); using var next = await new RequestPreparation(memory).PrepareAsync(fifth, null, default);
        Assert.Throws<RequestFailure>(() => owner.Start(next, fifth.Id, fifth.Revision, null)); Assert.Equal(4, transport.Sends);
        await owner.DisposeAsync(); Assert.All(attempts, a => Assert.True(a.Completion.IsCompletedSuccessfully));
        next.Dispose(); Assert.Equal(0, memory.Reserved);
    }
    [Fact]
    public async Task 预览失效环境变化和期限都在发送前拒绝()
    {
        var clock = new Clock(); var memory = new ContentMemoryBudget(); var transport = new BlockingTransport();
        await using var owner = new RequestExecutionCoordinator(transport, new RecordSpy());
        var draft = RequestPreparationTests.Draft(); var environment = new RequestEnvironment(Guid.NewGuid(), 1, "A", []);
        using var prepared = await new RequestPreparation(memory, clock: clock).PrepareAsync(draft, environment, default);
        Assert.Throws<RequestFailure>(() => owner.Start(prepared, draft.Id, 2, environment));
        Assert.Throws<RequestFailure>(() => owner.Start(prepared, draft.Id, 1, environment with { Revision = 2 }));
        clock.Value = 120000;
        Assert.Throws<RequestFailure>(() => owner.Start(prepared, draft.Id, 1, environment));
        Assert.Equal(0, transport.Sends); Assert.Equal(0, memory.Reserved);
    }
    [Fact]
    public async Task 真实执行记录独立来源且不覆盖抓包会话()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            using var certificate = ProxyCertificates.CreateRoot();
            await using var origin = await ControlledOrigin.StartAsync("M4", certificate, https: false);
            var capture = new CaptureSessionStore(); await using var debug = new DebugCaptureStore(root, capture.ContentBudget);
            await using var owner = new RequestExecutionCoordinator(new HttpRequestTransport(), debug);
            var draft = RequestPreparationTests.Draft(new Uri(origin.Address, "/echo").AbsoluteUri) with { BodyKind = RequestBodyKind.Text, Body = "中文" };
            using var prepared = await new RequestPreparation(capture.ContentBudget).PrepareAsync(draft, null, default);
            var attempt = owner.Start(prepared, draft.Id, draft.Revision, null); await attempt.Completion;
            var record = await debug.Session.ReadAsync(attempt.Id, default);
            Assert.NotNull(record); Assert.Equal(TrafficSource.Manual, record.Source); Assert.Null(capture.Current);
            Assert.Equal(200, record.StatusCode); Assert.Equal(6, record.RequestBody.ObservedBytes);
            Assert.Equal(RequestExecutionState.Completed, attempt.Snapshot.State); Assert.Single(origin.Receipts);
            Assert.Equal(0, capture.ContentBudget.Reserved);
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Fact]
    public async Task 敏感请求不进入临时日志或正文文件()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            using var certificate = ProxyCertificates.CreateRoot(); await using var origin = await ControlledOrigin.StartAsync("M4", certificate, https: false);
            var memory = new ContentMemoryBudget(); await using var debug = new DebugCaptureStore(root, memory, deleteOnDispose: false);
            await using var owner = new RequestExecutionCoordinator(new HttpRequestTransport(), debug);
            // 端点返回空响应，避免把服务器主动回显的原文误当成请求索引泄漏。
            var draft = RequestPreparationTests.Draft(new Uri(origin.Address, "/empty?q=${token}").AbsoluteUri) with
            { BodyKind = RequestBodyKind.Text, Body = "${token}", Variables = [new("token", "M4_SECRET_SENTINEL", true)] };
            // Uri构造会编码占位符，编辑器本身使用原始字符串。
            draft = draft with { Address = origin.Address.GetLeftPart(UriPartial.Authority) + "/empty?q=${token}" };
            using var prepared = await new RequestPreparation(memory).PrepareAsync(draft, null, default);
            var attempt = owner.Start(prepared, draft.Id, draft.Revision, null); await attempt.Completion; await debug.DisposeAsync();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain("M4_SECRET_SENTINEL", await File.ReadAllTextAsync(file));
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
}
