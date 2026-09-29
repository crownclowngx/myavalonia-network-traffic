using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input.Platform;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Features.Capture;
using NetWorkTrafficPlugin.Features.Main;
using NetWorkTrafficPlugin.Features.ProxyStatus;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Content;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Ui;

[CollectionDefinition("M1UI", DisableParallelization = true)]
public sealed class M1UiCollection;

/// <summary>使用生产 View 的真实绑定与 Headless 输入，图像由 Skia 渲染；不制作另一套测试界面。</summary>
[Collection("M1UI")]
public sealed class M1CaptureUiTests
{
    public sealed class TestApp : Avalonia.Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseHeadless(new() { UseHeadlessDrawing = false }).UseSkia();
    private static readonly Lazy<HeadlessUnitTestSession> Ui = new(() => HeadlessUnitTestSession.StartNew(typeof(M1CaptureUiTests)));

    [Theory]
    [InlineData(1200, 720, false, false)]
    [InlineData(800, 600, false, false)]
    [InlineData(520, 420, false, false)]
    [InlineData(1200, 720, true, false)]
    [InlineData(800, 600, true, false)]
    [InlineData(520, 420, true, false)]
    [InlineData(420, 600, false, true)]
    [InlineData(280, 420, false, true)]
    [InlineData(420, 600, true, true)]
    [InlineData(280, 420, true, true)]
    public async Task 生产Document和Tool两主题主要尺寸关键入口可达(int width, int height, bool dark, bool tool)
    {
        await Ui.Value.Dispatch(async () =>
        {
            await using var fixture = new M1SessionFixture();
            await fixture.AddAsync("{\"title\":\"中文抓包记录\",\"items\":[1,2]}"u8.ToArray(), type: "application/json");
            var store = new CaptureSessionStore(); store.Publish(fixture.Session);
            await using var archive = new FileCaptureArchiveStore();
            using var owner = new ProxySessionCoordinator(() => new TitaniumProxyRuntime());
            using var model = new CaptureWorkspace(store, archive, new BoundedImageDecoder(store.ContentBudget));
            using var panel = new ProxyPanelModel(owner) { RecordDirectory = fixture.Root };
            using var document = new MainDocument(panel, model);
            Control view = tool ? new ProxyPanelView { DataContext = panel } : new MainView { DataContext = document };
            var window = new Window { Width = width, Height = height, Content = view, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            try
            {
                window.Show(); await model.RefreshAsync(); Pump(window);
                var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && Equals(b.Content, "停止代理")).ToArray();
                Assert.NotEmpty(buttons); AssertInside(buttons[0], window);
                if (!tool)
                {
                    var capture = view.GetVisualDescendants().OfType<CaptureView>().Single();
                    var list = capture.FindControl<ListBox>("TrafficList")!;
                    AssertInside(list, window); Assert.Single(model.Rows);
                    list.SelectedIndex = 0; Pump(window);
                    await model.DetailTask; Pump(window);
                    Assert.NotNull(model.Selected); Assert.Equal(width < 720, model.IsCompact);
                    AssertInside(capture.FindControl<TabControl>("DetailsTabs")!, window);
                    AssertInside(capture.FindControl<ScrollViewer>("BodyScroll")!, window);
                    if (model.IsCompact)
                    {
                        Assert.False(capture.FindControl<TextBox>("FilterBox")!.IsEffectivelyVisible);
                        Assert.True(capture.FindControl<ScrollViewer>("BodyScroll")!.Bounds.Height >= 100, "窄屏正文必须有可阅读空间。");
                        model.BackCommand.Execute(null); Pump(window); Assert.True(capture.FindControl<TextBox>("FilterBox")!.IsEffectivelyVisible);
                        model.ShowDetails = true; Pump(window);
                    }
                }
                var name = $"m1-{(tool ? "tool" : "document")}-{(dark ? "dark" : "light")}-{width}.png";
                Save(window, name);
                ProbeEvidence.Write("m1-ui", $"layout-{tool}-{dark}-{width}", new { width, height, dark, tool,
                    stopReachable = buttons[0].IsEffectivelyVisible, rows = model.Rows.Count, compact = model.IsCompact, realHost = false });
            }
            finally { window.Close(); }
            return true;
        }, default);
    }
    [Fact]
    public async Task 实际按钮选择筛选暂停保存和重开不触发代理()
    {
        await Ui.Value.Dispatch(async () =>
        {
            await using var fixture = new M1SessionFixture();
            await fixture.AddAsync("data: first\n\n"u8.ToArray(), false, "text/event-stream");
            var store = new CaptureSessionStore(); store.Publish(fixture.Session);
            await using var archive = new FileCaptureArchiveStore();
            using var model = new CaptureWorkspace(store, archive, new BoundedImageDecoder(store.ContentBudget));
            var view = new CaptureView { DataContext = model }; var window = new Window { Width = 900, Height = 620, Content = view };
            try
            {
                window.Show(); await model.RefreshAsync(); Pump(window);
                Click(window, view.FindControl<Button>("FollowButton")!); Assert.False(model.Follow);
                var list = view.FindControl<ListBox>("TrafficList")!; list.SelectedIndex = 0; Pump(window);
                Assert.NotNull(model.Selected);
                Click(window, view.FindControl<Button>("FollowButton")!); Assert.True(model.Follow);
                var expander = view.GetVisualDescendants().OfType<Expander>().Single(); expander.IsExpanded = true; Pump(window);
                model.ArchivePath = Path.Combine(fixture.Root, "ui-saved");
                Click(window, view.FindControl<Button>("SaveButton")!);
                if (model.SaveCommand.ExecutionTask is { } saving) await saving;
                Assert.True(File.Exists(Path.Combine(model.ArchivePath, "manifest.json")));
                Click(window, view.FindControl<Button>("OpenButton")!);
                if (model.OpenCommand.ExecutionTask is { } opening) await opening;
                Assert.True(model.IsReadOnly); Assert.True(fixture.Session.Snapshot().IsRunning);
                // 打开改变数据源后等待生产刷新，截图必须显示只读记录，不能截到上一帧实时投影。
                using (var refreshed = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (!model.Status.Contains("只读", StringComparison.Ordinal))
                    { await Task.Delay(10, refreshed.Token); await model.RefreshAsync(); Pump(window); }
                list.SelectedIndex = 0; await model.DetailTask; Pump(window);
                Assert.Contains("只读", model.Status); Assert.Single(model.Rows); Assert.Contains("Partial", model.BodyStatus);
                Save(window, "m1-readonly-partial.png");
                ProbeEvidence.Write("m1-ui", "interaction", new { saved = true, readOnly = model.IsReadOnly,
                    liveStillRunning = fixture.Session.Snapshot().IsRunning, follow = model.Follow, realHost = false });
            }
            finally { window.Close(); }
            return true;
        }, default);
    }
    internal static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    [Fact]
    public async Task 生产控件启动停止端口错误和公开证书入口可操作()
    {
        await Ui.Value.Dispatch(async () =>
        {
            var directory = TemporaryArtifacts.Create();
            try
            {
                await using var owner = new ProxySessionCoordinator(() => new TitaniumProxyRuntime());
                using var panel = new ProxyPanelModel(owner) { RecordDirectory = directory, Port = "-1" };
                var view = new ProxyPanelView { DataContext = panel }; var window = new Window { Width = 480, Height = 720, Content = view };
                try
                {
                    window.Show(); Pump(window);
                    Assert.Equal(ProxyProtocolCapabilities.Http2IdleNotice, view.FindControl<TextBlock>("IdlePolicyNotice")!.Text);
                    Button Find(string text) => view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, text));
                    Click(window, Find("启动代理")); if (panel.StartCommand.ExecutionTask is { } invalid) await invalid;
                    Assert.Contains("0～65535", panel.Message); Assert.Equal(ProxyState.Stopped, owner.Status.State);
                    panel.Port = "0"; Click(window, Find("启动代理")); await panel.StartCommand.ExecutionTask!; Pump(window);
                    Assert.Equal(ProxyState.Running, owner.Status.State); var address = panel.Address;
                    Assert.Equal(ProxyProtocolCapabilities.Http2IdleNotice, view.FindControl<TextBlock>("IdlePolicyNotice")!.Text);
                    panel.Port = "12345"; panel.CaptureModeIndex = 2; Assert.Equal(address, panel.Address);
                    Assert.Contains("BEGIN CERTIFICATE", panel.CertificatePem); Assert.DoesNotContain("PRIVATE KEY", panel.CertificatePem);
                    Click(window, Find("复制代理地址")); Pump(window);
                    if (TopLevel.GetTopLevel(view)!.Clipboard is { } clipboard) Assert.Equal(address, await clipboard.TryGetTextAsync());
                    Click(window, Find("停止代理")); await panel.StopCommand.ExecutionTask!; Pump(window);
                    Assert.Equal(ProxyState.Stopped, owner.Status.State); Assert.False(Find("停止代理").IsEffectivelyEnabled);
                }
                finally { window.Close(); }
            }
            finally { TemporaryArtifacts.Delete(directory); }
            return true;
        }, default);
    }

    [Fact]
    public async Task 键盘选择关键词输入和Esc返回列表保持请求身份()
    {
        await Ui.Value.Dispatch(async () =>
        {
            await using var f = new M1SessionFixture(); await f.AddAsync("a"u8.ToArray(), uri: "https://example.test/a"); await f.AddAsync("b"u8.ToArray(), uri: "https://example.test/b");
            var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore();
            using var model = new CaptureWorkspace(store, archive, new BoundedImageDecoder(store.ContentBudget));
            var view = new CaptureView { DataContext = model }; var window = new Window { Width = 1000, Height = 640, Content = view };
            try
            {
                window.Show(); await model.RefreshAsync(); Pump(window); var list = view.FindControl<ListBox>("TrafficList")!;
                list.SelectedIndex = 0; Pump(window);
                Assert.True(list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => Equals(i.DataContext, model.Rows[0])).Focus());
                window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null); window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.None, null); await model.DetailTask;
                Assert.Equal(model.Rows[1].RecordId, model.Selected!.RecordId); Assert.Equal("a", model.BodyText);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null); window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null); Assert.False(model.ShowDetails);
                var input = view.FindControl<TextBox>("FilterBox")!; input.Focus(); window.KeyTextInput("not-matched");
                await Task.Delay(250); await model.RefreshAsync(); Assert.Equal("not-matched", model.Keyword); Assert.Empty(model.Rows); Assert.Null(model.Selected);
                input.Text = "/b"; await Task.Delay(250); await model.RefreshAsync(); Assert.Single(model.Rows); Assert.Equal("/b", model.Rows[0].Path);
            }
            finally { window.Close(); }
            return true;
        }, default);
    }
    [Theory]
    [InlineData("sse")]
    [InlineData("resource")]
    [InlineData("quota")]
    [InlineData("image")]
    public async Task 正文查看流式资源配额和图片使用生产渲染且释放租约(string kind)
    {
        await Ui.Value.Dispatch(async () =>
        {
            var limits = CaptureLimits.M1() with { BodyBytes = kind == "quota" ? 4 : 1 << 20 };
            await using var f = new M1SessionFixture(limits);
            using var pixel = new RenderTargetBitmap(new PixelSize(1, 1));
            var square = new Border { Width = 1, Height = 1, Background = Avalonia.Media.Brushes.Red };
            square.Measure(new(1, 1)); square.Arrange(new(0, 0, 1, 1)); pixel.Render(square);
            using var pngStream = new MemoryStream(); pixel.Save(pngStream, PngBitmapEncoderOptions.Default); var png = pngStream.ToArray();
            var body = kind == "image" ? png : kind == "sse" ? "data: 第一事件\n\ndata: 未完成"u8.ToArray() : "synthetic-resource-content"u8.ToArray();
            var type = kind == "image" ? "image/png" : kind == "sse" ? "text/event-stream" : kind == "resource" ? "video/mp4" : "text/plain";
            var entry = await f.AddAsync(body, kind != "sse", type);
            if (kind == "sse") await ProxyFixture.WaitUntilAsync(() => f.Session.Stream(entry.Id)?.Total > 0); else await f.StopAsync();
            var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore();
            var decoder = new BoundedImageDecoder(store.ContentBudget); using var model = new CaptureWorkspace(store, archive, decoder);
            var view = new CaptureView { DataContext = model }; var window = new Window { Width = 1000, Height = 640, Content = view };
            try
            {
                window.Show(); await model.RefreshAsync(); view.FindControl<ListBox>("TrafficList")!.SelectedIndex = 0; await model.DetailTask; Pump(window);
                if (kind == "sse") { Assert.Single(model.Events); Assert.Contains("未完成", model.StreamStatus); }
                if (kind == "resource") { Assert.False(model.HasImage); Assert.Contains("二进制", model.BodyStatus); }
                if (kind == "quota") { Assert.Contains("Partial", model.BodyStatus); Assert.Equal("synt", model.BodyText); }
                if (kind == "image")
                {
                    Assert.True(model.HasImage, model.BodyText + model.BodyStatus); Assert.Equal(1, model.ImageSource!.PixelSize.Width);
                    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoder.DecodeAsync(png, cancelled.Token));
                }
                var tabs = view.FindControl<TabControl>("DetailsTabs")!;
                tabs.SelectedIndex = 3; Pump(window); Assert.DoesNotContain("synthetic=1", model.RequestHeadersText);
                model.RevealSensitive = true; Assert.Contains("synthetic=1", model.RequestHeadersText); model.RevealSensitive = false;
                tabs.SelectedIndex = 0; Save(window, "m1-" + kind + ".png");
                ProbeEvidence.Write("m1-ui", "content-" + kind, new { kind, image = model.HasImage, stream = model.HasStream, bodyStatus = model.BodyStatus, events = model.Events.Count });
            }
            finally { window.Close(); model.Dispose(); }
            Assert.Equal(0, store.ContentBudget.Reserved); return true;
        }, default);
    }
    private static void Click(Window window, Button button)
    {
        button.BringIntoView(); Pump(window);
        var position = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(position, MouseButton.Left); window.MouseUp(position, MouseButton.Left); Pump(window);
    }
    private static void AssertInside(Control control, Window window)
    {
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, $"{control.Name}: {control.Bounds}");
        var point = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(point.X, -1, window.Bounds.Width - control.Bounds.Width + 1);
        Assert.InRange(point.Y, -1, window.Bounds.Height - control.Bounds.Height + 1);
    }
    private static void Save(Window window, string name)
    {
        var root = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS") ?? Path.Combine(ProbeProcess.Repository, "TestResults/M1UiPreview");
        Directory.CreateDirectory(root); Pump(window);
        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var path = Path.Combine(root, name); frame.Save(path, PngBitmapEncoderOptions.Default);
        ProbeEvidence.Write("m1-ui-screenshots", name, new { name, width = frame.PixelSize.Width, height = frame.PixelSize.Height,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), renderer = "Avalonia Headless / Skia", realHost = false });
    }
}
