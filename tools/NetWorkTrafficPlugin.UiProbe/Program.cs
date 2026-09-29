using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Features.Capture;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Content;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;

namespace NetWorkTrafficPlugin.UiProbe;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

/// <summary>
/// 独立桌面 Avalonia 进程承载生产 CaptureView，窗口移到屏幕外避免打断用户。
/// 测量的是应用输入投递/处理延迟，不声称测过物理键鼠或真实 Host；Headless 布局截图另有测试。
/// </summary>
public sealed class ProbeApp : Avalonia.Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window { Width = 1200, Height = 720, Position = new(-20000, -20000), ShowInTaskbar = false, ShowActivated = false };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                var code = 0;
                try { await RunAsync(window, desktop.Args?.Contains("--smoke") == true); Console.WriteLine("M1 生产 UI 测量结束。"); }
                catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
                finally { desktop.Shutdown(code); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task RunAsync(Window window, bool smoke)
    {
        var directory = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS") ?? throw new InvalidOperationException("缺少测量输出目录。");
        var seconds = smoke ? 5 : 600;
        var rootDirectory = TemporaryArtifacts.Create();
        var samples = new List<object>(); var latency = new List<double>(); var virtualized = new List<object>();
        var store = new CaptureSessionStore();
        await using var archive = new FileCaptureArchiveStore();
        var decoder = new BoundedImageDecoder(store.ContentBudget);
        CaptureWorkspace? model = null;
        try
        {
            using var ca = ProxyCertificates.CreateRoot();
            await using var origin = await ControlledOrigin.StartAsync("native-ui", ca, https: false);
            await using var runtime = new TitaniumProxyRuntime(captures: store);
            var info = await runtime.StartAsync(new(Capture: CaptureMode.BoundedBody, RecordDirectory: rootDirectory, Limits: CaptureLimits.M1()), default);
            using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{info.Port}"), UseProxy = true, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(16)); var token = deadline.Token;
            // 真正发送一万次独立请求，不能只把一万条虚假数据塞进 ObservableCollection。
            await Parallel.ForEachAsync(Enumerable.Range(0, 10000), new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = token }, async (i, ct) =>
            { using var response = await client.GetAsync(new Uri(origin.Address, $"/empty?i={i}"), ct); response.EnsureSuccessStatusCode(); });
            await ProxyFixture.WaitUntilAsync(() => runtime.GetMetrics().ActiveRequests == 0);
            model = new CaptureWorkspace(store, archive, decoder);
            var view = new CaptureView { DataContext = model }; window.Content = view;
            while (model.Rows.Count < 10000) await model.RefreshAsync();
            window.UpdateLayout();
            var list = view.FindControl<ListBox>("TrafficList")!;
            foreach (var index in new[] { 0, 5000, 9999 })
            {
                list.ScrollIntoView(model.Rows[index]); await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                var containers = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
                var visible = containers.Count(c => c.TranslatePoint(default, list) is { } point && point.Y + c.Bounds.Height > 0 && point.Y < list.Bounds.Height);
                Check(containers.Length <= visible * 3 + 20, "列表未虚拟化。"); virtualized.Add(new { index, containers = containers.Length, visible });
            }
            var baseline = Process.GetCurrentProcess().PrivateMemorySize64;
            var pending = Enumerable.Range(0, 20).Select(i => client.GetAsync(new Uri(origin.Address, i < 5 ? $"/stream?i={i}" : $"/binary?size={16 << 20}&hold=1&i={i}"), HttpCompletionOption.ResponseHeadersRead, token)).ToArray();
            var responses = await Task.WhenAll(pending);
            Check(runtime.GetMetrics().ActiveRequests == 20, "混合负载未达到二十路。");
            origin.ContinueStream.TrySetResult();
            var streamReaders = responses.Take(5).Select(async response =>
            { try { await TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync(token), token); } catch (Exception ex) when (ex is IOException or HttpRequestException) { } }).ToArray();
            await Task.WhenAll(responses.Skip(5).Select(async response =>
            { var digest = await TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync(token), token); Check(digest.Bytes == 16 << 20, "混合文件传输长度错误。"); }));
            var streamIds = store.Current!.Snapshot().Records.Where(r => r.Path == "/stream").Select(r => r.RecordId).ToArray();
            Check(streamIds.Length == 5, "未捕获五条持续流。");
            await ProxyFixture.WaitUntilAsync(() => store.Current.Stream(streamIds[0])?.Total > 0);
            var parsedUtc = DateTimeOffset.UtcNow;
            while (!model.Rows.Any(r => r.RecordId == streamIds[0])) { await model.RefreshAsync(); await Task.Delay(10, token); }
            model.Selected = model.Rows.First(r => r.RecordId == streamIds[0]); await model.DetailTask;
            Check(model.Events.Count > 0, "首事件未进入生产视图模型。"); window.UpdateLayout(); var visibleUtc = DateTimeOffset.UtcNow;
            Check((visibleUtc - parsedUtc).TotalMilliseconds <= 1000, "首事件到界面超时。");
            var beforePause = store.Current.Stream(streamIds[0])!.Total; model.ToggleFollowCommand.Execute(null);
            var watch = Stopwatch.StartNew(); var rounds = 0; var pausedContinued = false;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                token.ThrowIfCancellationRequested();
                if (watch.Elapsed.TotalSeconds >= rounds * seconds / 20.0)
                {
                    window.Content = null; model.Dispose(); await model.DetailTask;
                    // 流事件缓存由共享会话拥有；页面释放只归还其预览对象，不停止后台捕获。
                    model = new CaptureWorkspace(store, archive, decoder); view = new CaptureView { DataContext = model }; window.Content = view;
                    await model.RefreshAsync(); model.Selected = model.Rows.FirstOrDefault(r => r.RecordId == streamIds[0]); await model.DetailTask;
                    rounds++;
                    if (rounds == 2)
                    {
                        pausedContinued = store.Current.Stream(streamIds[0])!.Total > beforePause;
                        model.Follow = true;
                        model.ArchivePath = Path.Combine(Path.GetDirectoryName(rootDirectory)!, "network-m0-" + Guid.NewGuid().ToString("N"));
                        try { await model.SaveCommand.ExecuteAsync(null); Check(File.Exists(Path.Combine(model.ArchivePath, "manifest.json")), "活动流快照保存失败。"); }
                        finally { TemporaryArtifacts.Delete(model.ArchivePath); }
                    }
                }
                // 从后台线程实际投递到生产 Dispatcher，执行选择/跟随意图，计入 UI 忙碌造成的排队时间。
                var current = model;
                var delay = await Task.Run(async () =>
                {
                    var sent = Stopwatch.GetTimestamp();
                    await Dispatcher.UIThread.InvokeAsync(() => { current.ToggleFollowCommand.Execute(null); window.UpdateLayout(); });
                    return Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                });
                latency.Add(delay);
                using (var process = Process.GetCurrentProcess())
                    samples.Add(new { utc = DateTimeOffset.UtcNow, seconds = watch.Elapsed.TotalSeconds, privateBytes = process.PrivateMemorySize64,
                        handles = process.HandleCount, queue = runtime.GetMetrics().QueueBytes, content = store.ContentBudget.Reserved,
                        active = runtime.GetMetrics().ActiveRequests, retained = store.Current.Snapshot().Records.Length, delay });
                Check(runtime.GetMetrics().ActiveRequests == 5, "活动流被提前截断。");
                await Task.Delay(smoke ? 10 : 250, token);
            }
            model.Dispose(); window.Content = null; await model.DetailTask;
            var stopping = Stopwatch.StartNew(); await runtime.StopAsync(); await Task.WhenAll(streamReaders); var stopMs = stopping.Elapsed.TotalMilliseconds;
            foreach (var response in responses) response.Dispose();
            Check(store.ContentBudget.Reserved == 0, "退出后内容租约未归还。"); Check(stopMs <= 10000, "退出超时。");
            var sorted = latency.Order().ToArray(); var p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
            var maxPrivate = samples.Select(s => JsonSerializer.SerializeToElement(s).GetProperty("privateBytes").GetInt64()).Max();
            Check(p95 <= 200 && sorted[^1] <= 1000, "UI 投递时延超预算。"); Check(maxPrivate - baseline <= 256L << 20, "UI 私有内存增长超预算。");
            Check(smoke || rounds == 20 && pausedContinued && latency.Count >= 200, "UI 负载/轮次不足。");
            var curve = Path.Combine(directory, "m1-ui-native-samples.json");
            await File.WriteAllTextAsync(curve, JsonSerializer.Serialize(samples));
            ProbeEvidence.Write("m1-ui-native", "sustained", new { renderer = "Avalonia Desktop", realHost = false, physicalInput = false, smoke,
                seconds = watch.Elapsed.TotalSeconds, rounds, sampleCount = samples.Count, latency, p95, maximum = sorted[^1], baseline, maxPrivate,
                contentPeak = store.ContentBudget.Peak, contentAfter = store.ContentBudget.Reserved, stopMs, virtualized, parsedUtc, visibleUtc,
                pausedContinued, originReceipts = origin.Receipts.Count, curveSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(curve))) });
        }
        finally { model?.Dispose(); TemporaryArtifacts.Delete(rootDirectory); }
    }
}
