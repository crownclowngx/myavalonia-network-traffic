using Avalonia.Controls;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
