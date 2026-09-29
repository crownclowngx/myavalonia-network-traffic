# NetWorkTrafficPlugin 文档导航与开发快速开始

> 更新日期：2026-09-29。V2 / M0 已实现；V3 / M1 执行文档已编写、业务未开始。当前行为、自动结果及人工未验项见以下专用文档。

## 产品规划与当前状态

| 阅读目的 | 入口 |
| --- | --- |
| 当前行为、依赖边界和协议限制 | [M0 运行说明](reference/network-traffic-m0-runtime.md) |
| 本地门禁、诊断页和合成演示 | [M0 快速开始](quick-start/network-traffic-m0.md) |
| 实施证据、设计审查与人工未验项 | [2026-09-29 实施记录](archive/2026-09-29-v2-m0-implementation.md) |
| 产品定位、需求、界面形态、数据模型、DSL 与边界 | [产品与规划方案](roadmap/network-traffic-product-plan.md) |
| M0–M6 阶段范围、交付物、依赖和验收条件 | [能力路线图](roadmap/network-traffic-capability-roadmap.md) |
| V2-01～10 任务、实施顺序、文件交付与完成条件 | [V2 / M0 技术基线执行计划](roadmap/network-traffic-v2-m0-technical-baseline-plan.md) |
| SOLID、代理适配、TLS、流式预算、目标映射与寿命 | [V2 / M0 专项技术设计](design/network-traffic-v2-m0-technical-design.md) |
| 单元测试、真实协议探针、故障注入及开发门禁 | [V2 / M0 专用验证矩阵](maintenance/network-traffic-v2-m0-verification-plan.md) |
| V3-01～14 任务、依赖、交付文件与完成条件 | [V3 / M1 可用抓包执行计划](roadmap/network-traffic-v3-m1-capture-plan.md) |
| M1 SOLID、双向捕获、流式预览、预算及保存回看 | [V3 / M1 专项技术设计](design/network-traffic-v3-m1-capture-design.md) |
| M1 的 53 项业务场景、单测、UI、故障注入与开发门禁 | [V3 / M1 专用验证矩阵](maintenance/network-traffic-v3-m1-verification-plan.md) |
| 协议、规则、资源、UI、Host 的分阶段验证 | [专用验证计划](maintenance/network-traffic-verification-plan.md) |

当前源码包含 Titanium 7.0.13 生产适配、共享协调器、有限记录、最小 Document/Tool、受控测试和本地门禁。M1 已补充 V3 执行文档，业务实现未开始；M2–M6 仍为后续规划。
正式工程及插件身份保持不变，候选显示名称为 `HttpLab / HTTP 工作台`，V3 计划采用“HTTP 工作台”。V1 是产品方案，V2 是 M0 执行方案，V3 是 M1 执行方案，均不是包版本。M0 已完成实现与开发验证，真实 Host、真实客户端和人工验收独立登记；本轮仅交付 V3 文档。

规划放入 `roadmap`，专项设计放入 `design`，可复用验证方法放入 `maintenance`。已新增当前行为 `reference`、使用步骤
`quick-start` 和带日期的实施记录；现有模板开发文档暂保留当前位置。实现、自动验证、真实 Host、人工验收和发布分别记录。

## 开发快速开始

V2 / M0 首要满足 SOLID，设计模式朴素使用，关键实现与测试写详细中文注释。不使用 AIFLOW；只运行本地 Debug 开发检查，不新增或运行 Windows CI、发布门禁。现行完整入口为 pwsh -NoProfile -File tools/verify-development.ps1 -Mode M0，包含十分钟持续流；Docs 模式只检查文档。以下是现有模板开发流程，后文发布说明仅在正式发布阶段适用。

V3 / M1 同样以 SOLID 为首要规定，并要求单元测试、生产 UI、资源测量及门禁正反例齐全。计划中的 `-Mode M1` 尚未接入，默认值仍为 M0；本次文档验证使用 `pwsh -NoProfile -File tools/verify-development.ps1 -Mode Docs`，不代表 M1 业务通过。

本解决方案用于开发 `myavalonia.plugin.network.traffic` Managed Plugin。它把真实插件、独立 Avalonia 开发窗口和
自动化测试放在同一个解决方案中，使界面与业务代码既能快速预览，也能由 MyAvaloniaManagement Host
按正式插件协议加载。

## 项目结构

```text
NetWorkTrafficPlugin/
├─ NetWorkTrafficPlugin.slnx
├─ src/
│  ├─ NetWorkTrafficPlugin.Plugin/       # 唯一真实插件程序集和正式交付内容
│  └─ NetWorkTrafficPlugin.Standalone/   # 只供本地开发的 Avalonia 窗口
├─ tests/
│  └─ NetWorkTrafficPlugin.Tests/        # 插件业务、状态和注册行为测试
└─ docs/                       # 当前项目随模板生成的开发说明
```

`NetWorkTrafficPlugin.Plugin` 是唯一正式插件项目。Standalone 和 Tests 都直接引用它，不能各自复制一套 View、
ViewModel、服务或贡献清单。

## 最短开发流程

在解决方案根目录打开 PowerShell：

```powershell
dotnet restore
dotnet build -c Debug -warnaserror
dotnet test -c Debug --no-build --filter Category!=ResourceProbe
dotnet run --project src/NetWorkTrafficPlugin.Standalone
```

Standalone 适合快速检查 AXAML、编译绑定、命令和插件自身对象图。写到可以联调时，再把干净的插件目录
部署到真实 Host；发布前则必须生成正式 ZIP。不要把 Standalone 能运行当成 Host 验收已经通过。

## 接下来阅读

1. [项目、Host 与 Standalone 窗口职责](project-and-window-responsibilities.md)
2. [临时部署、正式发布与验收](deployment-and-release.md)
3. [Workflow Action Provider 与 Consumer 接入](workflow-actions.md)
4. [Workbench Command 开发说明](workbench-commands.md)

## 开发前记住

- `myavalonia.plugin.network.traffic` 是持久身份，发布后不要因为显示名、项目名或文件夹改名而改变它。
- manifest 由 Build 包生成，不要手写或复制一份长期维护。
- 插件只通过公开 Plugin SDK 接入 Host，不引用 Host 内部项目。
- 新增插件运行时 NuGet 包时，要同时更新根目录 `Directory.Packages.props`、Plugin 项目的
  `PackageReference` 和 `ManagedPluginPrivatePackage`；完整示例见部署文档。
- 当前交付目标是 Windows x64；插件替换后必须完整重启 Host，不支持热更新。
- Workflow Action Provider 与 Consumer 是两种互斥角色，选择前先阅读专项文档，不要在同一插件中同时注册。
- Workbench Command 只提升跨工作台有价值的用户意图；模板示例默认不占用快捷键。


- [公共资源与专属图标](plugin-icons.md)：注册、画布、独立预览与私有包交付。
