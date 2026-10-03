using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NetWorkTrafficPlugin.Features.Capture;

namespace NetWorkTrafficPlugin.Features.Main;

/// <summary>外框只负责可见期间刷新代理摘要、临时设置层与焦点；不取得代理所有权。</summary>
public partial class MainView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public MainView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => { if (DataContext is MainDocument document) { document.Proxy.Refresh(); document.SyncAnalysisSource(); } };
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape && SettingsOverlay.IsVisible)
            { SettingsOverlay.IsVisible = false; SettingsButton.Focus(); args.Handled = true; }
        }, RoutingStrategies.Tunnel);
        KeyDown += (_, args) =>
        {
            if (!SettingsOverlay.IsVisible && (DataContext as MainDocument)?.ShowCapture == true) CaptureWorkspaceView.RoutePageKey(args);
        };
    }
    private void ToggleSettings(object? sender, RoutedEventArgs args)
    {
        SettingsOverlay.IsVisible = !SettingsOverlay.IsVisible;
        if (!SettingsOverlay.IsVisible) SettingsButton.Focus();
    }
    private void SaveSnapshot(object? sender, RoutedEventArgs args) => (DataContext as MainDocument)?.Capture?.BeginFileOperation(WorkspaceFileKind.Save);
    private void OpenArchive(object? sender, RoutedEventArgs args) => (DataContext as MainDocument)?.Capture?.BeginFileOperation(WorkspaceFileKind.Open);
    private void RecoverArchive(object? sender, RoutedEventArgs args) => (DataContext as MainDocument)?.Capture?.BeginFileOperation(WorkspaceFileKind.Recover);
    private void OpenRequests(object? sender, RoutedEventArgs args) { if (DataContext is MainDocument document) document.RequestsVisible = document.HasRequests; }
    private void OpenCapture(object? sender, RoutedEventArgs args) { if (DataContext is MainDocument document) { document.RequestsVisible = false; document.AnalysisVisible = false; } }
    private async void ExtractRequest(object? sender, RoutedEventArgs args) { if (DataContext is MainDocument document) await document.ExtractDebugRequestAsync(); }
    private async void CopyAddress(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainDocument document) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && document.Proxy.HasAddress)
                await clipboard.SetTextAsync(document.Proxy.Address);
            else document.Proxy.Message = "当前宿主没有剪贴板。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { document.Proxy.Message = "无法复制代理地址。"; }
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && SettingsOverlay is not null)
        {
            SettingsOverlay.Width = Math.Min(400, Math.Max(240, Bounds.Width - 16));
            // 常用控制在窄屏继续常驻，地址可压缩；文件操作不新增一整行。
            if (SaveSnapshotButton is not null) SaveSnapshotButton.Content = Bounds.Width < 650 ? "保存" : "保存快照";
        }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    { base.OnAttachedToVisualTree(args); (DataContext as MainDocument)?.Proxy.Refresh(); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    { _timer.Stop(); base.OnDetachedFromVisualTree(args); }
}
