using System.Diagnostics;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Requests;
using NetWorkTrafficPlugin.Tests.Fixtures;
using NetWorkTrafficPlugin.Tests.Requests;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

/// <summary>主动请求独立资源探针。这里只证明明确列出的传输和寿命边界，不冒充24路混合负载的600秒验收。</summary>
[Collection("M4Resources")]
[Trait("Category", "M4ResourceProbe")]
public sealed class M4ResourceTests
{
    [Theory]
    [InlineData(134217728L)] [InlineData(1073741824L)]
    public async Task 大响应流式消费且正文保存和内存有界(long size)
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            using var certificate = ProxyCertificates.CreateRoot(); await using var origin = await ControlledOrigin.StartAsync("M4RESOURCE", certificate, https: false);
            var memory = new ContentMemoryBudget(); await using var debug = new DebugCaptureStore(root, memory);
            await using var coordinator = new RequestExecutionCoordinator(new HttpRequestTransport(), debug);
            var draft = RequestPreparationTests.Draft(new Uri(origin.Address, "/binary?size=" + size).AbsoluteUri) with { Method = "GET" };
            using var prepared = await new RequestPreparation(memory).PrepareAsync(draft, null, default);
            using var process = Process.GetCurrentProcess(); process.Refresh(); var baseline = process.PrivateMemorySize64;
            var samples = new List<long> { baseline }; var stopwatch = Stopwatch.StartNew();
            var attempt = coordinator.Start(prepared, draft.Id, draft.Revision, null);
            while (!attempt.Completion.IsCompleted)
            {
                await Task.WhenAny(attempt.Completion, Task.Delay(20)); process.Refresh(); samples.Add(process.PrivateMemorySize64);
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(120));
            }
            await attempt.Completion; var record = await debug.Session.ReadAsync(attempt.Id, default); Assert.NotNull(record);
            var peak = samples.Max() - baseline;
            Assert.Equal(RequestExecutionState.Completed, attempt.Snapshot.State); Assert.Equal(size, attempt.Snapshot.ResponseBytes);
            Assert.True(record.ResponseBody.CommittedBytes <= 1048576); Assert.True(peak <= 256L << 20);
            Assert.Equal(0, memory.Reserved); Assert.Equal(0, coordinator.Active); Assert.Single(origin.Receipts);
            ProbeEvidence.Write("m4-resource", "download-" + size, new { size, observed = attempt.Snapshot.ResponseBytes,
                stored = record.ResponseBody.CommittedBytes, peakPrivateDelta = peak, samples, milliseconds = stopwatch.ElapsedMilliseconds,
                active = coordinator.Active, reserved = memory.Reserved, peakReserved = memory.Peak, receipts = origin.Receipts.Count });
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Fact]
    public async Task 二十次取消与容器关闭归还正文租约连接任务和调试缓存()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            using var certificate = ProxyCertificates.CreateRoot(); var latencies = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                await using var origin = await ControlledOrigin.StartAsync("M4STOP", certificate, https: false);
                var memory = new ContentMemoryBudget();
                await using var debug = new DebugCaptureStore(root, memory); await using var owner = new RequestExecutionCoordinator(new HttpRequestTransport(), debug);
                var draft = RequestPreparationTests.Draft(new Uri(origin.Address, "/silent").AbsoluteUri) with { Method = "GET" };
                using var prepared = await new RequestPreparation(memory).PrepareAsync(draft, null, default);
                var attempt = owner.Start(prepared, draft.Id, draft.Revision, null);
                var wait = Stopwatch.StartNew();
                while (attempt.Snapshot.ResponseBytes == 0) { Assert.True(wait.Elapsed < TimeSpan.FromSeconds(5)); await Task.Delay(5); }
                var stop = Stopwatch.StartNew(); await owner.DisposeAsync(); await origin.StreamExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await debug.DisposeAsync(); latencies.Add(stop.Elapsed.TotalMilliseconds);
                Assert.True(attempt.Completion.IsCompletedSuccessfully); Assert.Equal(0, owner.Active); Assert.Equal(0, memory.Reserved);
                Assert.Empty(Directory.EnumerateDirectories(root, "debug-*")); Assert.Single(origin.Receipts);
                Assert.Equal(200, attempt.Snapshot.StatusCode); Assert.Contains("未知", attempt.Snapshot.Message);
            }
            Assert.All(latencies, milliseconds => Assert.True(milliseconds <= 5000));
            ProbeEvidence.Write("m4-resource", "twenty-stops", new { cycles = latencies.Count, latencies, active = 0, reserved = 0, remainingCaches = Directory.EnumerateDirectories(root, "debug-*").Count() });
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
}
[CollectionDefinition("M4Resources", DisableParallelization = true)]
public sealed class M4ResourceCollection;
