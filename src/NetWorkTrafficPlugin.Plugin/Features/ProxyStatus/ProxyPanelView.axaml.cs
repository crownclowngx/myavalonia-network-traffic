using Avalonia.Controls;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;

namespace NetWorkTrafficPlugin.Features.ProxyStatus;

/// <summary>仅可见时每秒刷新诊断投影；离开可视树即移除计时器，网络线程不触碰 Avalonia 对象。</summary>
public partial class ProxyPanelView : UserControl
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    public ProxyPanelView()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => (DataContext as ProxyPanelModel)?.Refresh();
    }
    // 剪贴板属于 View 的平台能力；模型仍可在不启动 Avalonia 的单元测试中独立验证。
    private async void CopyAddress(object? sender, RoutedEventArgs e) => await CopyAsync(false);
    private async void CopyCertificate(object? sender, RoutedEventArgs e) => await CopyAsync(true);
    private async Task CopyAsync(bool certificate)
    {
        if (DataContext is not ProxyPanelModel model) return;
        try
        {
            var value = certificate ? model.CertificatePem : model.Address;
            if (string.IsNullOrEmpty(value) || value == "尚未监听") { model.Message = "请先启动代理。"; return; }
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) { model.Message = "当前宿主不提供剪贴板。"; return; }
            await clipboard.SetTextAsync(value); model.Message = certificate ? "已复制公开证书，不含私钥。" : "已复制代理地址。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { model.Message = "当前环境无法写入剪贴板。"; }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as ProxyPanelModel)?.Refresh();
        _refresh.Start();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _refresh.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
