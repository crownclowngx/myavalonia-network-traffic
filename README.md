# NetWorkTrafficPlugin

这是由 `myavalonia-plugin` 创建的 Managed Plugin 解决方案。真实交付物是
`src/NetWorkTrafficPlugin.Plugin`；`Standalone` 只负责快速预览同一份 View、ViewModel 与业务代码。

> 第一次开始开发前，请先阅读 [项目文档与快速开始](docs/README.md)。其中说明了三个子项目和
> Standalone 窗口的职责、接入真实 Host 的边界，以及临时部署和正式 ZIP 发布流程。

```powershell
dotnet restore
dotnet build
dotnet run --project src/NetWorkTrafficPlugin.Standalone
dotnet msbuild src/NetWorkTrafficPlugin.Plugin/NetWorkTrafficPlugin.Plugin.csproj -t:BuildManagedPluginPackage -p:Configuration=Release
```

要在真实 Host 中调试，请显式提供 Host 的 `Controls` 目录：

```powershell
dotnet msbuild src/NetWorkTrafficPlugin.Plugin/NetWorkTrafficPlugin.Plugin.csproj `
  -t:DeployManagedPlugin `
  -p:ManagedPluginDeployRoot=C:\Path\To\Host\Controls
```

Standalone 只能验证界面和插件自身对象图；manifest、加载上下文、Document Scope、Dock、Tool 和
生命周期必须使用真实 Host 做最终验收。

模板包含一条不注册快捷键的最小 Document Command 示例。设计边界、Target 适配和测试清单见
[Workbench Command 开发说明](docs/workbench-commands.md)。
