using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

/// <summary>上传/下载和三策略均在独立生产代理进程测量；父进程的生成及哈希内存不混入代理指标。</summary>
public sealed class M1ResourceProbeTests
{
    [Fact]
    [Trait("Category", "M1ResourceProbe")]
    public async Task 三策略双方向两种大小各三次独立测量保持前缀和资源预算()
    {
        var profilePath = Path.Combine(ProbeProcess.Repository, "tools/m1-resource-profile.json");
        using var profile = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath));
        var p = profile.RootElement;
        var sizes = p.GetProperty("sizes").EnumerateArray().Select(x => x.GetInt64()).ToArray();
        Assert.Equal(new[] { 128L << 20, 1L << 30 }, sizes); Assert.Equal(3, p.GetProperty("repetitions").GetInt32());
        var expected = sizes.ToDictionary(s => s, TransferDigest.Expected);
        var samples = new ConcurrentQueue<ResourceSample>(); var transfers = new List<object>();
        var peaks = new Dictionary<string, List<long>>();
        using var root = Infrastructure.Proxy.ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("M1-resource", root, https: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(19)); var token = deadline.Token;
        foreach (var mode in new[] { CaptureMode.MetadataOnly, CaptureMode.BoundedBody, CaptureMode.Disk })
        foreach (var upload in new[] { false, true })
        foreach (var size in sizes)
        for (var repeat = 0; repeat < 3; repeat++)
        {
            var directory = TemporaryArtifacts.Create();
            var phase = $"{mode}-{(upload ? "upload" : "download")}-{size}-{repeat}";
            try
            {
                await using var process = new ProbeProcess();
                await process.StartAsync(new(Capture: mode, RecordDirectory: directory, Limits: CaptureLimits.M1(mode, 2L << 30)));
                using var client = process.Client();
                await process.WarmupAsync(p.GetProperty("warmupBytes").GetInt32(), p.GetProperty("warmupConcurrency").GetInt32());
                var baseline = await process.SampleAsync(phase + "-baseline"); samples.Enqueue(baseline);
                using var sampling = new CancellationTokenSource();
                var sampler = Task.Run(async () =>
                {
                    try { while (!sampling.IsCancellationRequested) { samples.Enqueue(await process.SampleAsync(phase)); await Task.Delay(250, sampling.Token); } }
                    catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
                });
                var time = Stopwatch.StartNew();
                try
                {
                    string hash;
                    if (upload)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin.Address, "/echo?case=" + phase))
                        { Content = new GeneratedContent(size, knownLength: repeat != 1) };
                        request.Headers.ExpectContinue = repeat == 2;
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                        response.EnsureSuccessStatusCode(); hash = (await response.Content.ReadFromJsonAsync<OriginReceipt>(token))!.BodyHash;
                    }
                    else
                    {
                        using var response = await client.GetAsync(new Uri(origin.Address, $"/binary?size={size}&unknown={(repeat == 1 ? 1 : 0)}&case={phase}"), HttpCompletionOption.ResponseHeadersRead, token);
                        response.EnsureSuccessStatusCode(); var digest = await TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync(token), token);
                        Assert.Equal(size, digest.Bytes); hash = digest.Hash;
                    }
                    Assert.Equal(expected[size], hash);
                    for (var i = 0; i < 500; i++)
                    {
                        var sample = await process.SampleAsync(phase); samples.Enqueue(sample);
                        if (sample.ActiveRequests == 0 && sample.QueueBytes == 0) break;
                        await Task.Delay(10, token);
                    }
                    var record = Assert.Single(await process.CaptureAsync(), r => r.OriginalUri.Contains(phase, StringComparison.Ordinal));
                    var body = upload ? record.RequestBody : record.ResponseBody;
                    Assert.Equal(size, body.ObservedBytes); Assert.Equal(TransportState.Completed, body.Transport);
                    Assert.Equal(repeat == 1 ? null : size, body.ExpectedBytes);
                    var expectedPrefix = TransferDigest.Expected(body.CommittedBytes);
                    string? storedHash = null;
                    if (body.CommittedBytes > 0)
                    {
                        await using var file = new FileStream(body.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var digest = await TransferDigest.ReadAsync(file, token); Assert.Equal(body.CommittedBytes, digest.Bytes);
                        storedHash = digest.Hash; Assert.Equal(expectedPrefix, storedHash);
                    }
                    Assert.InRange(body.CommittedBytes, 0, mode == CaptureMode.MetadataOnly ? 0 : mode == CaptureMode.BoundedBody ? 1 << 20 : size);
                    Assert.Equal(body.CommittedBytes == size, body.Capture == CaptureState.Complete);
                    sampling.Cancel(); await sampler;
                    var points = samples.Where(s => s.Phase == phase).ToArray();
                    var peak = Math.Max(0, points.Max(s => s.PrivateBytes) - baseline.PrivateBytes);
                    Assert.InRange(peak, 0, p.GetProperty("maxPrivateDeltaBytes").GetInt64());
                    Assert.All(points, s => Assert.InRange(s.QueueBytes, 0, p.GetProperty("maxQueueBytes").GetInt32()));
                    var key = $"{mode}-{upload}-{size}"; if (!peaks.TryGetValue(key, out var values)) peaks[key] = values = []; values.Add(peak);
                    var stopped = await process.StopAsync();
                    Assert.Equal(0, stopped.ExitCode); Assert.Equal(0, stopped.Metrics.ActiveRequests); Assert.Equal(0, stopped.Metrics.QueueBytes);
                    Assert.InRange(stopped.Milliseconds, 0, 10000); Assert.InRange(stopped.Handles - baseline.Handles, int.MinValue, 32);
                    using var rebound = new TcpListener(System.Net.IPAddress.Loopback, process.Info.Port); rebound.Start();
                    long physical = 0;
                    foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    { using var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); physical += file.Length; }
                    Assert.InRange(physical, 0, (2L << 30) + 256);
                    transfers.Add(new { phase, mode = mode.ToString(), direction = upload ? "upload" : "download", size, repeat,
                        knownLength = repeat != 1, expectContinue = upload && repeat == 2, hash, storedHash, expectedPrefix,
                        observed = body.ObservedBytes, stored = body.CommittedBytes, state = body.Capture.ToString(), baseline,
                        peak, sampleCount = points.Length, milliseconds = time.Elapsed.TotalMilliseconds, stopMilliseconds = stopped.Milliseconds,
                        stoppedHandles = stopped.Handles, physical, portReleased = true });
                }
                finally { sampling.Cancel(); await sampler; }
            }
            finally { TemporaryArtifacts.Delete(directory); await SaveAsync(samples, transfers, profilePath); }
        }
        foreach (var mode in new[] { CaptureMode.MetadataOnly, CaptureMode.BoundedBody, CaptureMode.Disk })
        foreach (var upload in new[] { false, true })
            Assert.True(peaks[$"{mode}-{upload}-{sizes[1]}"].Order().ElementAt(1) - peaks[$"{mode}-{upload}-{sizes[0]}"].Order().ElementAt(1) <= p.GetProperty("maxMedianGrowthBytes").GetInt64());
        Assert.Equal(36, transfers.Count); await SaveAsync(samples, transfers, profilePath);
    }
    private static async Task SaveAsync(IEnumerable<ResourceSample> samples, List<object> transfers, string profilePath)
    {
        var directory = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS") ?? Path.Combine(ProbeProcess.Repository, "TestResults/M1ResourcePreview");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "m1-resource-samples.csv");
        var points = samples.ToArray();
        var lines = points.Select(s => string.Join(',', s.Phase, s.Utc.ToString("O"), s.PrivateBytes.ToString(CultureInfo.InvariantCulture), s.WorkingSet, s.ManagedBytes, s.Handles, s.ActiveRequests, s.QueueBytes, s.StoredBytes));
        await File.WriteAllLinesAsync(path, new[] { "Phase,Utc,PrivateBytes,WorkingSet,ManagedBytes,Handles,ActiveRequests,QueueBytes,StoredBytes" }.Concat(lines));
        ProbeEvidence.Write("m1-resources", "transfers", new { transfers, sampleCount = points.Length, csvSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), profileSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(profilePath))) });
    }
}
