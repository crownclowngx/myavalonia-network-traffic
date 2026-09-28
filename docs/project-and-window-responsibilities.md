# 项目、Host 与 Standalone 窗口职责

## 三个项目如何分工

| 项目 | 应当负责 | 不应负责 |
| --- | --- | --- |
| `NetWorkTrafficPlugin.Plugin` | `IPluginModule`、View、Document/Tool Model、业务服务和插件私有资源 | 启动独立桌面程序、引用 Host 内部实现 |
| `NetWorkTrafficPlugin.Standalone` | 启动 Avalonia、承载 Plugin 中的真实界面、提供开发期 Stub | 成为第二套插件实现或模拟完整 Host |
| `NetWorkTrafficPlugin.Tests` | 验证业务、初始化、状态隔离、注册和生命周期约定 | 代替真实 Host 的最终加载验收 |

只有 `.Plugin` 进入正式插件目录和 ZIP。Standalone 可执行程序、开发 Stub 与 Tests 均不得随插件发布。

## 插件如何融入主项目

`NetWorkTrafficPluginModule.Configure` 是组合入口。它通过 `IPluginRegistration`：

1. 登记插件私有服务；
2. 用稳定 Descriptor 声明 Document、Tool 等贡献；
3. 可选声明 Workbench Command 及其菜单位置，但不保存 Document 实例或执行回调；
4. 把 Model 与 View 的对应关系交给 Host。

Host 读取构建生成的 `plugin.manifest.json`，检查 Plugin SDK 兼容区间，加载唯一的 `IPluginModule`，再按
登记结果创建 Document Scope、Tool singleton、View 和 Dock 适配对象。插件不应扫描 Host、直接操作
Host 容器，或保存 `IPluginRegistration` 供运行时使用。

## Standalone/MainWindow 应当做什么

Standalone 的 `MainWindow` 是开发工作台，不是插件对 Host 暴露的正式窗体。它应当保持轻薄，只负责：

- 启动 Avalonia 并加载与插件一致的主题和必要资源；
- 创建或解析 Plugin 中的真实 Model，把真实 View 放入窗口并设置正确的 `DataContext`；
- 为插件确实需要的 Host Port 提供显式、可识别的开发 Stub；
- 在插件贡献增多时，扩展成简单的 Document/Tool 浏览工作台，但继续复用 Module 的登记事实。

它不负责证明以下行为：

- manifest 发现、Plugin SDK 兼容检查和程序集加载上下文；
- 真实 Dock 布局、Document Scope、Tool singleton、保存恢复和关闭语义；
- Host 生命周期、卸载、安装、升级或 ZIP 加载。

这些行为必须在真实 Host 中验收。不要为了让 Standalone 看起来像 Host 而复制 Host 源码或维护第二份
贡献清单。

## 项目原则

1. **稳定身份优先。** Plugin、Document 和 Tool ID 发布后保持稳定；显示名、类名和目录名可以演进。
2. **一份业务与界面事实。** View、Model、服务和 Module 都放在 Plugin 项目，Standalone 只负责承载。
3. **只依赖公开边界。** 使用 Plugin SDK、UI SDK 和声明式注册，不引用 Host 内部程序集或 Dock 实现。
4. **遵守生命周期。** Document Model 与局部服务属于每次打开的 Scope；Tool Model 属于插件级 singleton。
5. **明确缺失能力。** Standalone 缺少 Host Port 时使用显式 Stub 或显示“不支持”，不要静默传入 `null`。
6. **保持包边界干净。** Host 共享的 SDK、Avalonia、Dock 和 `Microsoft.Extensions.*` 不进入插件目录。
7. **让构建生成事实。** manifest、依赖闭包和正式 ZIP 交给 Build 包，不手工维护或压缩 `bin`。
8. **逐层验证。** 单元测试、Standalone、干净部署目录、正式 ZIP、真实 Host 五层验证不能互相替代。
