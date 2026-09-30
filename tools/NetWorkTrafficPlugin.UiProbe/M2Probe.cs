using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Features.Analysis;
using NetWorkTrafficPlugin.Infrastructure.Analysis;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Rules;
using NetWorkTrafficPlugin.Tests.Fixtures;

namespace NetWorkTrafficPlugin.UiProbe;

public sealed partial class ProbeApp
{
    private sealed record M2Point(DateTimeOffset Utc, double Seconds, long PrivateBytes, int Handles,
        long Queue, long Content, long Analysis, int Tasks, int Active, int Retained, double Delay);
    /// <summary>
    /// 独立桌面进程运行生产AnalysisView和真实代理；250ms采样不等待分析完成。
    /// 9980个已完成请求加20路混合请求形成10000条保留集，之后5条流持续到停止。
    /// 计时从负载和渲染预热后开始；原始曲线可以独立复算，不用Headless替代桌面响应时延。
    /// </summary>
    private static async Task RunM2Async(Window window, bool smoke)
    {
        var directory = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS")!;
        Directory.CreateDirectory(directory);
        var root = TemporaryArtifacts.Create();
        var resources = new AnalysisResources(); var store = new CaptureSessionStore();
        var rules = new FileRuleLibraryStore(Path.Combine(root, "rules"));
        var points = new List<M2Point>(); var runs = new List<object>();
        AnalysisWorkspace? page = null;
        using var ca = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("m2-desktop", ca, https: false);
        await using var runtime = new TitaniumProxyRuntime(captures: store);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15)); var ct = timeout.Token;
        try
        {
            var info = await runtime.StartAsync(new(Capture: CaptureMode.BoundedBody, RecordDirectory: Path.Combine(root, "capture"), Limits: CaptureLimits.M1()), ct);
            using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{info.Port}"), UseProxy = true }) { Timeout = Timeout.InfiniteTimeSpan };
            // 预填不是吞吐压力测量。每批32条最多产生64条开始/完成日志，批间让后台写入者排空；
            // 避免瞬时填满128项元信息队列后，按产品契约降级为MetadataPartial，污染“完整10k”分析负载。
            // 正式计时仍在预填和渲染预热之后，20并发、5条流、10k保留和600秒均不变。
            foreach (var batch in Enumerable.Range(0, 9980).Chunk(32))
            {
                await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct }, async (i, token) =>
                { using var response = await client.GetAsync(new Uri(origin.Address, $"/empty?user=u{i % 100}"), token); response.EnsureSuccessStatusCode(); });
                await Task.Delay(20, ct);
            }
            await ProxyFixture.WaitUntilAsync(() => runtime.GetMetrics().ActiveRequests == 0);
            var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
                client.GetAsync(new Uri(origin.Address, i < 5 ? $"/stream?user=stream{i}" : $"/binary?size={16 << 20}&hold=1&user=file{i}"), HttpCompletionOption.ResponseHeadersRead, ct)));
            var concurrent = runtime.GetMetrics().ActiveRequests;
            Check(concurrent == 20, "没有达到20并发。");
            origin.ContinueStream.TrySetResult();
            var streamReaders = responses.Take(5).Select(async response =>
            { try { await TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync(ct), ct); } catch (Exception ex) when (ex is IOException or HttpRequestException) { } }).ToArray();
            foreach (var response in responses.Skip(5))
            {
                var digest = await TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync(ct), ct);
                Check(digest.Bytes == 16 << 20 && digest.Hash == TransferDigest.Expected(16 << 20), "文件转发事实错误。");
            }
            await ProxyFixture.WaitUntilAsync(() => runtime.GetMetrics().ActiveRequests == 5);
            var source = new CaptureAnalysisSource(store.Current!, store.ContentBudget);
            AnalysisWorkspace CreatePage()
            {
                var model = new AnalysisWorkspace(new(resources), rules);
                model.SetSource(source);
                model.Editor.Text = M2Fixture.Program(group: "original.host");
                window.Content = new AnalysisView { DataContext = model };
                window.UpdateLayout(); return model;
            }
            page = CreatePage(); await page.ApplyAsync();
            Check(page.Result?.Coverage.Retained == 10000 && page.Result.Coverage.GroupedRecords == 10000,
                $"{page.Status}；初始覆盖={JsonSerializer.Serialize(page.Result?.Coverage)}；窗口={page.Result?.From:O}～{page.Result?.To:O}");
            var baseline = Process.GetCurrentProcess().PrivateMemorySize64;
            var baselineHandles = Process.GetCurrentProcess().HandleCount;
            var seconds = smoke ? 5 : 600; var watch = Stopwatch.StartNew();
            var cycle = 0; var launched = 0; var successes = 0;
            Task? pending = null; Stopwatch? queryWatch = null;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                ct.ThrowIfCancellationRequested();
                if (pending?.IsCompleted == true)
                {
                    await pending; pending = null;
                    var result = page.Result;
                    Check(result is not null && result.Coverage.GroupedRecords == 10000, page.Status);
                    successes++;
                    runs.Add(new { milliseconds = queryWatch!.Elapsed.TotalMilliseconds, result!.AnalysisId, result.SourceId, result.RuleHash,
                        result.SnapshotRevision, result.From, result.To, result.Coverage });
                }
                if (pending is null && watch.Elapsed.TotalSeconds >= cycle * seconds / 20.0 && cycle < 20)
                {
                    page.Dispose(); await page.CurrentTask;
                    Check(resources.Active == 0 && resources.Memory.Reserved == 0, "关页残留分析任务或结果租约。");
                    page = CreatePage(); cycle++;
                }
                if (pending is null && watch.Elapsed.TotalSeconds >= launched * seconds / 60.0)
                { queryWatch = Stopwatch.StartNew(); pending = page.ApplyAsync(); launched++; }
                var current = page;
                var delay = await Task.Run(async () =>
                {
                    var sent = Stopwatch.GetTimestamp();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        current.RevealValues = !current.RevealValues;
                        if (current.Groups.Count > 0) current.SelectedGroup = current.Groups[0];
                        window.UpdateLayout();
                    });
                    return Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                });
                using (var process = Process.GetCurrentProcess())
                    points.Add(new(DateTimeOffset.UtcNow, watch.Elapsed.TotalSeconds, process.PrivateMemorySize64, process.HandleCount,
                        runtime.GetMetrics().QueueBytes, store.ContentBudget.Reserved, resources.Memory.Reserved, resources.Active,
                        runtime.GetMetrics().ActiveRequests, store.Current!.Snapshot().Records.Length, delay));
                Check(runtime.GetMetrics().ActiveRequests == 5, "持续流提前中断。");
                await Task.Delay(smoke ? 10 : 250, ct);
            }
            if (pending is not null)
            {
                await pending; var result = page.Result!;
                Check(result.Coverage.GroupedRecords == 10000, page.Status); successes++;
                runs.Add(new { milliseconds = queryWatch!.Elapsed.TotalMilliseconds, result.AnalysisId, result.SourceId, result.RuleHash,
                    result.SnapshotRevision, result.From, result.To, result.Coverage });
            }
            page.Dispose(); window.Content = null; await page.CurrentTask;
            var stop = Stopwatch.StartNew(); await runtime.StopAsync(); await Task.WhenAll(streamReaders);
            var stopMs = stop.Elapsed.TotalMilliseconds;
            foreach (var response in responses) response.Dispose();
            client.Dispose(); await Task.Delay(100, ct);
            var handles = Process.GetCurrentProcess().HandleCount;
            var sorted = points.Select(p => p.Delay).Order().ToArray(); var p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
            Check(smoke || successes >= 50 && cycle == 20 && points.Count >= 200, "分析成功轮次、开关页或采样不足。");
            Check(p95 <= 200 && sorted[^1] <= 1000, "分析期间桌面延迟超限。");
            Check(points.Max(p => p.PrivateBytes) - baseline <= 256L << 20 && handles - baselineHandles <= 32, "进程资源超限。");
            Check(resources.Memory.Reserved == 0 && resources.Active == 0 && store.ContentBudget.Reserved == 0 && stopMs <= 10000, "停止后资源未归还。");
            var csv = Path.Combine(directory, "m2-performance-samples.csv");
            await File.WriteAllLinesAsync(csv, new[] { "Utc,Seconds,PrivateBytes,Handles,Queue,Content,Analysis,Tasks,Active,Retained,Delay" }.Concat(points.Select(p =>
                string.Join(',', p.Utc.ToString("O"), p.Seconds.ToString(CultureInfo.InvariantCulture), p.PrivateBytes, p.Handles, p.Queue, p.Content, p.Analysis, p.Tasks, p.Active, p.Retained, p.Delay.ToString(CultureInfo.InvariantCulture)))));
            ProbeEvidence.Write("m2-desktop", "sustained", new { smoke, renderer = "Avalonia Desktop", realHost = false, physicalInput = false,
                seconds = watch.Elapsed.TotalSeconds, concurrent, streams = 5, retained = 10000, successes, launched, cycles = cycle,
                sampleCount = points.Count, p95, maximum = sorted[^1], runs, baseline, baselineHandles, stoppedHandles = handles,
                stopMs, analysisAfter = resources.Memory.Reserved, contentAfter = store.ContentBudget.Reserved, activeAfter = resources.Active,
                csvSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(csv))) });
        }
        finally { page?.Dispose(); await runtime.DisposeAsync(); TemporaryArtifacts.Delete(root); }
    }
}
