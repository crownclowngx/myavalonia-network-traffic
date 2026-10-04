# NetWorkTrafficPlugin 文档导航与开发快速开始

> 更新日期：2026-10-03。V2 / M0 与 V3 / M1 已实现；V4 / M1 UX 已实现并通过完整本地开发门禁（172 个唯一测试、143 项同轮场景），人工待验见 [V4 实施记录](archive/2026-09-30-v4-m1-ux-implementation.md)。当前行为、实际自动结果及人工未验项见以下专用文档。
> V5 / M2 DSL 分组已实现，完整M2本地开发门禁通过（236个测试、207项同轮场景、445个拒绝检查）；实际结果与人工未验见 [V5 实施记录](archive/2026-09-30-v5-m2-implementation.md)。
> V6 / M3 共享前端、v2编译/纯规划、修订库和快照协调基础已实现；实际发送、UI、存档和完整M3门禁受 [V6-02能力缺口](maintenance/network-traffic-v6-body-capability.md)阻断。见 [本期记录](archive/2026-10-03-v6-m3-foundation.md)与 [DSL第四章](dsl/04-rewrite-foundation.md)。产品页面仍使用v1，72/279是未完成的业务场景要求。
> 本期完整M2兼容回归通过：316个测试（新增80）、207项同轮场景、445个门禁拒绝检查；这不是完整M3验收。
> V7 / M4 无规则请求调试已接入，完整M4未完成。`M4Foundation`验证已实现分项，`M4`入口当前拒绝完整通过；80项/累计359项与M3前置仍待闭合，详见[V7实施记录](archive/2026-10-03-v7-m4-implementation.md)。不使用AIFLOW、Windows CI或发布门禁。

> V8 / M5 文档辅助执行方案已编写，代码与专项门禁尚未实现。V8-01～18细化结构化导入、网页/手工说明、关联、修订保护和请求草稿辅助；72项新场景、431项累计要求均未验证，完整验收依赖M3/完整M4闭合。仅本地Debug，不使用AIFLOW、Windows CI或发布门禁。

## 产品规划与当前状态

| 阅读目的 | 入口 |
| --- | --- |
| 真实 Host 类型预检失败、日志依赖修复与部署检查 | [Host 加载专用说明](maintenance/network-traffic-host-loading.md) |
| V8-01～18：M5文档辅助的实现顺序、文件交付与前置边界 | [V8 / M5 执行计划](roadmap/network-traffic-v8-m5-documentation-plan.md) |
| SOLID、OpenAPI子集、来源/匹配、手工覆盖、草稿和资源契约 | [V8 文档辅助专项设计](design/network-traffic-v8-m5-documentation-design.md) |
| 72项新场景、431项累计要求、单测分层、门禁自测与人工验收 | [V8 M5 专用验证矩阵](maintenance/network-traffic-v8-m5-verification-plan.md) |
| 当前可用请求调试、限制与开发命令 | [M4当前实现](reference/network-traffic-m4-request-debugging.md)、[快速开始](quick-start/network-traffic-m4.md)、[合成样例](examples/m4/README.md) |
| V7-01～18：手工请求、编辑重发、环境/模板、历史与对比 | [V7 / M4 执行计划](roadmap/network-traffic-v7-m4-request-debugging-plan.md) |
| SOLID、预览一致性、正文完整性、发送/取消、来源和资源所有权 | [V7 请求调试专项设计](design/network-traffic-v7-m4-request-debugging-design.md) |
| 80项M4场景、359项累计要求、单测与门禁正反例 | [V7 M4 专用验证矩阵](maintenance/network-traffic-v7-m4-verification-plan.md) |
| V6-01～16：域名映射、请求修改、修订、追查与完成条件 | [V6 / M3 执行计划](roadmap/network-traffic-v6-m3-dsl-rewrite-plan.md) |
| SOLID、计划 DSL v2、动作/冲突/失败、Body 预算、存档兼容 | [V6 改写专项设计](design/network-traffic-v6-m3-dsl-rewrite-design.md) |
| 72 项 M3 场景、累计 279 项要求、门禁自测与人工边界 | [V6 M3 专用验证矩阵](maintenance/network-traffic-v6-m3-verification-plan.md) |
| DSL 入门教程、语法手册与设计意图 | [DSL 专用文档文件夹](dsl/README.md) |
| M2 当前语义、限制与预算 | [M2 DSL 说明](reference/network-traffic-m2-dsl.md) |
| M2 分组、视图保存与开发验证 | [M2 快速开始](quick-start/network-traffic-m2.md) |
| V5-01～14：DSL 字段、过滤分组与调用时间线 | [V5 / M2 执行计划](roadmap/network-traffic-v5-m2-dsl-grouping-plan.md) |
| DSL v1 文法、状态、提取、预算、SOLID 与保存视图 | [V5 DSL 与分组专项设计](design/network-traffic-v5-m2-dsl-grouping-design.md) |
| 64 项 V5 场景、测试分层、累计回归与门禁反例 | [V5 M2 专用验证矩阵](maintenance/network-traffic-v5-m2-verification-plan.md) |
| V4-01～12：工作台紧凑化与阅读体验 | [V4 / M1 UX 执行计划](roadmap/network-traffic-v4-m1-ux-plan.md) |
| UX 的 SOLID 职责、布局量尺、跟随/筛选/详情契约 | [V4 UX 专项设计](design/network-traffic-v4-m1-ux-design.md) |
| 40 项 V4 业务场景、生产 UI、累计回归与门禁反例 | [V4 UX 专用验证矩阵](maintenance/network-traffic-v4-m1-ux-verification-plan.md) |
| M1 当前能力、记录预算和产品降级 | [M1 运行说明](reference/network-traffic-m1-runtime.md) |
| M1 客户端接入、筛选、保存回看及开发门禁 | [M1 快速开始](quick-start/network-traffic-m1.md) |
| V3 代码审查、实际结果与人工未验 | [V3 实施记录](archive/2026-09-29-v3-m1-implementation.md) |
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

当前源码包含 Titanium 7.0.13 生产适配、共享协调器、双向有界捕获、生产 Document/Tool、内容预览、原生存档、受控协议/资源/UI 测试和本地门禁。HTTP/2 空闲流仅提示，不自动取消；M2 的 DSL 分组已实现，M3仅基础代码已落地、实际接入未完成；M4独立无规则部分已接通，完整M4和M5–M6仍待推进。
正式工程及插件身份保持不变，V3 采用“HTTP 工作台”。V1 是产品方案，V2 是 M0 执行方案，V3 是 M1 执行方案，V4 是现有 M1 的 UX 优化方案，V5 是 M2 DSL 分组方案，V6 是正在实施的 M3 DSL 改写方案，均不是包版本。V4 未启动 M2 DSL；实现与开发自动结果、真实 Host、真实客户端和人工验收分别登记。

DSL 教程、语法手册和设计意图集中放入 `dsl`。规划放入 `roadmap`，专项设计放入 `design`，可复用验证方法放入 `maintenance`。已新增当前行为 `reference`、使用步骤
`quick-start` 和带日期的实施记录；现有模板开发文档暂保留当前位置。实现、自动验证、真实 Host、人工验收和发布分别记录。

## 开发快速开始

V2 / M0 首要满足 SOLID，设计模式朴素使用，关键实现与测试写详细中文注释。不使用 AIFLOW；只运行本地 Debug 开发检查，不新增或运行 Windows CI、发布门禁。现行完整入口为 pwsh -NoProfile -File tools/verify-development.ps1 -Mode M0，包含十分钟持续流；Docs 模式只检查文档。以下是现有模板开发流程，后文发布说明仅在正式发布阶段适用。

V3 / M1 同样以 SOLID 为首要规定，单元测试、生产 UI、资源测量及门禁正反例随功能实现。完整入口为 `pwsh -NoProfile -File tools/verify-development.ps1 -Mode M1`，包含全部 M0 与专项测量，默认值仍为 M0；`-Mode Docs` 仅验证文档。

V4 文档沿用上述工程规定，定义 V4-01～12 和 40 项业务场景，额外包括 SOLID/中文注释审查、10 组门禁自测及 4 项人工验证。`-Mode UX` 与专用映射/profile/检查器已实现，在同轮继承 M0/M1，默认模式仍为 M0。完整 UX 同时检查 40 项新场景、20 张专项截图和真实桌面锚点；不运行 Windows CI 或发布门禁。

V5 定义 V5-01～14、64项业务场景、4项工程审查、12组门禁自测与4项人工验收；实现已接通。`-Mode M2` 同轮继承 M0/M1/UX，固定207项场景；不使用 AIFLOW、Windows CI 或发布门禁。

V6 定义 V6-01～16、72 项业务场景、4 项工程审查、12 组门禁自测与 4 项人工验收。`-Mode M3` 是未来交付入口，当前尚不支持；本期基础代码执行定向测试与完整 `-Mode M2` 兼容回归，结果见本期实施记录。设计要求改写运行在启动前固定 HTTP/1.1，发送前失败阻断而不回退原环境，保存/预览不启用规则；SOLID、朴素设计与详细中文注释贯穿实施，不使用 AIFLOW、Windows CI 或发布门禁。

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
dotnet test -c Debug --no-build --filter 'Category!=ResourceProbe&Category!=M1ResourceProbe&Category!=NativeUiProbe&Category!=M2ResourceProbe'
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
