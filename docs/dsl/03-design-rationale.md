# 03 · DSL 的设计意图与实现取舍

[返回手册首页](README.md) · [入门教程](01-tutorial.md) · [语法手册](02-syntax-reference.md)

这份说明解释当前 M2 为什么采用这些语法和运行规则，面向需要理解系统行为的使用者及维护者。更完整的实施契约见 [V5 专项设计](../design/network-traffic-v5-m2-dsl-grouping-design.md)。

## 1. 从业务问题出发

设计出发点是：请求已经能被抓到，但仅按 URL 排序，很难回答“同一个租户中的某个调用者，先后调用了哪些接口”。

调用者可能在 Query、Header、Cookie、JSON、表单或 Path 中，各接口也可能不一致。把这些差异写进界面代码，会使每加一个业务位置都要增加专用控件；通用脚本引擎则会带来执行权限、资源控制、调试和兼容成本。

当前 DSL 只描述三类业务决定：

1. **从哪里取字段**：把协议层数据映射为 caller、tenant、orderId 等业务名称。
2. **哪些请求参与**：来源、时间窗口和布尔条件。
3. **什么叫同一组**：一个标量或有序组合键，并保留调用时间线。

有限语法便于在执行前发现错误，也便于解释每条请求为什么进组、为什么被排除、为什么身份无法确定。规则没有网络与文件访问能力；数据读取由工作台提供。

## 2. 将编译、预览、应用分开

编辑文本时，人需要快速知道括号、字段名、类型是否写错；此时不需要每次按键都扫描正文。因此编辑停止约 200ms 后只编译，不读取捕获。

预览回答“这段规则对这几条记录怎样求值”。它使用正式字段求值管线，展示类型、状态和来源，但只取最多 20 条样本。即使条件为 False 或 Unknown，也解释候选分组键，帮助定位问题。

应用回答“对整个冻结范围，分组和覆盖统计是什么”。它建立完整候选结果，成功后才替换页面结果。编译失败、取消、总预算失败或过期操作不会把半份结果发布到界面。

| 操作 | 成功代表什么 | 不代表什么 |
| --- | --- | --- |
| 验证 | 文法、依赖和静态类型合法 | 数据中一定有对应字段 |
| 样本预览 | 这些冻结样本可以这样解释 | 完整窗口和所有视图选项都已统计 |
| 应用/刷新 | 当前冻结范围的候选结果已完整产生并被接纳 | 后续新到达记录已自动包含 |

保留上次成功结果是为了让失败可恢复；界面同时保留对应修订与状态。切换来源时则清除旧来源结果，防止把另一份存档的结果误当作当前数据。

## 3. 类型错误尽量提前，数据未知必须保留

类型回答“这个值应该是什么”，状态回答“现在是否有可靠值”。将两者分开，可以表达“caller 应为 String，但这条记录的 caller 是 NotReady”。

例如两个请求都暂时读不到 caller：

- 完整 Query 没有 user：这是 Missing，可以尝试 Header。
- JSON 请求正文还在接收：这是 NotReady，无法断言正文里没有 caller。

如果统一转为空字符串再回退，第二条请求可能先被错误分到 Path 身份，正文完整后又跳到另一个身份。当前 `firstNonEmpty` 只跳过 Missing、Null 和空字符串，遇到其他状态停止。这让回退优先级具有可靠含义。

同样，`not` 不能把“无法证明 caller 等于 u1”变成“已经证明 caller 不等于 u1”。因此使用三值逻辑，只有确定 True 才通过过滤。`isMissing` 和 `isNull` 也只识别对应状态，不会把 Unavailable 伪装成 False。

静态类型检查拒绝能提前证明的错误，例如 String 与 Number 比较、列表直接分组、Number 转 Boolean。JSON 的类型要读取正文才能确定，因此要求显式转换；运行时不符合约定则给出 Invalid。编译器检查规则，读取器和求值器检查数据。

## 4. 多值与组合键必须由使用者明确选择

HTTP 参数和 Header 天然可能重复。若默认把重复项拼成字符串，身份相等规则会依赖逗号或分隔符；若默认展开，会让一条请求进入多个组，使请求数难以解释。

因此访问器先返回有序列表：`first` 明确选择首项，首项是 Null 也保留；`all` 保留列表用于展示或适用的成员判断。group by 只接收标量或一个 tuple，保持“一条请求最多进入一个正常组”。

Tuple 解决的是组合身份。引擎将每个分量的**类型、长度、规范值**写入内部键，而不是拼接人看到的文字。字符串中的分隔符不会造成键碰撞，String `"1"` 不会与 Number 1 混为一谈；Number 1 和 1.00 则按相同数值处理。

某个键分量未知时，保留 `Value / Missing` 这样的状态向量，放入“未确定”。这既避免把所有缺失身份算作一个业务主体，也指出究竟是 tenant 还是 caller 出了问题。

组别名只帮助阅读，不是永久业务 ID；内部标识也不应被当成跨重启的身份协议。需要持久理解的仍是规则修订与业务键含义。

## 5. 统计必须能够核对

一次分析冻结全部保留记录，按来源、时间窗口、条件、键状态依次分类。主统计桶互斥，建立守恒关系：

```text
保留 = 来源排除 + 窗口外 + 条件 False + 条件 Unknown + 条件 True
条件 True = 正常分组 + 身份未确定
```

这样“少了多少请求”就能被回答。总捕获数和淘汰数另外展示，避免把有限保留集误说成完整历史。窗口前上下文属于窗口外的一个可查看子集，不额外计入主统计。

不同 RecordId 的相同请求都保留，因为重复调用可能正是要诊断的现象。冻结快照里重复出现同一个 RecordId 则属于身份不确定，整轮拒绝；分析层不猜测它是重复读取还是新修订。

接口数按 Method、Host、Path 计算，不把 Query 变化自动算成新接口。错误统计包含 HTTP 状态码至少 400 或传输 Failed 的并集；接收中和取消单独表示。这些都是工作台固定口径，DSL v1 没有开放任意聚合语言。

## 6. 冻结快照让结果可以解释

捕获不断变化，而使用者需要能阅读一份稳定结果。分析开始时冻结记录元数据并持有读取租约，按这个边界读取已有正文；新增记录只产生刷新提示。

冻结并不把未结束请求变成完整请求，也不能补回未捕获的字节。因此当前仍接收的适用请求正文是 NotReady，存档内未结束正文是 Unavailable。正文查看器可以展示已有片段，身份提取则不能据此前缀猜测完整字段。

时间窗口使用请求开始时间和 `[from, to)` 半开区间，使相邻固定窗口没有边界重复。实时锚点取冻结时刻；存档没有可靠冻结时间时，以最后请求开始时间加一个 tick 定位，避免旧存档相对窗口随着今天日期改变。

时间线按 StartedUtc、Sequence、RecordId 稳定排序。持续时间来自已记录的单调耗时，不简单相减两个可能受系统时钟调整影响的墙钟时间。开放端表示冻结时仍接收；条带重叠只表达观察到的并行，DSL 不推断调用因果。

## 7. SOLID 如何落实到代码

首要约束是职责和依赖清楚。当前实现使用普通递归下降解析、不可变记录、小接口和显式构造注入；没有为每个运算符建立插件工厂或事件总线。

| 部件 | 单一职责 | 依赖边界与实现入口 |
| --- | --- | --- |
| DslCompiler | 词法、解析、字段绑定、静态类型与诊断 | [编译器](../../src/NetWorkTrafficPlugin.Plugin/Application/Dsl/DslCompiler.cs)，不接触捕获和控件 |
| DslProgram / FieldValue | 表达已编译规则、类型、状态及来源 | [契约](../../src/NetWorkTrafficPlugin.Plugin/Application/Dsl/DslContracts.cs)，不负责文件或页面寿命 |
| IFieldSource / FieldEvaluator | 求值、短路、状态传播、每记录字段记忆 | [求值器](../../src/NetWorkTrafficPlugin.Plugin/Application/Dsl/FieldEvaluator.cs)，只向字段端口请求数据 |
| RequestFieldReader | 从记录和正文中提取 original 字段，严格解析并释放资源 | [读取适配器](../../src/NetWorkTrafficPlugin.Plugin/Infrastructure/Analysis/RequestFieldReader.cs) |
| IAnalysisSource / CaptureAnalysisSource | 提供冻结来源与读取租约 | [分析契约](../../src/NetWorkTrafficPlugin.Plugin/Application/Analysis/AnalysisContracts.cs)、[来源适配器](../../src/NetWorkTrafficPlugin.Plugin/Infrastructure/Analysis/CaptureAnalysisSource.cs) |
| AnalysisQueryEngine | 扫描、过滤、聚合、覆盖统计与候选结果 | [查询引擎](../../src/NetWorkTrafficPlugin.Plugin/Application/Analysis/AnalysisQueryEngine.cs)，不负责按钮状态和文件写入 |
| RuleLibrary / FileRuleLibraryStore | 规则修订及视图契约 / 文件持久化 | [规则库契约](../../src/NetWorkTrafficPlugin.Plugin/Application/Rules/RuleLibrary.cs)、[文件存储](../../src/NetWorkTrafficPlugin.Plugin/Infrastructure/Rules/FileRuleLibraryStore.cs) |
| RuleEditorModel / AnalysisWorkspace | 编辑诊断与辅助片段 / 页面操作编排与结果接纳 | [编辑模型](../../src/NetWorkTrafficPlugin.Plugin/Features/Rules/RuleEditorModel.cs)、[页面模型](../../src/NetWorkTrafficPlugin.Plugin/Features/Analysis/AnalysisWorkspace.cs) |

对 SOLID 五项原则的具体理解：

1. **单一职责**：编译器不读取正文，求值器不写规则库，查询引擎不控制界面。故障定位能沿相同边界展开。
2. **开闭原则**：捕获与存档通过分析来源契约进入同一引擎；存储实现可在端口后变化。增加语言函数仍需显式更新白名单、绑定、求值和测试，不承诺任意语法可热插拔。
3. **里氏替换**：来源实现都必须遵守冻结、取消、租约和状态语义。不能让测试来源默默补全生产来源中缺失的数据，也不能让存档实现暗中发送网络请求。
4. **接口隔离**：字段读取端口只暴露求值所需能力；求值器不被迫依赖代理启动、页面导航、规则保存等无关接口。
5. **依赖倒置**：应用层依赖字段、分析来源和规则存储契约；具体捕获、正文文件和界面由外层装配。单元测试提供受控输入，同时继续调用真正的编译器和求值器。

采用适配器，是因为确实存在不同数据边界；采用字段记忆，是为了同一条记录不重复求值和读正文。固定函数集合用直接分支表达，比为了模式名称引入多层继承更容易核对。纯函数和简单记录无需全部再包一层接口。

## 8. 有界执行是工作台行为的一部分

大正文、大量组或深层输入可能拖慢界面，甚至影响代理捕获。预算因此在实际工作路径上累计，包括失败的解压字节，而不是只在成功结果上记账。

正文按需读取，同一记录复用已经读取的字节和 JSON 解析结果，离开记录后释放。短路表达式避免无用读取；结果仅保留有界字段投影，不永久持有所有正文或来源租约。

单页最多一个分析或预览，容器最多两个。达到并发上限立即提示忙，不积累无界任务队列。候选结果和保留结果一起计入分析预算，因此保留旧结果的同时生成新结果也可能超过内存上限。

取消令牌传入扫描、读取和解码。页面还检查来源、编辑和操作代次，防止较早任务在较晚规则之后回写。关闭分析页面只释放自己的资源，不停止其他页面共享的代理。

具体限额见 [语法手册](02-syntax-reference.md)，资源与取消测试见 [V5 验证矩阵](../maintenance/network-traffic-v5-m2-verification-plan.md)。

## 9. 规则修订、保存视图与隐私

编辑草稿本身不持久化。保存时创建不可变规则修订；应用时复用与草稿匹配的修订，或创建本轮修订。保存视图引用确定的 RevisionId，并记录窗口、来源、列和排序等选项。这样能回答“当前结果来自哪份规则”，也不会因为另一个页面保存规则而悄悄改变正在阅读的结果。

规则库位于 `%LOCALAPPDATA%/MyAvalonia/NetworkTraffic/Rules/rules.json`。文件存储使用同目录临时文件、刷新和原子替换，并结合锁与代次比较处理并发保存。坏 JSON、未知 schema 或损坏引用不会被自动覆盖成空库；冲突需要重新加载或另存。

保存的是规则及选项，不是请求正文、会话路径或计算出的身份值。但用户写在规则里的字面量本来就是文本的一部分，仍会被保存；规则库不是密钥保险库。

自定义字段统一视为敏感，包括由常量、公开 Host 或状态码生成的字段。原因是字段的业务含义由作者决定，读取器无法凭数据位置判断它是否可以公开。命名 tuple 的分量也继承遮罩，避免展开后泄漏。

遮罩只影响展示，不改变分组相等规则；预览始终遮罩，分组页可以显式查看本次原值。诊断定位与来源轨迹应说明“从哪里得到”，不需要复制凭据本身。

## 10. 测试为何按边界安排

DSL 的正确性不止是“这段文本能解析”。需要同时验证静态拒绝、运行状态、业务成员、保存冲突和资源释放：

| 要证明的行为 | 对应测试入口 |
| --- | --- |
| 合法语法、优先级、保留名、类型、依赖环、诊断位置、编译预算 | [DslCompilerTests](../../tests/NetWorkTrafficPlugin.Tests/Dsl/DslCompilerTests.cs) |
| 六位置读取、解码、多值、状态、转换和正文预算 | [FieldExtractionTests](../../tests/NetWorkTrafficPlugin.Tests/Dsl/FieldExtractionTests.cs) |
| 真实键成员、组合键、未确定身份和覆盖守恒 | [GroupingTests](../../tests/NetWorkTrafficPlugin.Tests/Analysis/GroupingTests.cs) |
| 时间排序、窗口、持续与开放端 | [TimelineTests](../../tests/NetWorkTrafficPlugin.Tests/Analysis/TimelineTests.cs) |
| 不可变修订、文件冲突、损坏保留和视图持久化 | [RuleLibraryTests](../../tests/NetWorkTrafficPlugin.Tests/Rules/RuleLibraryTests.cs) |
| 编辑、应用、选择、原值遮罩、来源切换等界面行为 | [M2UiTests](../../tests/NetWorkTrafficPlugin.Tests/Ui/M2UiTests.cs) |

关键样例的期望成员手工列出，避免“测试用同一套分组算法算答案”。门禁还用拒绝案例检验漏证据、旧证据或不完整场景不能被当作通过。

维护本手册时，用实际编译器检查可复制示例；修改语言时，同时考虑文法、静态类型、成功值、未知传播、敏感标记、预算和取消，再更新教程与语法表。详细中文注释应说明约束和设计原因，不能只复述代码。

文档检查使用 `pwsh -NoProfile -File tools/verify-development.ps1 -Mode Docs`。完整行为与资源验证使用 `-Mode M2`，执行本地 Debug 开发门禁；AIFLOW、Windows CI 和发布门禁不属于本轮范围。已有通过结果和人工未验项分别见 [实施记录](../archive/2026-09-30-v5-m2-implementation.md)，不能由教程或样例编译结果代替。
