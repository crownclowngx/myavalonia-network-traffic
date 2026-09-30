# V5 · M2 DSL 与分组专项设计

> 日期：2026-09-30。状态：**待实施的语言与行为契约，不是当前可运行配置**。
> 执行顺序见[V5 执行计划](../roadmap/network-traffic-v5-m2-dsl-grouping-plan.md)，证明方式见[V5 验证矩阵](../maintenance/network-traffic-v5-m2-verification-plan.md)。

## 1. 设计目标与依赖方向

M2 是捕获事实之上的只读分析层。输入是已保存或当前保留的请求快照，输出是可解释字段、筛选结论、组摘要和时间线；分析不进入代理转发回调，不修改流量，不补发缺失请求。M3 将来可以复用语言前端和纯字段求值，但其发送前时机、动作、失败策略必须另立契约，不能复用 M2 的历史读取策略直接拦截网络。

| SOLID 原则 | 本轮具体决定 | 审查依据 |
| --- | --- | --- |
| S 单一职责 | 词法/语法生成 AST；绑定检查类型/依赖；提取器解释捕获输入；求值器计算；查询器聚合；存储器负责文件；页面负责交互 | UI 无文件/JSON 解析，代理无 DSL 求值；聚合器无控件和存储路径 |
| O 开闭原则 | 内置函数由有限名称、签名和普通分支集中定义；新增一种提取不修改捕获记录器和组 UI | 不散布字符串判断；允许修改有限语言实现，不为未来扩展搭运行时插件框架 |
| L 里氏替换 | Fake、实时、存档源共同遵守冻结、缺口、取消、释放和来源契约 | 相同契约测试覆盖三类源；不把 Fake 的错误全部变成空成功 |
| I 接口隔离 | 分析只读源与规则库写入分开；领域纯计算无需接口 | 分析拿不到代理起停、重发和任意文件读写能力 |
| D 依赖倒置 | Application 接受快照/窄端口、TimeProvider 和额度；Infrastructure 适配现有会话/文件；组合根构造 | 应用无 Titanium、Avalonia、服务定位器或静态全局会话 |

朴素实现：递归下降解析器、不可变记录、枚举结果、字典聚合、拓扑排序、MVVM 和构造注入。不生成动态代码，不用编译器平台执行用户文本，不为 AST 节点建立接口工厂层；求值采用节点类型分支即可。

建议在现有 Plugin 项目内增加以下职责，不预拆程序集：

| 拟新增位置 | 主要对象与职责 | 所有权 |
| --- | --- | --- |
| `Application/Dsl/` | DslLexer/Parser/Binder、AST、DslDiagnostic、FieldEvaluator、有限函数与状态类型 | 编译结果不可变；求值上下文每记录一份 |
| `Application/Analysis/` | IAnalysisSource、AnalysisQueryEngine、GroupAccumulator、TimelineProjection、AnalysisBudget | 引擎无页面状态；容器额度最多两项任务 |
| `Application/Rules/` | RuleRevision、SavedView、RuleLibrary、IRuleLibraryStore | 领域契约；修订不可变 |
| `Infrastructure/Analysis/` | CaptureAnalysisSource、BoundedRequestContentReader | 适配 ICaptureSession，持有并归还读取租约 |
| `Infrastructure/Rules/` | FileRuleLibraryStore | 容器共享、串行原子写入和库代次检查 |
| `Features/Analysis/` | AnalysisWorkspace、分组列表、TimelineView | 每 Document 独立；取消、选择、刷新与显示状态 |
| `Features/Rules/` | RuleEditorModel/View、字段辅助表单 | 当前页面草稿与预览，不直接改全局生效规则 |

MainDocument 组合 CaptureWorkspace 与 AnalysisWorkspace。现有 CaptureWorkspace 只补只读数据源变化及按身份打开详情的薄协作，不把新业务继续塞入其 partial 文件。共享规则库只发布“库已更新”信息，页面自行决定是否加载修订；通知订阅必须释放。

## 2. 语言版本、程序结构与样例

`dsl 1;` 是首行版本声明，一份文本含 0～32 个字段和恰好一个 view；字段可前向引用，由绑定阶段排序，循环依赖拒绝。v1 只分析 original 请求，禁止 route、rewrite、send、response 和 effective 访问器，给出不支持诊断而非忽略。

以下为**实施后应支持的 v1 样例**，字段名与测试数据均为合成值：

```text
dsl 1;
// 设计思路：明确选择每处首值；只在可证明没有身份时向后回退。
field tenant = firstNonEmpty(first(original.query["tenant"]),
                            first(original.headers["x-tenant"]));
field caller = firstNonEmpty(
    first(original.query["user"]),
    first(original.headers["x-business-user"]),
    first(original.cookies["business_user"]),
    toString(original.json("$.identity.user")),
    first(original.form["user"]),
    if(startsWith(original.path, "/actors/"), original.pathSegment(1), null)
);
field businessKey = tuple(tenant, caller);
view "租户调用过程" {
    where source == "capture";
    within last(30m);
    group by businessKey;
    columns tenant, caller;
    timeline by startedAt;
}
```

第二份样例只将 `group by businessKey;` 改为 `group by caller;`，其余输入保持相同，证明模型既不依赖某一参数名，也不依赖一个固定提取位置。使用 JSON 时，前面的 Query/Header/Cookie 不可证明完整会阻止回退，这是未知状态的真实语义。

有效/无效样例集至少包含下列小例子；片段放回完整程序后测试，缺失程序本身不能算该案例的预期失败原因。

| 样例片段 | 预期 |
| --- | --- |
| `field caller = first(original.query["user"]);` | 有效，String 标量，保留首值状态 |
| `field amount = toNumber(original.json("$.amount"));` | 有效；运行时非数字 JSON 值为 Invalid，不是语法错 |
| `where original.host == "api.example.test" and statusCode >= 400;` | 有效；尚无响应状态时为 Unknown，不能当 0 |
| `within range("2026-09-30T12:00:00Z", "2026-09-30T12:30:00Z");` | 有效，UTC 半开窗口 |
| `field a = b; field b = a;` | DSL007，列出依赖环和字段位置 |
| `group by all(original.query["user"]);` | DSL005，列表不能直接作组键 |
| `where "1" == 1;` | DSL005，比较类型不匹配 |
| `field x = original.json("$..user");` | DSL008，递归路径不在 v1 子集 |
| `within range("2026-09-30T12:00:00", "2026-09-30T12:30:00");` | DSL010，缺少时区 |
| `field x = eval("1+1");` | DSL004，未知函数且绝不执行文本 |

### 2.1 文法子集

下列 EBNF 是实施与测试的共同依据；关键字和标识符区分大小写，标识符为 ASCII 字母/下划线开头再接字母/数字/下划线；展示名可为中文。允许空白和 `//` 行注释，不支持块注释、赋值表达式、循环、用户函数和导入。

```text
program     = "dsl" integer ";" { field } view EOF ;
field       = "field" identifier "=" expression ";" ;
view        = "view" string "{" "where" expression ";"
              "within" window ";" "group" "by" expression { "," expression } ";"
              [ "columns" identifier { "," identifier } ";" ]
              "timeline" "by" "startedAt" ";" "}" ;
window      = "last" "(" duration ")"
            | "range" "(" string "," string ")" ;
expression  = orExpr ;
orExpr      = andExpr { "or" andExpr } ;
andExpr     = comparison { "and" comparison } ;
comparison  = unary [ ( "==" | "!=" | "<" | "<=" | ">" | ">=" | "in" ) unary ] ;
unary       = [ "not" ] primary ;
primary     = literal | identifier | accessor | call | "(" expression ")" | list ;
call        = identifier "(" [ expression { "," expression } ] ")" ;
list        = "[" [ literal { "," literal } ] "]" ;
accessor    = "source" | "statusCode" | "original.host" | "original.method" | "original.path"
            | "original." collection "[" string "]"
            | "original.json" "(" string ")"
            | "original.pathSegment" "(" integer ")" ;
collection  = "query" | "headers" | "cookies" | "form" ;
literal     = string | number | "true" | "false" | "null" ;
duration    = positiveInteger ( "s" | "m" | "h" | "d" ) ;
```

`source`、`statusCode`、`original`、函数名和语言关键字为保留名，不能用作字段名。比较不能连写 `a < b < c`；重复 `not` 使用括号。字符串用双引号，转义规则按 JSON 字符串子集，拒绝未配对代理字符和非法转义。数字为可带负号的十进制定点数，不支持指数、NaN、Infinity；有效数字及小数位各不超过 28，最终须能由 decimal 无损表示，否则诊断 InvalidNumber。枚举列表要求元素同类型、非 null，空列表由 `in` 的左操作数推导类型。

操作优先级从高到低为括号/访问/函数、not、比较/in、and、or。排序比较仅用于 Number；String/Boolean 支持同类型相等比较；不存在隐式类型转换，`"1" == 1` 编译失败。字符串按 Ordinal 比较，不依赖设备语言。null 不参与普通比较，用 `isNull` 判断。

JSON 路径的静态类型为受限 `JsonValue`（运行期标量或同类型标量列表），不引入任意 Any。直接作为比较、回退的普通标量参数或组键时，必须先用 toString/toNumber/toBool 显式确定目标类型；原值可用于状态检查或显示列。列表可先 first 再转换；类型已知的非法组合在绑定时拒绝，JSON 实际类型不符合转换时返回 InvalidType。样例将 JSON user 显式转 String，故其余 String 回退分支类型一致。

时间常量必须携带 UTC `Z` 或明确 offset，绑定时转 UTC；拒绝无时区日期。last 为正时长且不超过 7 天，range 满足 from < to 且不超过 7 天；此限制控制首版范围，不意味着捕获保留 7 天。显示本地时间时同时展示时区，保存 UTC。

### 2.2 允许函数

函数白名单外的名字均为编译诊断，不动态查找 .NET 方法。

| 函数 | 类型与 v1 语义 |
| --- | --- |
| `first(list)` | 按原出现顺序取首项；空列表为 Missing；列表自身未知时传播原状态 |
| `all(list)` | 保留有界列表及顺序，不排序、不去重、不截断后假装完整 |
| `firstNonEmpty(a,b,...)` | 参数须为同一标量类型或 null；依次跳过 Missing/Null/空字符串；其他非 Value 状态立即返回；全被跳过为 Missing |
| `if(condition,a,b)` | condition 为 Boolean；两分支同类型；只求被选择分支；Unknown 条件直接保留未知状态，不猜分支 |
| `trim`、`lower`、`upper` | String → String；trim 只去首尾 U+0020/U+0009/U+000A/U+000D，大小写用 invariant；不自动作用于提取结果 |
| `contains`、`startsWith`、`endsWith` | 两个 String → Boolean，Ordinal；任一非 Value 传播未知 |
| `toString`、`toNumber`、`toBool` | toString 接受 String/Number/Boolean，Number 用 invariant 定点表示；toNumber 接受 Number 或全串十进制 String，不接受千分位/指数/外侧空白；toBool 接受 Boolean 或小写字符串 true/false；不将数值当布尔；JSON 按实际类型套用上述转换，失败为 Invalid |
| `isMissing`、`isNull` | 对相应状态返回 True，确定的其他值/缺省状态返回 False；NotReady/Unavailable/Invalid/BudgetExceeded 仍为 Unknown |
| `tuple(a,b,...)` | 2～4 个 String/Number/Boolean 分量；组键保留类型和边界；任一分量非 Value 则进入未确定分类 |

`in` 左侧为标量，右侧为相同类型列表，按相等比较判定；列表或其必要元素不可确定则结果 Unknown。`all` 的结果只用于列表展示或 `in`，不能直接作为 group key；不提供隐式笛卡尔展开、一条请求进入多组、聚合脚本或未定义的 join/regex 函数。

除 first/firstNonEmpty/if/状态检查的专门规则外，函数收到非 Value 状态原样传播，不把 Null 转成字符串 `"null"`；需要比较的两侧出现 Null/Missing 等状态时为 Unknown，常量 null 的普通比较则在绑定时提示使用 isNull。firstNonEmpty 为2～8参数，其他函数严格按表中元数（字符串判断2个、if为3个、单值转换/状态检查1个）绑定；tuple 为2～4参数。

## 3. 类型、状态、三值逻辑与诊断

值载荷支持 String、Number(decimal)、Boolean、同类型 List、有限 Tuple。`FieldResult` 独立携带 State、Value、Origins、Sensitivity、DiagnosticCodes；Null 是状态而不是空字符串。每个字段最多保留 8 条有界来源说明，记录实际选择及必要的回退原因，不复制完整头或正文。

| State | 含义与例子 | 可以当作缺失回退吗 |
| --- | --- | --- |
| Value | 可确定的标量、列表或 tuple；空字符串仍是值 | firstNonEmpty 对空字符串可回退，其他值不可 |
| Missing | 来源完整且键/路径不存在，或明确无正文/格式不适用 | 可以 |
| Null | Query 键无等号或 JSON 显式 null | 可以，且仍可用 isNull 区分 |
| NotReady | 实时请求体仍在接收且未来可能完整 | 不可以 |
| Unavailable | 元信息缺口、终态部分记录、仅元信息、缺文件、历史未结束快照、暂不支持的内容编码 | 不可以，原因码分别记录 |
| Invalid | 损坏 URI/JSON/表单/UTF-8、类型错误、转换失败、重复 JSON 属性 | 不可以 |
| BudgetExceeded | 读取/解码/深度/集合/字段操作等达到上限 | 不可以，不伪装成 Missing |

取消属于**整项操作控制流**，不是记录字段的 Missing，也不计入 Unknown 成功结果。记录局部输入错误可以输出 Invalid 并继续分析其他记录；整个程序的语法/类型错误在查询前失败。

条件的 True/False 为确定 Boolean，其他状态都映射为 Unknown 并保留原因。左侧可确定结果时短路；否则只求必要的右侧：

| 左值 | `and False` | `and True` | `and Unknown` | `or False` | `or True` | `or Unknown` |
| --- | --- | --- | --- | --- | --- | --- |
| False | False | False | False | False | True | Unknown |
| True | False | True | Unknown | True | True | True |
| Unknown | False | Unknown | Unknown | Unknown | True | Unknown |

`not Unknown` 仍为 Unknown，只有 True 进入分组候选。不得把 Unknown 转 bool false 后再取反。多个未知原因按表达式访问顺序保留有界列表，主原因取最早出现者，避免随线程时序变化。

诊断分为语言错误、输入记录诊断、操作错误：语言错误阻止候选应用；输入诊断附在字段/计数；取消、超出总预算、快照失败、存储失败则保留旧结果。诊断至少包含 code、severity、中文 message、span(start,length,line,column)、fieldName 和建议动作。跨度为 UTF-16 偏移，行列从 1 开始，LF/CRLF 及中文/emoji 均测试；错误消息不嵌入捕获原值。

拟定稳定 code 类别：`DSL001` 版本、`DSL002` 词法、`DSL003` 文法、`DSL004` 未知名称、`DSL005` 类型/签名、`DSL006` 重复字段、`DSL007` 依赖环、`DSL008` 不支持能力、`DSL009` 语言预算、`DSL010` 窗口；输入使用 `DATA_*`，操作使用 `ANALYSIS_*`、`STORE_*`。诊断最多 50 条，再显示省略数；解析失败后在分号/闭括号处恢复，不能无限重试同一 token。

## 4. 六种提取位置与完整性

所有访问默认读取请求原始事实。当前只有 OriginalUri/EffectiveUri 及一份 RequestHeaders/RequestBody；M2 将后两者视为当前捕获的请求事实，不宣称已经保存未来改写前后的双份完整快照。host 来自 OriginalUri 的规范化小写主机名，method 使用记录中的大写方法，path 是原 URI 的转义路径且不包含 Query。

`source` 是明确映射的 String，`statusCode` 是可缺省 Number：有状态码则为 Value，实时尚无响应为 NotReady，终态无响应或只读档案无法确定为 Unavailable，不用0替代；已有状态码时即使响应仍接收也可过滤。来源、状态码与请求字段分开建模，不能靠读取正文推断。

| 来源 | 精确语义 |
| --- | --- |
| Query | 键名 Ordinal；严格百分号/UTF-8 解码一次，`+` 保留加号；重复项按出现顺序；`?x` 为 Null，`?x=` 为空字符串；非法编码为 Invalid |
| Header | 名称 OrdinalIgnoreCase，值不改大小写；重复头逐行保留，不按逗号擅自拆值；首值需显式 first |
| Cookie | 仅 RequestHeaders 中的 Cookie，名称 Ordinal；按头顺序和分号项顺序；首个等号分隔，名称/分隔空白修整但值不 URL 解码；畸形项有 Invalid，Set-Cookie 不进入请求 cookie |
| JSON | 仅完整且受预算限制的 `application/json` 或 `+json` 请求体；属性 Ordinal；标量映射到类型，数组允许有界同类型标量列表，对象/混合数组为 InvalidType；重复属性拒绝，避免最后值胜出 |
| 表单 | 仅 `application/x-www-form-urlencoded`，charset 缺省 UTF-8、显式非 UTF-8 为 Unavailable；`+` 为空格、严格百分号解码；重复项有序，缺等号 Null，空值空字符串；multipart 为 UnsupportedFormat |
| 路径 | `original.pathSegment(n)`，去一个起始 `/` 后按 `/` 拆分，连续斜杠保留空段；0 起始，路径根无段；选中段严格解码一次，`%2F` 不成为新的分段；越界 Missing |

JSON 路径限定 `$` 根、`.name`、`["name"]`、`[非负整数]` 组合，如 `$.identity.user`、`$["business.id"]`、`$.items[0].id`。不接受通配符、负下标、切片、过滤表达式或递归；路径在编译时验证，最多 16 段。不存在的成员/数组下标为 Missing；在非对象上取属性或非数组上取下标为 InvalidType。

多值源的类型是 `List<String?>`，成员保留 Value/Null 状态；正文 JSON 的 Number/Boolean 使用强类型。`first` 取第一项的完整状态，不能跳过首个 Null 自动选第二项；`all` 保留全部状态用于展示，`in` 未命中且存在未知元素时为 Unknown。静态类型检查允许 nullable 标量成员，列表字面量仍采用第 2 节的非 null 限制。

完整性判断先于值选择。`MetadataPartial=true` 时，没有字段级完整性证据的 Query/Header/Cookie/路径都保守返回 Unavailable，即使还能看到一个前缀值也不能宣称 first/all 完整；不要从被截断的头推断该字段不存在。现有解析器可共享严格解码帮助函数，但查看器“保留损坏原值继续显示”的策略不能直接用作 DSL 成功值。

### 4.1 正文读取流程

1. 根据冻结的 BodyDescriptor 判断方向、ContentType、Representation、Transport/Capture 和 CommittedBytes。明确没有实体的终态请求返回 Missing；媒体类型完整明确但不适用于该访问器时返回 Missing，因而 JSON 与表单可顺序回退。
2. 实时仍接收为 NotReady；只读存档中保存的接收中正文不会继续到达，记 Unavailable(ArchivedIncomplete)。终态 Partial/MetadataOnly/Failed 不解析前缀；缺 ContentType 且正文非空为 Unavailable，不猜 JSON。
3. 只读取冻结水位内的实体字节，且不能超过编码体上限。文件消失/读取短缺/IO 失败为 Unavailable 并附原因；不通过文件路径直接绕过现有存档路径检查。
4. Identity/已解码实体和单层 gzip/br/deflate 使用有界解码；同时限制输入与输出，不二次解码 DecodedEntity；未知或多层编码为 Unavailable(UnsupportedEncoding)，损坏压缩数据为 Invalid。
5. JSON/表单都严格 UTF-8；校验完整文档、深度、token、属性和集合预算。不因文件存在或传输成功就认为内容完整。
6. 请求的完整正文可在响应仍流式接收时分析；响应体大小不参与 M2 字段读取。未用正文的规则不得创建正文解析缓存。

共享内容额度预留在分配前执行，覆盖字节、解码字符串及 JSON 对象的保守估算；解析节点数有独立硬上限。预留不足返回 BudgetExceeded，不等待代理，也不创建无界队列。完成/失败/取消统一释放；字段只保留有界结果与来源路径。

## 5. 快照、分析身份和并发

`IAnalysisSource` 提供冻结能力，返回拥有租约的 AnalysisSnapshot；正文读取必须受该快照中的描述符约束。初版复用 `ICaptureSession.FreezeAsync`，校验记录总数和内存预算，并确认现有归档读取已在解析前限制输入规模。超大/损坏存档应在有界打开阶段拒绝，不能先无限反序列化再截断。

一次 AnalysisRequest 固定以下信息：SourceInstanceId（每次打开或切换生成）、SessionId、SnapshotRevision、CutoffUtc、RuleRevisionId/Hash、WindowSpec、实际 UTC from/to、AnalysisId 和页面 OperationGeneration。存档与实时可以拥有相同 SessionId，故缓存/迟到判断不能只用 SessionId。

冻结后复制必要不可变元信息和头数组；冻结不能停止捕获或持有全局锁等待正文 I/O。当前 CaptureLease/CaptureSnapshot 没有冻结时间，V5-02 应以兼容方式补充只读冻结时间，在 LiveCaptureSession 固定记录/日志位置的同一锁区采一次 UTC；不能把后续补头或读取完成时间当作快照时刻。原有调用者与 M1 存档格式继续兼容。正文每次只读冻结 CommittedBytes，不能看到冻结后补齐的数据后声称同一快照结果。分析结束立即归还源租约，结果只持有有界 ID/统计/字段；用户点详情再获取当前可用读能力。

执行顺序：语法绑定成功 → 申请分析名额 → 冻结并固定窗口 → 元信息/来源预筛 → 按记录需要提取/求值 → 分组/时间线投影 → 校验结果预算 → 核对页面/源/修订/代次 → UI 一次替换。单记录以依赖拓扑顺序或等价的惰性记忆求值，每字段至多一次；没有跨轮正文缓存、无界索引或自动增量计算。

每页最多一项任务；用户再次刷新撤销旧任务并合并为最新一次意图，旧任务退出后启动，不积压点击队列。容器最多同时两项分析，名额不足显示忙碌，可重试；不自动排长队。编辑草稿仅做有界防抖编译，样本读取需点击预览。取消/关闭/换源必须撤销未提交候选，已提交快照结果保持原身份或随明确换源清空。

## 6. 过滤、组键与统计口径

分析从冻结的会话保留集开始，不从 CaptureWorkspace.Rows 开始。SavedView 来源选择与 `where` 为明确的 AND；缺省来源集合为 `{capture}`。当前生产只有 Captured；分析模型为将来保留 manual/replay/unknown 映射和合成测试，未知来源绝不能默认映射为 capture。不因此增加实际发送或改变旧存档枚举格式；未知旧格式若无法安全读取则明确拒绝。

元信息预筛只允许可证明等价的条件，不能提前计算正文或改变 Unknown/短路语义。默认不启用复杂查询优化；顺序扫描 10,000 条预算内的记录足够作为首版。

组键规则：

- `group by tenant, caller` 与 `group by tuple(tenant,caller)` 语义等价，嵌套 tuple 绑定时拒绝；最多 4 个标量分量。
- 以分量类型、规范值和长度共同比较，Number 按数值相等；不拼接分隔字符串，不用显示遮罩作为键。
- 字符串 `"1"` 与 Number `1` 不能落同一键；`("a|b","c")` 与 `("a","b|c")` 不冲突。
- 任一分量非 Value 时进入“未确定”区域，按分量状态向量分类并可查看来源原因；既不把所有未知合成一个用户，也不为每条未知记录创建一个业务组。
- 对 Value 的空字符串可形成“空字符串”业务键；用户若不希望如此需显式 trim/firstNonEmpty。Null 与 Missing 永不作为正常业务组键。

覆盖计数使用互斥桶，可验证下式；实现不得只显示“匹配 7 条”而隐去其他记录：

```text
retained = sourceExcluded + outsideWindow + evaluated
evaluated = filterFalse + filterUnknown + filterTrue
filterTrue = groupedRecords + unresolvedGroupRecords
sum(每个业务组.requestCount) = groupedRecords
```

计数顺序先来源，再窗口，再 where，再分组。窗口外上下文是 outsideWindow 的额外投影，不再相加进入总数；被淘汰记录不在 retained 内，另显示源 Total/Evicted 和未知元信息数量。结果注明“本次保留范围”，不能推算完整会话、全部用户或全网调用。

| 指标 | 定义 |
| --- | --- |
| 请求数 | 分组内唯一 RecordId 数；不同 RecordId 即使参数完全相同也分别计数 |
| 接口数 | `(original.method, original.host, original.path)` 的不同组合，忽略 Query；真实路径而非模板接口数，不自动把 `/users/1` 和 `/users/2` 合并 |
| 首次/最后开始 | 本组窗口内记录 StartedUtc 的 min/max |
| 错误数 | HTTP 状态 >=400 或传输 Failed 的记录并集，重复只计一次；Cancelled、Receiving 独立计数，可与 HTTP 错误交叉，UI 说明不强求相加等于总数 |
| 耗时 | 已终态记录的现有 ElapsedMilliseconds；无有效计时为未知；组总跨度与各请求耗时不相加冒充串行耗时 |
| 组数 | 完整业务组键数量；动态凭据变更就是不同凭据组，不能称人数 |

默认组排序按首次开始升序，再按类型化键的稳定排序；请求时间线始终开始时间升序。可选组排序只有请求数降序/首次开始升序，次键固定；不提供任意排序表达式。业务显示列最多 8 个命名字段，列表类型显示有界摘要与展开入口，超长值显示省略并保留状态，不改变实际键。

## 7. 时间窗口与时间线

实时 `last(d)` 在取得快照时捕获一次 TimeProvider UTC 时间作为 CutoffUtc，窗口为 `[CutoffUtc-d,CutoffUtc)`，扫描期间不移动。新数据只提示源已变，不暗中修改本次时间锚点。绝对 range 固定使用指定端点。

历史 `last(d)` 使用存档明确保存的快照时刻；当前 V3 格式只有 ArchiveManifest.CreatedUtc，它是在复制后写 manifest 的时间，不是冻结时刻，而且未通过 ICaptureSession 暴露。因此 V5 对当前存档固定采用“保留集中最大 StartedUtc 加一个 DateTime tick”作为参考，UI 标“按最后请求时间定位”，不为 M2 升级存档格式。将来若格式明确提供冻结时刻，需另做兼容验证后采用。空存档显示无记录、不虚构时间；溢出或异常时间提供诊断。重开相同存档的窗口必须相同，不能使用当前系统时间。末端包含历史最后一条仅依上述 +tick 规则，不改变半开窗口定义。

主列表归属只看 StartedUtc，排序键为 `(StartedUtc,Sequence,RecordId)`，不按 CompletedUtc 排序。瞬时/零耗时仍保留可点击最小视觉标记，但 tooltip 显示真实值；记录异常/负耗时显示未知，不伪造 1 ms。

条带起点为开始时间，长度采用已记录单调耗时；屏幕可裁剪到窗口，原始时间和耗时仍可查。快照时仍接收的请求画开放端并标状态，长度固定为快照中已观察耗时，不在 UI 时钟里不断假装网络还在继续。CompletedUtc 只作为墙钟展示；检测到墙钟回拨或时间与耗时不一致时标注，不重排开始序列。

可选“窗口前在途上下文”：StartedUtc < from，且依据已记录耗时的观察终点严格大于 from，或快照时仍接收；终点恰好等于 from 不重叠。快照时间早于窗口起点或耗时无法确定时不猜测重叠，列入无法判断的上下文提示。上下文另区显示且不计请求/接口/错误/组数；没有窗口内成员的键不产生正常业务组。上下文仍须通过来源和 where，并显示自己的未知状态。

相同开始时刻保留 Sequence/RecordId 顺序；并发请求显示重叠，响应先完成不提前排列。不使用连线推断因果；明确 trace/parent 的关联属于后续范围。

## 8. 规则修订、SavedView 与原子保存

Draft 的文本属于当前页面；CompiledCandidate 绑定编辑代次和诊断；AppliedRevision 只由“应用成功”替换。样本预览、保存成功都不自动应用。应用一次候选分析成功后，规则和结果一起切换；新分析取消/失败时保留旧结果，并标出草稿与已应用修订的区别。

`RuleRevision`：RevisionId、LanguageVersion、原样 UTF-8 SourceText、SourceHash、CreatedUtc、可选 ParentRevisionId。hash 计算明确存储字节（不静默格式化或改换行）；创建后不可变。未保存的已应用候选也有临时修订身份，保存后可沿用其内容摘要，不把“最新”当作可复现引用。

`SavedView`：ViewId、Name、RuleRevisionId、WindowOverride（可空）、SourceSelection、GroupSort、VisibleColumns、IncludeContext、SchemaVersion。字段/where/group 来自所引用 DSL 的单一语义模型；时间面板修改时产生明确覆盖，UI 同时显示 DSL 默认与当前窗口。保存窗口模式与参数，执行时的绝对 from/to 记在 AnalysisResult，last 不保存为无提示的固定日期。

SavedView 不保存样本正文、运行期字段值、结果缓存、源绝对路径、代理状态或凭据。打开视图只加载定义，仍由用户对当前源应用；不启动捕获、不恢复网络任务、不自动执行含正文扫描的查询。库中 DSL 常量可能包含用户填写的敏感值，不把 SourceText 写入普通日志或证据。

首版采用一个有界 JSON 库文件，将修订和视图放在同一提交单元，路径由现有应用数据目录约定确定并注入，不依赖当前工作目录。schemaVersion=1；最多 50 个视图、200 个修订、总文件 8 MiB（任一上限先到即拒绝），不自动清除被引用修订、不静默覆盖同名视图。显式删除只删指定 ViewId；修订清理暂不提供 UI，可在后续维护设计中处理。

写入过程：拿同进程串行锁并对固定库锁文件获取跨进程独占锁 → 比较 expectedGeneration → 验证完整候选、字节上限和引用 → 写同目录唯一临时文件并刷新 → 原子替换/首次原子移动 → 发布新 generation。无法取得锁返回忙碌，generation 变化返回冲突；不退回直接截断原文件。提交点后收到取消仍返回真实已提交身份，UI 可忽略迟到应用但不能谎称未保存。文件替换能力通过本机故障注入确认，临时文件清理仅限本次拥有的文件。

坏 JSON、缺修订、未知 schema/dsl、损坏 hash、非法类型和超限库均显示明确错误并保留原字节，不覆盖为空库；继续使用已在内存加载的旧有效库。首次文件不存在才初始化空库。两页同时编辑不同视图也使用 generation 冲突，首版不实现复杂自动合并。

## 9. 预算、降级和测量

以下是 V5-01 应固化到 `m2-profile.json` 的**初始开发目标**，不是已达到的性能成绩。实现时逐项测量；预算变更必须登记依据并同步矩阵，不以减少样本宣称旧条件通过。

| 项目 | 初始上限/目标 | 超限行为 |
| --- | --- | --- |
| DSL 输入 | UTF-8 64 KiB、8,192 token、AST 深度 32、32 字段、50 诊断 | 编译拒绝，旧修订保持 |
| 字段/键/列 | 单值 4,096 UTF-16 单元；集合 128 项；字段来源 8 项；组键 4 分量；显示列 8 | 记录字段 BudgetExceeded；配置上限在编译/保存时拒绝 |
| 求值工作 | 每记录 10,000 节点访问；每轮 5,000,000 次；每 128 次检查取消 | 单记录上限形成字段状态；整轮上限失败不提交 |
| JSON 路径/结构 | 路径 16 段、JSON 深度 32、每正文 16,384 token | 对应字段 BudgetExceeded |
| 正文输入/解码 | 单体编码与解码各 256 KiB；每轮累计编码读取 32 MiB、累计解码输出 32 MiB | 单体过大为字段 BudgetExceeded；累计超限整轮失败 |
| 内容内存 | 沿用容器共享 32 MiB，分析读/解码/DOM 同样预留 | 无额度的字段 BudgetExceeded，完成归还 |
| 分析元信息/结果 | 容器共享 64 MiB，覆盖两页当前结果、候选、字段及键；与 M1 元数据预算分别计量 | 候选不能完整构建则整轮失败；不得偷偷丢组 |
| 查询规模 | 每快照最多 10,000 条、5,000 个业务组；上下文也占记录上限 | 超限拒绝并建议缩小窗口/保存范围 |
| 任务并发 | 每 Document 1 项，容器 2 项，无等待队列 | 忙碌可重试；新意图替换当前页旧意图 |
| 样本预览 | 最多 20 条、2 秒目标 | 超时/取消不提交，明确样本范围 |
| 编译/整轮查询 | 编译 500 ms 硬截止；查询含冻结/读取 10 秒硬截止；10k 元信息分组 warm p95 <=2 秒 | 截止失败保持旧结果，不作为部分成功 |
| 取消与 UI | 取消后 1 秒内任务/租约归还；UI p95 <=200 ms、最大 <=1,000 ms | 自动检查失败；分析不阻塞 Dispatcher |
| 寿命/持续负载 | 20 轮页面循环、600 秒抓包、20 并发含 5 条流；停止 <=10 秒 | 沿用 M1/UX 原始资源检查并增加分析观察 |

时间预算是单调截止和合作取消，I/O 也必须传递 CancellationToken；不能用 `Task.WhenAny` 仅放弃等待让后台继续耗资源。单测用受控操作计数/可控时钟测试边界，性能探针用 Stopwatch 实测真实耗时。

当前会话本就有 10,000 条保留上限。未来来源若更大，适配器须先有界检查再冻结，或实施单独分页方案；本轮不为任意大历史提供隐式扫描。64 MiB 分析预算包括旧结果与新候选并存，避免原子替换短时翻倍失控；内存不足时可提示用户显式清除旧分析后重算。

## 10. UI、来源说明与隐私展示

分组页显示数据源（实时快照/只读存档）、规则修订、已应用窗口、来源、上次刷新、保留/排除/未知计数。无源、空捕获、无匹配、全未确定、处理中、取消、语法错和存储错分别表达。旧结果仍可阅读时标“显示上次成功结果”，不能用新草稿标题冒充其来源。

规则面板提供文本编辑、基础字段表单、验证、样本预览、应用、保存修订/视图；中文名称和错误定位键盘可达。表单只生成所支持的字段片段，插入前显示文本，不反向重排未知语法或覆盖注释。语法诊断显示位置和修复建议，运行字段问题展示类型/状态/来源路径。

所有自定义提取字段默认敏感；内置方法/状态和规范化 host 可公开，path 沿用现有详情显示策略。组合/转换传播敏感性。组键在内部比较真实值，界面以稳定于当前结果的“组 1/组 2”等别名区分遮罩值；不能把多个 `••••` 当同一组。显式显示原值仅对当前页面/当前结果有效，换源/换修订重置；复制遵守当前遮罩，日志/截图自动夹具只用合成数据，不保存真实身份值或原文片段。

生产 UI 的基础尺寸为 1200×720、800×600、520×420，两主题；不要求把三栏塞进窄屏。窄屏用“组列表 → 时间线 → 原请求详情”返回栈；编辑器单独覆盖面板，停止代理始终可达。分组模式使用自己的量尺，不机械套用请求双行高度；原请求模式继续通过 V4 原量尺。

## 11. 中文注释示例与审查重点

下例说明后续实现的注释深度，是伪代码而非现有 API：

```csharp
// 设计思路：快照分析必须可复现，不能在扫描一半时接纳新提交的正文。
// 租约只保护本轮源文件；所有读取仍受冻结的 CommittedBytes 限制。
// 页面取消不会停止代理，finally 会归还租约和预算，避免旧页面拖住清理。
using var snapshot = await source.FreezeAsync(cancellationToken);
var candidate = await engine.AnalyzeAsync(snapshot, revision, cancellationToken);

// CancellationToken 不能撤回已经排入 Dispatcher 的回调，因此提交时再比身份。
// SourceInstanceId 区分相同 SessionId 的实时会话与历史存档，防止结果串源。
if (generation != currentGeneration || sourceId != currentSourceId)
{
    // 候选还未转交页面，丢弃时同时归还它持有的结果额度。
    candidate.Dispose();
    return;
}
// 成功提交后由页面持有结果额度；下次替换或页面关闭负责归还。
Publish(candidate);
```

解析器注释说明优先级和有界恢复；提取注释说明为何不解析 Partial JSON；分组注释说明 tuple 不用分隔字符串；测试注释解释输入为何能暴露回退/未知/重复的错误；门禁注释说明为何删除一个旧阶段文件也必须失败。无需给简单属性写长注释，重点是未来维护者容易误改的契约。

## 12. 固定验收样本与期望结果

冻结时刻为 `2026-09-30T12:30:00Z`，窗口 `[12:00:00Z,12:30:00Z)`；所有记录为完整元信息，默认来源 capture，tenant 从 query 提取。表中 u/t 值仅合成身份；R09 的字段回退走到尚未完成的 JSON 请求体，不能被后续位置救成 Missing。

| 记录 | 开始 UTC | tenant/caller | caller 位置或状态 | 补充条件 |
| --- | --- | --- | --- | --- |
| R00 | 11:59:59 | t1/u1 | Query | 快照仍接收，仅窗口前上下文 |
| R01 | 12:00:00 | t1/u1 | Query | 起边界计入 |
| R02 | 12:01:00 | t1/u1 | Header | 与 R03 同刻，Sequence 更小 |
| R03 | 12:01:00 | t1/u2 | Cookie | 与 R02 耗时条带重叠 |
| R04 | 12:02:00 | t2/u1 | JSON | 完整且有界 |
| R05 | 12:03:00 | t2/u1 | urlencoded 表单 | JSON 访问因明确媒体不适用返回 Missing |
| R06 | 12:04:00 | t2/u3 | `/actors/u3` 路径 | 其余来源明确无身份 |
| R07 | 12:05:00 | t1/u1 | Query | 与 R01 业务内容重复但 RecordId 不同 |
| R08 | 12:06:00 | t1/未知 | Missing | 所有位置均明确缺失 |
| R09 | 12:07:00 | t1/未知 | NotReady | 请求 JSON 未接收完整 |
| R10 | 12:08:00 | t1/u1 | Query | 合成来源 replay，默认排除 |
| R11 | 12:30:00 | t1/u1 | Query | 终边界排除 |

默认来源与 where 后：retained=12、sourceExcluded=1、outsideWindow=2、evaluated=9、filterTrue=9、filterFalse=0、filterUnknown=0、groupedRecords=7、unresolvedGroupRecords=2。R00 的上下文不增加这些计数。这里 filterUnknown=0 是因为 where 只检查 source；未知 caller 在分组阶段分类。

按 `(tenant,caller)` 得到 4 个业务组：`(t1,u1)` 3 条、`(t1,u2)` 1 条、`(t2,u1)` 2 条、`(t2,u3)` 1 条。按 caller 得到 3 组：u1=5、u2=1、u3=1。Missing 与 NotReady 分列，均不计为用户或有效组；R01/R07 不能业务去重。两次结果都应可逐条回到相同原记录，历史重算若 R09 已冻结为未结束，则显示 Unavailable(ArchivedIncomplete) 并解释差异，不谎称与实时等待状态完全相同。
