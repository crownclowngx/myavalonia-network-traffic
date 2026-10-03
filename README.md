# NetWorkTrafficPlugin

HTTP/HTTPS 流量工作台插件，支持 DSL 字段提取与分组、调用时间线；后续规划请求改写、手工请求和编辑重发。
当前已实现 V2 / M0 代理技术基线、V3 / M1 抓包工作台与 V5 / M2 DSL 分组，显示名称为“HTTP 工作台”。真实 Host、真实客户端及人工验收待执行，自动结果见专用实施记录。
M1 提供双向捕获、筛选、内容预览、SSE/NDJSON、受配额落盘、快照保存与只读回看。HTTP/2 空闲流按用户确认的范围降级为仅提示；保留客户端单流取消和全局停止。

V4 / M1 工作台 UX 紧凑化与阅读体验已实现，完整本地 UX 门禁通过（172 个唯一测试、143 项同轮场景）；V4-01～V4-12 的结果和人工未验范围见 [V4 实施记录](docs/archive/2026-09-30-v4-m1-ux-implementation.md)。重点是双行列表、紧凑工具栏、独立跟随、正文优先、分栏/窄屏导航和明确的快照操作，配套 40 项 V4 业务验证场景。V4 沿用 SOLID 首要规定、朴素设计和详细中文注释，仅执行本地 Debug 开发验证，未使用 AIFLOW、Windows CI 或发布门禁。

V5 / M2 DSL 分组已实现：六位置字段提取、三值过滤、组合键分组、调用时间线、规则修订、保存视图与历史重算。`-Mode M2` 完整本地开发门禁通过：236个测试、207项同轮场景、445个门禁拒绝检查；实际结果与人工未验见 [V5 实施记录](docs/archive/2026-09-30-v5-m2-implementation.md)。以 SOLID 为首要规定，朴素设计和中文注释；不使用 AIFLOW、Windows CI 或发布门禁。

V6 / M3 已实现共享表达式前端、v2 编译/纯规划、独立修订库和快照协调基础；**实际代理与 M3 门禁尚未接通**，受严格 Continue、正文取消和物理预读契约阻断。实际进度见 [V6 基础实施记录](docs/archive/2026-10-03-v6-m3-foundation.md)，语法见 [DSL 第四章](docs/dsl/04-rewrite-foundation.md)：V6-01～16 规定域名映射、请求修改、修订热切、原始/有效追查及本地验证。配套 72 项新业务场景，计划同轮继承前置后合计 279 项；这不是已通过数量。M3 设计沿用改写运行仅 HTTP/1.1 的协议边界，SOLID 为首要规定，中文注释需解释设计思路；不使用 AIFLOW、Windows CI 或发布门禁。

本期完整M2兼容回归已通过：316个测试（新增80）、207项同轮场景、445个门禁拒绝检查；源码摘要前后一致。该结果不代表完整M3或人工验收通过。

V7 / M4 请求调试已补充详细执行方案，**尚未实施**：V7-01～18 覆盖手工请求、原始/有效快照提取、环境变量、最终预览、一次发送、模板/历史与有界对比。配套80项新增业务场景，完整门禁须在M3闭合后同轮覆盖359项；当前没有 `-Mode M4` 入口。SOLID为首要规定，朴素模式与详细中文注释；仅安排本地Debug开发验证，不使用AIFLOW、Windows CI或发布门禁。

| 阅读目的 | 文档 |
| --- | --- |
| V7-01～18：M4 请求调试的实施顺序、交付文件和完成条件 | [V7 / M4 执行计划](docs/roadmap/network-traffic-v7-m4-request-debugging-plan.md) |
| 草稿/变量、冻结预览、发送状态、来源、模板与对比目标契约 | [V7 请求调试专项设计](docs/design/network-traffic-v7-m4-request-debugging-design.md) |
| 80项新增场景、359项累计要求、门禁反例与人工边界 | [V7 M4 专用验证矩阵](docs/maintenance/network-traffic-v7-m4-verification-plan.md) |
| V6-01～16：M3 DSL 改写的任务、文件交付与完成条件 | [V6 / M3 执行计划](docs/roadmap/network-traffic-v6-m3-dsl-rewrite-plan.md) |
| DSL v2、动作、失败、发送前边界与双快照的目标契约 | [V6 改写专项设计](docs/design/network-traffic-v6-m3-dsl-rewrite-design.md) |
| 72 项新场景、累计回归和门禁正反例要求 | [V6 M3 专用验证矩阵](docs/maintenance/network-traffic-v6-m3-verification-plan.md) |
| 学习 DSL、查询完整语法与理解设计意图 | [DSL 使用与设计手册](docs/dsl/README.md) |
| M2 当前语义、限制与预算 | [M2 DSL 说明](docs/reference/network-traffic-m2-dsl.md) |
| M2 使用、保存视图与验证 | [M2 快速开始](docs/quick-start/network-traffic-m2.md) |
| V5-01～14：M2 DSL 分组实施顺序与完成条件 | [V5 / M2 执行计划](docs/roadmap/network-traffic-v5-m2-dsl-grouping-plan.md) |
| DSL v1、值状态、六位置提取、时间线与保存视图契约 | [V5 DSL 与分组专项设计](docs/design/network-traffic-v5-m2-dsl-grouping-design.md) |
| 64 项 V5 场景、单元测试、累计回归与门禁反例 | [V5 M2 专用验证矩阵](docs/maintenance/network-traffic-v5-m2-verification-plan.md) |
| V4-01～12 紧凑度与阅读体验 | [V4 / M1 UX 执行计划](docs/roadmap/network-traffic-v4-m1-ux-plan.md) |
| 查看布局、跟随、筛选、详情与 SOLID 契约 | [V4 UX 专项设计](docs/design/network-traffic-v4-m1-ux-design.md) |
| 查看 40 项 V4 场景及本地开发门禁设计 | [V4 UX 专用验证矩阵](docs/maintenance/network-traffic-v4-m1-ux-verification-plan.md) |
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

V3 / M1 按 V3-01～14 实施，补齐单元测试、失败注入、生产 UI 和资源测量。当前检查入口支持 `-Mode Docs` / `-Mode M0` / `-Mode M1` / `-Mode UX` / `-Mode M2`，默认仍为 M0；M1 完整继承 M0 后运行专项资源与桌面 UI 检查。只执行本地 Debug 开发门禁，发布门禁留到正式发布时使用。

V4 使用 `pwsh -NoProfile -File tools/verify-development.ps1 -Mode UX`，同轮完整继承 M0/M1 后校验 40 项 V4 场景、20 张专项截图、阅读锚点与桌面资源。默认仍为 M0；Docs 只检查文档。

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
