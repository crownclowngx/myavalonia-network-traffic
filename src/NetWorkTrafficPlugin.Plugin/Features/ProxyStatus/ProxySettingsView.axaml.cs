using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace NetWorkTrafficPlugin.Features.ProxyStatus;

/// <summary>设置只复用配置与接入内容，不拥有代理寿命或独立刷新器。</summary>
public partial class ProxySettingsView : UserControl
{
    public ProxySettingsView() => InitializeComponent();
    private async void CopyCertificate(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ProxyPanelModel model) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) { model.Message = "当前宿主不提供剪贴板。"; return; }
            if (model.CertificatePem.Length == 0) { model.Message = "请先启动代理。"; return; }
            await clipboard.SetTextAsync(model.CertificatePem);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { model.Message = "当前环境无法写入剪贴板。"; }
    }
}
