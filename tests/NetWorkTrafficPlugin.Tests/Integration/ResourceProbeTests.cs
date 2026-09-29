using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

public sealed class ResourceProbeTests
{
    private sealed record Profile(int WarmupBytes, int WarmupConcurrency, long[] Sizes, int Repetitions, int StreamSeconds, int SampleMilliseconds,
        long MaxPrivateDeltaBytes, long MaxMedianGrowthBytes, int MaxQueueBytes, long MaxBodyCacheBytes, long MaxDiskBytes,
        int MaxActiveRequests, int MaxStopMilliseconds, int MaxHandleGrowthAfterStop);
    private sealed record Transfer(string Mode, long Size, int Iteration, string Hash, double Milliseconds, long PeakDelta, int Samples);

    [Fact]
    [Trait("Category", "ResourceProbe")]
    public async Task 独立进程三模式大文件三次对照和十分钟持续流满足冻结预算()
    {
        var profilePath = Path.Combine(ProbeProcess.Repository, "tools/m0-resource-profile.json");
        var profile = JsonSerializer.Deserialize<Profile>(await File.ReadAllTextAsync(profilePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        // 防止把手工快速试跑的 profile 当成正式资源证据；预算值只从这一个文件读取。
        Assert.Equal(new long[] { 128L << 20, 1L << 30 }, profile.Sizes);
        Assert.Equal(3, profile.Repetitions); Assert.Equal(600, profile.StreamSeconds);
        var expected = profile.Sizes.ToDictionary(s => s, TransferDigest.Expected);
        var transfers = new List<Transfer>(); var modes = new List<object>();
        var samples = new ConcurrentQueue<ResourceSample>();
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(18));
        var token = deadline.Token;
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var directTime = Stopwatch.StartNew();
        var directDigest = await Download(direct, new Uri(origin.Address, $"/binary?size={profile.Sizes[0]}"), token);
        Assert.Equal(expected[profile.Sizes[0]], directDigest.Hash);
        var directMilliseconds = directTime.Elapsed.TotalMilliseconds;

        foreach (var mode in Enum.GetValues<CaptureMode>())
        {
            var directory = Path.Combine(Path.GetTempPath(), "network-m0-" + Guid.NewGuid().ToString("N"));
            await using var process = new ProbeProcess();
            await process.StartAsync(new(Capture: mode, RecordDirectory: directory));
            using var client = process.Client();
            var cold = await process.SampleAsync($"{mode}-cold"); samples.Enqueue(cold);
            await process.WarmupAsync(profile.WarmupBytes, profile.WarmupConcurrency);
            var baseline = await process.SampleAsync($"{mode}-warm"); samples.Enqueue(baseline);
            var phase = $"{mode}-idle";
            using var sampling = new CancellationTokenSource();
            // 全过程采样，不主动 GC；同时记录私有内存、工作集、托管堆及插件预算指标。
            var sampler = Task.Run(async () =>
            {
                try
                {
                    while (!sampling.IsCancellationRequested)
                    {
                        samples.Enqueue(await process.SampleAsync(Volatile.Read(ref phase)));
                        await Task.Delay(profile.SampleMilliseconds, sampling.Token);
                    }
                }
                catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
            });
            try
            {
                foreach (var size in profile.Sizes)
                for (var repeat = 0; repeat < profile.Repetitions; repeat++)
                {
                    var name = $"{mode}-{size}-{repeat}"; Volatile.Write(ref phase, name);
                    samples.Enqueue(await process.SampleAsync(name));
                    var time = Stopwatch.StartNew();
                    var digest = await Download(client, new Uri(origin.Address, $"/binary?size={size}"), token);
                    var milliseconds = time.Elapsed.TotalMilliseconds;
                    samples.Enqueue(await process.SampleAsync(name));
                    Assert.Equal(size, digest.Bytes); Assert.Equal(expected[size], digest.Hash);
                    var points = samples.Where(s => s.Phase == name).ToArray();
                    var peak = Math.Max(0, points.Max(s => s.PrivateBytes) - baseline.PrivateBytes);
                    Assert.True(peak <= profile.MaxPrivateDeltaBytes, $"{name} 私有内存增量 {peak}");
                    transfers.Add(new(mode.ToString(), size, repeat, digest.Hash, milliseconds, peak, points.Length));
                }
                var medians = profile.Sizes.Select(size => transfers.Where(t => t.Mode == mode.ToString() && t.Size == size)
                    .Select(t => t.PeakDelta).Order().ElementAt(1)).ToArray();
                Assert.True(medians[1] - medians[0] <= profile.MaxMedianGrowthBytes, "两种大小的过程峰值中位数差超预算。");

                var unknown = await Download(client, new Uri(origin.Address, $"/binary?size={profile.WarmupBytes}&unknown=1"), token);
                Assert.Equal(profile.WarmupBytes, unknown.Bytes); Assert.Equal(TransferDigest.Expected(profile.WarmupBytes), unknown.Hash);
                double streamSeconds = 0;
                if (mode == CaptureMode.BoundedBody)
                {
                    Volatile.Write(ref phase, "mixed-20");
                    // 十个长流与十个停在首块的二进制同时存活，验证活动上限而非瞬时并发调用数。
                    var pending = Enumerable.Range(0, profile.MaxActiveRequests).Select(i => client.GetAsync(new Uri(origin.Address,
                        i < 10 ? "/stream" : $"/binary?size={profile.WarmupBytes}&hold=1"), HttpCompletionOption.ResponseHeadersRead, token)).ToArray();
                    var responses = await Task.WhenAll(pending);
                    var mixed = await process.SampleAsync("mixed-20"); samples.Enqueue(mixed);
                    Assert.Equal(20, mixed.ActiveRequests);
                    origin.ContinueStream.TrySetResult();
                    var readers = responses.Take(10).Select(async response =>
                    {
                        try { await TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync(token), token); }
                        catch (IOException) { /* 停止代理使长响应结束。 */ }
                    }).ToArray();
                    try
                    {
                        foreach (var response in responses.Skip(10))
                        {
                            var received = await TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync(token), token);
                            Assert.Equal(profile.WarmupBytes, received.Bytes);
                            Assert.Equal(unknown.Hash, received.Hash);
                        }
                        Volatile.Write(ref phase, "sustained-stream");
                        var sustained = Stopwatch.StartNew();
                        var duration = Task.Delay(TimeSpan.FromSeconds(profile.StreamSeconds), token);
                        await Task.WhenAny(duration, origin.StreamExited.Task);
                        Assert.False(origin.StreamExited.Task.IsCompleted, "活跃长流在截止前被意外终止。");
                        await duration;
                        streamSeconds = sustained.Elapsed.TotalSeconds;
                        samples.Enqueue(await process.SampleAsync("sustained-stream"));
                        Assert.False(origin.StreamExited.Task.IsCompleted);
                        sampling.Cancel(); await sampler;
                        var stopped = await process.StopAsync();
                        ProbeEvidence.Write("resource-stop", mode.ToString(), new { baseline, stopped.Milliseconds, stopped.Handles, stopped.Metrics });
                        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5), token);
                        await origin.StreamExited.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                        VerifyStop(stopped, process.Info.Port, baseline, profile);
                        var files = Directory.GetFiles(directory, "*.body", SearchOption.AllDirectories);
                        long fileBytes = 0;
                        foreach (var path in files)
                        {
                            using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                            Assert.InRange(file.Length, 1, 1048576); fileBytes += file.Length;
                        }
                        Assert.Equal(stopped.Metrics.StoredBytes, fileBytes);
                        Assert.InRange(fileBytes, 1, profile.MaxDiskBytes);
                        Assert.Contains(stopped.Metrics.Recent, o => o.Body.CaptureState == CaptureState.Partial && o.Body.ObservedBytes > o.Body.StoredBytes);
                        modes.Add(new { mode = mode.ToString(), cold, baseline, stopped.Milliseconds, stopped.ExitCode, stopped.Handles, fileBytes,
                            stopped.Metrics, streamSeconds, unknown, portReleased = true, filesClosed = true });
                    }
                    finally { foreach (var response in responses) response.Dispose(); }
                }
                else
                {
                    sampling.Cancel(); await sampler;
                    var stopped = await process.StopAsync(); VerifyStop(stopped, process.Info.Port, baseline, profile);
                    Assert.Equal(0, stopped.Metrics.StoredBytes);
                    modes.Add(new { mode = mode.ToString(), cold, baseline, stopped.Milliseconds, stopped.ExitCode, stopped.Handles, fileBytes = 0,
                        stopped.Metrics, streamSeconds, unknown, portReleased = true, filesClosed = true });
                }
            }
            finally
            {
                sampling.Cancel(); await sampler;
                // 失败也保存过程采样，下一次调查不必仅凭最后一个断言猜测中断时刻。
                await SaveSamples(samples);
                ProbeEvidence.Write("resource-progress", mode.ToString(), new { transfers, sampleCount = samples.Count, last = samples.LastOrDefault() });
                TemporaryArtifacts.Delete(directory);
            }
        }
        Assert.All(samples, s =>
        {
            Assert.InRange(s.ActiveRequests, 0, profile.MaxActiveRequests);
            Assert.InRange(s.QueueBytes, 0, profile.MaxQueueBytes);
            Assert.True(s.QueueBytes <= profile.MaxBodyCacheBytes); // 唯一正文缓存是该队列；转发缓冲属于库，进程峰值另验。
            Assert.InRange(s.StoredBytes, 0, profile.MaxDiskBytes);
            var baseline = samples.First(p => p.Phase == (s.Phase.StartsWith("None") ? "None-warm" : s.Phase.StartsWith("MetadataOnly") ? "MetadataOnly-warm" : "BoundedBody-warm"));
            Assert.True(s.PrivateBytes - baseline.PrivateBytes <= profile.MaxPrivateDeltaBytes);
        });
        var sustainedPoints = samples.Where(s => s.Phase == "sustained-stream").ToArray();
        Assert.True(sustainedPoints.Length >= profile.StreamSeconds * 2, "持续流关键采样缺失。");
        Assert.True(sustainedPoints[^1].Utc - sustainedPoints[0].Utc >= TimeSpan.FromSeconds(599));
        var artifacts = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS");
        if (artifacts is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(artifacts, "resource-profile.json"), await File.ReadAllTextAsync(profilePath));
            var rows = samples.Select(s => string.Join(',', s.Phase, s.Utc.ToString("O"), s.PrivateBytes, s.WorkingSet, s.ManagedBytes,
                s.Handles, s.ActiveRequests, s.QueueBytes, s.StoredBytes));
            await File.WriteAllLinesAsync(Path.Combine(artifacts, "resource-samples.csv"),
                new[] { "phase,utc,privateBytes,workingSet,managedBytes,handles,activeRequests,queueBytes,storedBytes" }.Concat(rows));
        }
        ProbeEvidence.Write("resource-probes", "B01-B07-S06", new { profile, directMilliseconds, directDigest, transfers, modes,
            sampleCount = samples.Count, sustainedSampleCount = sustainedPoints.Length, budgetPassed = true });
    }

    private static async Task<TransferDigest> Download(HttpClient client, Uri address, CancellationToken token)
    {
        using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await TransferDigest.ReadAsync(stream, token);
    }
    private static async Task SaveSamples(IEnumerable<ResourceSample> samples)
    {
        var directory = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS");
        if (directory is null) return;
        var rows = samples.Select(s => string.Join(',', s.Phase, s.Utc.ToString("O"), s.PrivateBytes, s.WorkingSet, s.ManagedBytes,
            s.Handles, s.ActiveRequests, s.QueueBytes, s.StoredBytes));
        await File.WriteAllLinesAsync(Path.Combine(directory, "resource-samples.csv"),
            new[] { "phase,utc,privateBytes,workingSet,managedBytes,handles,activeRequests,queueBytes,storedBytes" }.Concat(rows));
    }
    private static void VerifyStop((ProxyMetrics Metrics, double Milliseconds, int ExitCode, int Handles) stopped,
        int port, ResourceSample baseline, Profile profile)
    {
        Assert.Equal(0, stopped.ExitCode); Assert.Equal(0, stopped.Metrics.ActiveRequests); Assert.Equal(0, stopped.Metrics.QueueBytes);
        Assert.True(stopped.Milliseconds <= profile.MaxStopMilliseconds);
        Assert.True(stopped.Handles <= baseline.Handles + profile.MaxHandleGrowthAfterStop,
            $"退出后句柄 {stopped.Handles}，预热基线 {baseline.Handles}，允许增量 {profile.MaxHandleGrowthAfterStop}。");
        using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
    }
}
