# NetWorkTrafficPlugin

HTTP/HTTPS 流量工作台插件，规划强化 DSL 字段提取与分组、调用时间线、请求改写、手工请求和编辑重发。
当前已实现 V2 / M0 代理技术基线与 V3 / M1 抓包工作台，显示名称为“HTTP 工作台”。真实 Host、真实客户端及人工验收待执行，自动结果见专用实施记录。
M1 提供双向捕获、筛选、内容预览、SSE/NDJSON、受配额落盘、快照保存与只读回看。HTTP/2 空闲流按用户确认的范围降级为仅提示；保留客户端单流取消和全局停止。

| 阅读目的 | 文档 |
| --- | --- |
| M1 当前能力、预算和限制 | [M1 运行说明](docs/reference/network-traffic-m1-runtime.md) |
| M1 接入、查看、保存与开发门禁 | [M1 快速开始](docs/quick-start/network-traffic-m1.md) |
| V3 实际验证、范围调整与人工待验 | [V3 实施记录](docs/archive/2026-09-29-v3-m1-implementation.md) |
| 当前代理行为与限制 | [M0 运行说明](docs/reference/network-traffic-m0-runtime.md) |
| 本地门禁与最小诊断操作 | [M0 开发快速开始](docs/quick-start/network-traffic-m0.md) |
| V2 实施结果与待验事项 | [2026-09-29 实施记录](docs/archive/2026-09-29-v2-m0-implementation.md) |
| 了解产品定位、业务场景和 DSL 方向 | [产品与规划方案](docs/roadmap/network-traffic-product-plan.md) |
| 查看每阶段实现什么、依赖和完成条件 | [M0–M6 能力路线图](docs/roadmap/network-traffic-capability-roadmap.md) |
| 按 V2-01～10 执行 M0 技术基线 | [V2 / M0 执行计划](docs/roadmap/network-traffic-v2-m0-technical-baseline-plan.md) |
| 查看 M0 的架构、资源与生命周期设计 | [V2 技术设计](docs/design/network-traffic-v2-m0-technical-design.md) |
| 查看 M0 单测、协议探针与本地开发门禁要求 | [V2 专用验证矩阵](docs/maintenance/network-traffic-v2-m0-verification-plan.md) |
| 按 V3-01～14 实现 M1 可用抓包 | [V3 / M1 执行计划](docs/roadmap/network-traffic-v3-m1-capture-plan.md) |
| 查看 M1 的 SOLID、双向捕获、内容与存档设计 | [V3 专项技术设计](docs/design/network-traffic-v3-m1-capture-design.md) |
| 查看 M1 单测、生产 UI、资源与开发门禁要求 | [V3 专用验证矩阵](docs/maintenance/network-traffic-v3-m1-verification-plan.md) |
| 查看各阶段如何验证 | [分阶段验证计划](docs/maintenance/network-traffic-verification-plan.md) |
| 查看当前工程与开发方式 | [文档导航与快速开始](docs/README.md) |

这是由 `myavalonia-plugin` 创建的 Managed Plugin 解决方案。真实交付物是
`src/NetWorkTrafficPlugin.Plugin`；`Standalone` 只负责快速预览同一份 View、ViewModel 与业务代码。

V2 / M0 生产代理、最小诊断、单元/协议测试和本地开发门禁已实现，完整本地开发门禁已通过，结果见实施记录。M0 以 SOLID 为首要规定，采用朴素设计和详细中文注释；仅安排本地开发验证，不使用 AIFLOW、Windows CI 或发布门禁。下方打包/部署命令保留为通用工程参考，不属于本轮实施或 M0 开发门禁。

V3 / M1 按 V3-01～14 实施，补齐单元测试、失败注入、生产 UI 和资源测量。当前检查入口支持 `-Mode Docs` / `-Mode M0` / `-Mode M1`，默认仍为 M0；M1 完整继承 M0 后运行专项资源与桌面 UI 检查。只执行本地 Debug 开发门禁，发布门禁留到正式发布时使用。

> 第一次开始开发前，请先阅读 [项目文档与快速开始](docs/README.md)。其中说明了插件、Standalone、测试与技术探针宿主和
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
