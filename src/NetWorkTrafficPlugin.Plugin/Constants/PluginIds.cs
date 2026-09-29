using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;

namespace NetWorkTrafficPlugin.Constants;

public static class PluginIds
{
    public static readonly PluginId Plugin = new("myavalonia.plugin.network.traffic");
    public static readonly ToolTypeId ProxyStatus = new("myavalonia.plugin.network.traffic.tool.proxy-status");

    public static readonly DocumentTypeId MainDocument =
        new("myavalonia.plugin.network.traffic.document.main");

    /// <summary>获取模板示例文档向工作台公开的唯一命令身份。</summary>
    /// <remarks>
    /// 命令身份使用插件自己的命名空间，不能复用显示名称或 <see cref="System.Windows.Input.ICommand"/>
    /// 实例作为跨菜单、快捷键和命令面板的公共身份。
    /// </remarks>
    public static readonly CommandId ApplyWorkbenchMessage =
        new("myavalonia.plugin.network.traffic.command.main.apply-workbench-message");

    /// <summary>获取示例命令在 Host Tools 共享菜单中的独立展示身份。</summary>
    public static readonly CommandPlacementId ApplyWorkbenchMessageMenu =
        new("myavalonia.plugin.network.traffic.command-placement.menu.tools.apply-workbench-message");
}
