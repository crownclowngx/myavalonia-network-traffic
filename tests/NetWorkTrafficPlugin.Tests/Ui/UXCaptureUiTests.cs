using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Features.Capture;
using NetWorkTrafficPlugin.Features.Main;
using NetWorkTrafficPlugin.Features.ProxyStatus;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Content;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;
using static NetWorkTrafficPlugin.Tests.Ui.M1CaptureUiTests;

namespace NetWorkTrafficPlugin.Tests.Ui;

/// <summary>只渲染生产控件；位置来自布局后的实际几何，动作经 Headless 输入进入正常事件路径。</summary>
[Collection("M1UI")]
public sealed class UXCaptureUiTests
{
    private static async Task Populate(M1SessionFixture fixture, int count = 30)
    {
        for (var i = 0; i < count; i++) await fixture.AddAsync("{\"名称\":\"中文内容\",\"数据\":[1,2,3]}"u8.ToArray(), type: "application/json", uri: $"https://example.test/较长中文路径/{i}/检查列表省略与完整提示?token=synthetic-secret");
    }
    private static Button Button(Control view, string content) => view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content) && b.IsEffectivelyVisible);
    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPress(key, modifiers, PhysicalKey.None, null); window.KeyRelease(key, modifiers, PhysicalKey.None, null); Pump(window); }
    private static object Bounds(Control c, Window w) { var p = c.TranslatePoint(default, w)!.Value; return new { x = p.X, y = p.Y, width = c.Bounds.Width, height = c.Bounds.Height }; }
    private static void Screenshot(Window window, string key, object measurements)
    {
        var root = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS") ?? Path.Combine(ProbeProcess.Repository, "TestResults/UXUiPreview");
        Directory.CreateDirectory(root); Pump(window); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var name = "ux-" + key + ".png"; var path = Path.Combine(root, name); frame.Save(path, PngBitmapEncoderOptions.Default);
        var evidence = new { name, width = frame.PixelSize.Width, height = frame.PixelSize.Height, scaling = window.RenderScaling,
            renderer = "Avalonia Headless / Skia", realHost = false, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            test = "NetWorkTrafficPlugin.Tests.Ui.UXCaptureUiTests", measurements };
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(evidence));
        ProbeEvidence.Write("ux-layout", key, evidence);
    }
    [Theory]
    [InlineData(1200,720,false,false)] [InlineData(900,600,false,false)] [InlineData(800,600,false,false)] [InlineData(520,420,false,false)]
    [InlineData(1200,720,true,false)] [InlineData(900,600,true,false)] [InlineData(800,600,true,false)] [InlineData(520,420,true,false)]
    [InlineData(420,600,false,true)] [InlineData(280,420,false,true)] [InlineData(420,600,true,true)] [InlineData(280,420,true,true)]
    public async Task 十二种生产布局按实际坐标测量密度和正文空间(int width, int height, bool dark, bool tool)
    {
        await M1CaptureUiTests.Ui.Value.Dispatch(async () =>
        {
            await using var f = new M1SessionFixture(); await Populate(f); var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore();
            await using var owner = new ProxySessionCoordinator(() => new TitaniumProxyRuntime()); using var panel = new ProxyPanelModel(owner) { RecordDirectory = f.Root };
            using var page = new CaptureWorkspace(store, archive, new(store.ContentBudget)); using var document = new MainDocument(panel, page);
            Control view = tool ? new ProxyPanelView { DataContext = panel } : new MainView { DataContext = document };
            var window = new Window { Width = width, Height = height, Content = view, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            try
            {
                window.Show(); await panel.StartCommand.ExecuteAsync(null); await page.RefreshAsync(); Pump(window);
                var stop = Button(view, "停止代理"); AssertInside(stop, window);
                var stopPoint = stop.TranslatePoint(new Point(stop.Bounds.Width / 2, stop.Bounds.Height / 2), window)!.Value;
                Assert.NotNull(window.InputHitTest(stopPoint));
                double top = 0, bodyHeight = 0, rowHeight = 0; int fullRows = 0, containers = 0;
                if (!tool)
                {
                    var capture = view.GetVisualDescendants().OfType<CaptureView>().Single(); await capture.RefreshUiAsync(); Pump(window);
                    var list = capture.FindControl<ListBox>("TrafficList")!; var pane = capture.FindControl<Border>("ListPane")!;
                    top = pane.TranslatePoint(default, window)!.Value.Y;
                    var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray(); containers = rows.Length; rowHeight = rows[0].Bounds.Height;
                    fullRows = rows.Count(r => r.TranslatePoint(default, list) is { } p && p.Y >= 0 && p.Y + r.Bounds.Height <= list.Bounds.Height);
                    Assert.InRange(rowHeight, 44, 52); Assert.True(containers <= fullRows * 3 + 20);
                    if (width >= 800) Assert.InRange(top, 0, 104);
                    if (width == 800) Assert.True(fullRows >= 8, $"实际完整行 {fullRows}，视口 {list.Bounds}");
                    // 鼠标命中真实行，确认选择绑定后再测正文，而不是直接写页面属性。
                    var first = rows[0]; var point = first.TranslatePoint(new Point(90, 12), window)!.Value;
                    window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); await page.DetailTask; Pump(window);
                    Assert.NotNull(page.Selected); bodyHeight = capture.FindControl<ScrollViewer>("BodyScroll")!.Bounds.Height;
                    if (width == 520) Assert.True(bodyHeight >= 160, $"正文 {bodyHeight}");
                }
                Screenshot(window, $"{(tool ? "tool" : "document")}-{(dark ? "dark" : "light")}-{width}", new { width, height, dark, tool, top, rowHeight, fullRows, containers, bodyHeight, stop = Bounds(stop, window), stopReachable = true });
            }
            finally { window.Close(); }
            return true;
        }, default);
    }
    [Theory]
    [InlineData("list-paused")] [InlineData("stream-paused")] [InlineData("json-formatted")] [InlineData("json-tree")]
    [InlineData("partial")] [InlineData("invalid-filter")] [InlineData("readonly")] [InlineData("settings-fault")]
    public async Task 八种行为截图来自生产操作且阅读锚点不跳动(string behavior)
    {
        await M1CaptureUiTests.Ui.Value.Dispatch(async () =>
        {
            await using var f = new M1SessionFixture(); await Populate(f, 80); var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore();
            await using var owner = new ProxySessionCoordinator(() => new TitaniumProxyRuntime()); using var panel = new ProxyPanelModel(owner) { RecordDirectory = f.Root };
            using var page = new CaptureWorkspace(store, archive, new(store.ContentBudget)); using var document = new MainDocument(panel, page);
            var main = new MainView { DataContext = document }; var window = new Window { Width = 900, Height = 600, Content = main };
            try
            {
                window.Show(); await page.RefreshAsync(); Pump(window); var view = main.GetVisualDescendants().OfType<CaptureView>().Single(); await view.RefreshUiAsync(); Pump(window);
                var list = view.FindControl<ListBox>("TrafficList")!; list.SelectedIndex = 0; await page.DetailTask; Pump(window);
                double anchorError = 0; Guid? beforeId = null, afterId = null;
                switch (behavior)
                {
                    case "list-paused":
                        var position = list.TranslatePoint(new Point(100, 100), window)!.Value;
                        window.MouseWheel(position, new Vector(0, -5), RawInputModifiers.None); Pump(window); Assert.False(page.Follow); Assert.True(page.EventReading.Following);
                        var before = view.CaptureListAnchor(); Assert.NotNull(before); beforeId = before.RecordId; var selected = page.Selected!.RecordId;
                        await f.AddAsync("new"u8.ToArray()); await view.RefreshUiAsync(); Pump(window);
                        var after = view.CaptureListAnchor(); Assert.NotNull(after); afterId = after.RecordId; anchorError = Math.Abs(before.Offset - after.Offset);
                        Assert.True(beforeId == afterId, $"before={before}, after={after}, restore={view.ScrollRestorations}, error={view.LastAnchorError}"); Assert.InRange(anchorError, 0, 2); Assert.Equal(selected, page.Selected!.RecordId); Assert.Equal(1, page.ListReading.Pending);
                        await view.RefreshUiAsync(); Pump(window); Assert.Equal(after, view.CaptureListAnchor());
                        break;
                    case "stream-paused":
                        var stream = await f.AddAsync(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 270).Select(i => $"data: 中文事件 {i}\n\n"))), false, "text/event-stream");
                        await ProxyFixture.WaitUntilAsync(() => f.Session.Stream(stream.Id)?.Total == 270); await page.RefreshAsync(); list.SelectedIndex = 0; await page.DetailTask; await view.RefreshUiAsync(); Pump(window);
                        var body = view.FindControl<ScrollViewer>("BodyScroll")!; var bodyPoint = body.TranslatePoint(new Point(100, 100), window)!.Value;
                        window.MouseWheel(bodyPoint, new Vector(0, 8), RawInputModifiers.None); Pump(window); Assert.False(page.EventReading.Following);
                        var bytes = "data: 新完整事件\n\ndata: 半事件"u8.ToArray(); f.Recorder.Observe(stream.Response, bytes); f.Session.ResponseBytes(stream.Id, bytes);
                        await ProxyFixture.WaitUntilAsync(() => f.Session.Stream(stream.Id)?.Total == 271); await view.RefreshUiAsync(); Pump(window);
                        Assert.Equal(1, page.EventReading.Pending); Assert.Contains("淘汰 15", page.StreamStatus); Assert.Contains("半事件", page.BodyText); Assert.True(page.Follow);
                        ProbeEvidence.Write("ux-reading", "stream", new { total = f.Session.Stream(stream.Id)!.Total, retained = page.Events.Count, pending = page.EventReading.Pending, listFollowing = page.Follow, eventFollowing = page.EventReading.Following, observed = f.Recorder.Snapshot(stream.Response).ObservedBytes });
                        break;
                    case "json-formatted": Assert.Contains("\n", page.Body.Text); Assert.Contains("中文内容", page.Body.Text); break;
                    case "json-tree":
                        var mode = view.FindControl<ComboBox>("BodyModeBox")!; mode.Focus(); Press(window, Key.Down); Assert.Equal(1, page.Body.Mode); Assert.True(page.ShowJsonTree); Assert.False(page.ShowBodyText); break;
                    case "partial":
                        await f.AddAsync("有限前缀，传输仍在继续。"u8.ToArray(), false); await page.RefreshAsync(); list.SelectedIndex = 0; await page.DetailTask; Assert.Contains("Partial", page.BodySummary); break;
                    case "invalid-filter":
                        Click(window, Button(view, "筛选")); var input = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "开始时间（含）"); input.Focus(); window.KeyTextInput("invalid");
                        await Task.Delay(220); await page.RefreshAsync(); Assert.Equal(80, page.Rows.Count); Assert.NotEmpty(page.FilterError); Press(window, Key.Escape); break;
                    case "readonly":
                        page.ArchivePath = Path.Combine(f.Root, "ui-v4"); Click(window, main.FindControl<Button>("SaveSnapshotButton")!); Click(window, view.FindControl<Button>("SaveButton")!); await page.ExecuteFileCommand.ExecutionTask!;
                        page.BeginFileOperation(WorkspaceFileKind.Open); Pump(window); Click(window, view.FindControl<Button>("SaveButton")!); await page.ExecuteFileCommand.ExecutionTask!;
                        await Task.Delay(220); await page.RefreshAsync(); list.SelectedIndex = 0; await page.DetailTask; Assert.True(page.IsReadOnly); Assert.True(f.Session.Snapshot().IsRunning); break;
                    case "settings-fault":
                        var height = view.Bounds.Height; Click(window, main.FindControl<Button>("SettingsButton")!); Assert.Equal(height, view.Bounds.Height);
                        using (var occupied = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
                        {
                            occupied.Start(); panel.Port = ((System.Net.IPEndPoint)occupied.LocalEndpoint).Port.ToString();
                            Click(window, main.FindControl<Button>("StartProxyButton")!); await panel.StartCommand.ExecutionTask!;
                            Assert.Equal(ProxyState.Faulted, owner.Status.State); Assert.True(panel.StartVisible); Assert.Contains("失败", panel.Message);
                        }
                        break;
                }
                Pump(window); Screenshot(window, behavior, new { behavior, anchorError, beforeId, afterId, listFollow = page.Follow, eventFollow = page.EventReading.Following, pending = page.ListReading.Pending, page.BodySummary });
                if (behavior == "list-paused")
                {
                    var selected = page.Selected!.RecordId; Click(window, view.FindControl<Button>("FollowButton")!); await view.RefreshUiAsync(); Pump(window);
                    Assert.True(page.Follow); Assert.Equal(selected, page.Selected!.RecordId);
                    ProbeEvidence.Write("ux-reading", "anchor", new { beforeId, afterId, anchorError, selected, input = "MouseWheel", pending = 1, resumed = page.Follow, restorations = view.ScrollRestorations });
                }
            }
            finally { window.Close(); }
            return true;
        }, default);
    }
    [Fact]
    public async Task 快捷键逐层关闭宽窄切换以及重新附着保留选择和模式()
    {
        await M1CaptureUiTests.Ui.Value.Dispatch(async () =>
        {
            await using var f = new M1SessionFixture(); await Populate(f); var store = new CaptureSessionStore(); store.Publish(f.Session); await using var archive = new FileCaptureArchiveStore();
            await using var owner = new ProxySessionCoordinator(() => new TitaniumProxyRuntime()); using var panel = new ProxyPanelModel(owner);
            using var page = new CaptureWorkspace(store, archive, new(store.ContentBudget)); using var document = new MainDocument(panel, page);
            var main = new MainView { DataContext = document }; var window = new Window { Width = 900, Height = 600, Content = main };
            try
            {
                window.Show(); await page.RefreshAsync(); Pump(window); var view = main.GetVisualDescendants().OfType<CaptureView>().Single(); var list = view.FindControl<ListBox>("TrafficList")!;
                list.SelectedIndex = 2; await page.DetailTask; Pump(window); var id = page.Selected!.RecordId;
                Click(window, Button(view, "查找")); var find = view.FindControl<TextBox>("BodyFindBox")!; Assert.True(find.IsFocused); window.KeyTextInput("中文"); Click(window, Button(view, "下一处")); Assert.True(page.Body.MatchStart >= 0);
                var text = view.FindControl<TextBox>("BodyTextBox")!; Assert.Equal("中文", text.SelectedText);
                Click(window, Button(view, "筛选")); page.BeginFileOperation(WorkspaceFileKind.Export); Pump(window); view.FindControl<TextBox>("ArchivePathBox")!.Focus();
                Press(window, Key.Escape); Assert.False(page.FileDialogVisible); Assert.True(page.AdvancedVisible); Press(window, Key.Escape); Assert.False(page.AdvancedVisible); Assert.True(page.Body.FindVisible);
                Press(window, Key.Escape); Assert.False(page.Body.FindVisible);
                Click(window, main.FindControl<Button>("SettingsButton")!); Press(window, Key.Escape); Assert.False(main.FindControl<Border>("SettingsOverlay")!.IsVisible); Assert.True(main.FindControl<Button>("SettingsButton")!.IsFocused);
                // 设置关闭后焦点在顶部工具栏，仍应走当前 Document 的局部搜索路由。
                Press(window, Key.L, RawInputModifiers.Control); Assert.True(view.FindControl<TextBox>("FilterBox")!.IsFocused);
                // 真正拖动分隔条到两端，验证公开 GridSplitter 的尺寸约束，再检查窄屏往返。
                var splitter = view.FindControl<GridSplitter>("PaneSplitter")!; var grid = view.FindControl<Grid>("MainGrid")!;
                foreach (var x in new[] { 20d, 850d })
                {
                    var point = splitter.TranslatePoint(new Point(3, 100), window)!.Value;
                    window.MouseDown(point, MouseButton.Left); window.MouseMove(new Point(x, point.Y)); window.MouseUp(new Point(x, point.Y), MouseButton.Left); Pump(window);
                    Assert.True(grid.ColumnDefinitions[0].ActualWidth >= 260); Assert.True(grid.ColumnDefinitions[2].ActualWidth >= 360);
                }
                page.ShowDetails = true; page.Body.Mode = 2; window.Width = 520; Pump(window); Assert.True(page.IsCompact); Assert.Equal(id, page.Selected!.RecordId);
                Click(window, Button(view, "返回")); Assert.False(page.ShowDetails); Assert.Equal(id, page.Selected.RecordId);
                window.Width = 900; Pump(window); Assert.False(page.IsCompact); Assert.Equal(2, page.Body.Mode);
                for (var i = 0; i < 20; i++) { window.Content = null; window.Content = main; Pump(window); }
                await view.RefreshUiAsync(); Assert.Equal(id, page.Selected.RecordId); Assert.Equal(ProxyState.Stopped, owner.Status.State);
                // 经生产“浏览”按钮进入窄平台端口；取消后仍需用户确认，不能偷偷调用归档服务。
                var picker = new NetWorkTrafficPlugin.Tests.Capture.UXWorkspaceTests.Picker(); view.FilePicker = picker;
                main.FindControl<Button>("SaveSnapshotButton")!.Focus(); Press(window, Key.F, RawInputModifiers.Control); Assert.True(page.Body.FindVisible);
                page.ArchivePath = Path.Combine(f.Root, "cancelled-from-ui"); Click(window, main.FindControl<Button>("SaveSnapshotButton")!);
                Click(window, Button(view, "浏览…")); Assert.Equal(1, picker.Calls); Assert.Equal(WorkspaceFileKind.Save, picker.Kind);
                picker.Result.SetResult(null); await ProxyFixture.WaitUntilAsync(() => !page.PickingFile); Assert.False(Directory.Exists(page.ArchivePath));
                Press(window, Key.Escape); Assert.False(page.FileDialogVisible); Assert.True(page.Body.FindVisible);
                Press(window, Key.Escape); Assert.False(page.Body.FindVisible);
                var tabs = view.FindControl<TabControl>("DetailsTabs")!; tabs.SelectedIndex = 1; Pump(window); Click(window, Button(view, "复制地址"));
                var clipboard = TopLevel.GetTopLevel(view)!.Clipboard!; Assert.DoesNotContain("synthetic-secret", await clipboard.TryGetTextAsync());
                tabs.SelectedIndex = 2; Pump(window); var reveal = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.IsEffectivelyVisible);
                var revealPoint = reveal.TranslatePoint(new Point(12, reveal.Bounds.Height / 2), window)!.Value;
                window.MouseDown(revealPoint, MouseButton.Left); window.MouseUp(revealPoint, MouseButton.Left); Assert.True(page.RevealSensitive);
                tabs.SelectedIndex = 1; Pump(window); Click(window, Button(view, "复制地址")); Assert.Contains("synthetic-secret", await clipboard.TryGetTextAsync());
                ProbeEvidence.Write("ux-lifetime", "reattach", new { rounds = 20, selectedBefore = id, selectedAfter = page.Selected.RecordId, mode = page.Body.Mode, running = f.Session.Snapshot().IsRunning });
            }
            finally { window.Close(); }
            page.Dispose(); Assert.Equal(0, store.ContentBudget.Reserved); return true;
        }, default);
    }
}


