using Avalonia.Controls;
using Avalonia.Media;
using MyAvaloniaManagement.Icons;

namespace NetWorkTrafficPlugin.Features.Main;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        // 页面和 Standalone 直接使用纯资源包，不依赖 Host 注册表或伪造注册上下文。
        var asset = CommonIcons.TextCheck;
        IconCanvas.Width = asset.ViewBoxWidth;
        IconCanvas.Height = asset.ViewBoxHeight;
        DocumentIcon.Data = Geometry.Parse(asset.PathData);
    }
}
