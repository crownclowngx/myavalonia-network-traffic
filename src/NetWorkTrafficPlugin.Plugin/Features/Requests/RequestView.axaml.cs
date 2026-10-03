using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace NetWorkTrafficPlugin.Features.Requests;

/// <summary>View只负责系统文件选择和可见期间刷新，文件冻结和网络执行都由应用用例拥有。</summary>
public partial class RequestView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public RequestView() { InitializeComponent(); _timer.Tick += (_, _) => (DataContext as RequestWorkspace)?.Refresh(); }
    private async void SelectFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RequestWorkspace page || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new() { Title = "选择完整原始文件（最多64MiB）", AllowMultiple = false });
            if (files.Count == 1 && files[0].TryGetLocalPath() is { } path) await page.ImportFileAsync(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException) { page.Status = "无法选择文件：" + ex.GetType().Name; }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _timer.Stop(); base.OnDetachedFromVisualTree(e); }
}
