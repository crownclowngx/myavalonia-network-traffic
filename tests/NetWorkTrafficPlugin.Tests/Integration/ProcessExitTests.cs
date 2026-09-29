using System.Text.Json;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace NetWorkTrafficPlugin.Tests.Integration;

public sealed class ProcessExitTests(ITestOutputHelper output)
{
    [Fact]
    public async Task 隔离进程并发响应退出后释放句柄()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        var directory = Path.Combine(Path.GetTempPath(), "network-m0-" + Guid.NewGuid().ToString("N"));
        await using var process = new ProbeProcess();
        await process.StartAsync(new(Capture: CaptureMode.BoundedBody, RecordDirectory: directory));
        using var client = process.Client();
        try
        {
            await process.WarmupAsync(16777216, 20);
            var before = await process.SampleAsync("warm");
            var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => client.GetAsync(new Uri(origin.Address,
                i < 10 ? "/stream" : "/binary?size=16777216&hold=1"), HttpCompletionOption.ResponseHeadersRead)));
            var readers = responses.Select(async response =>
            {
                try { await TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync()); }
                catch (IOException) { }
            }).ToArray();
            origin.ContinueStream.TrySetResult();
            await Task.WhenAll(readers.Skip(10)).WaitAsync(TimeSpan.FromSeconds(10));
            var during = await process.SampleAsync("active");
            var stopped = await process.StopAsync();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var response in responses) response.Dispose();
            output.WriteLine(JsonSerializer.Serialize(new { before, during, stopped.Handles, stopped.Milliseconds }));
            Assert.True(stopped.Handles <= before.Handles + 32, $"before={before.Handles}, after={stopped.Handles}");
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }
}
