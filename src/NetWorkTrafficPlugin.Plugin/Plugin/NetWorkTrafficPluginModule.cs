using MyAvaloniaManagement.PluginSdk.UI;
using MyAvaloniaManagement.Icons;
using NetWorkTrafficPlugin.Constants;
using NetWorkTrafficPlugin.Features.Main;
using NetWorkTrafficPlugin.Features.ProxyStatus;
using MyAvaloniaManagement.PluginSdk;

namespace NetWorkTrafficPlugin.Plugin;

public sealed class NetWorkTrafficPluginModule : IPluginModule
{
    public void Configure(IPluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        registration.Services.AddNetWorkTrafficPluginServices();
        registration.UseLifecycle<NetWorkTrafficPluginLifecycle>();
        // 将插件自选版本的公共资源复制为 SDK 数据；Host 无需升级资源包即可显示这个别名。
        var asset = CommonIcons.TextCheck;
        var icon = registration.AddIcon("main-document", new VectorIconDefinition(
            asset.PathData, asset.ViewBoxWidth, asset.ViewBoxHeight));
        registration.AddDocument<MainDocument, MainView>(
            new DocumentDescriptor(
                PluginIds.MainDocument,
                "HTTP 工作台",
                "实时抓包、内容查看和保存回看",
                "NetWorkTrafficPlugin", iconPath: icon));
        registration.AddTool<ProxyStatusTool, ProxyStatusView>(new ToolDescriptor(
            PluginIds.ProxyStatus, "代理状态", "查看共享运行状态并停止代理", ToolDockSide.Right, ToolCloseBehavior.Hide, iconPath: icon));

        // Command 注册只冻结稳定身份、展示文本和目标 Document 类型，不保存 MainDocument 实例、
        // ICommand、回调或 Provider。真正执行时由 Host 路由到当前活动的 MainDocument 实例。
        registration.AddDocumentCommand(
            new CommandDescriptor(
                PluginIds.ApplyWorkbenchMessage,
                "应用工作台示例消息",
                "在当前示例文档实例中执行一条可等待的工作台命令。"),
            PluginIds.MainDocument);
        registration.AddMenuCommandContribution(
            new MenuCommandContributionDescriptor(
                PluginIds.ApplyWorkbenchMessageMenu,
                PluginIds.ApplyWorkbenchMessage,
                WorkbenchMenuLocations.ToolsShared,
                group: "demo",
                order: 0,
                targetUnavailableBehavior: MenuCommandTargetUnavailableBehavior.Disable));

        // 模板故意不默认注册快捷键。快捷键是全局稀缺资源，真实插件只有在产品语义稳定、
        // 冲突政策和用户预期经过评审后，才应显式增加 KeyBindingContributionDescriptor。
    }
}
