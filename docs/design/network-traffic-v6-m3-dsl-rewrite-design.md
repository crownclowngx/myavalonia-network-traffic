# V6 · M3 DSL 改写专项设计

> 日期：2026-09-30。状态：**待实现的设计契约，不是当前运行说明**。
> 配套：[V6 执行计划](../roadmap/network-traffic-v6-m3-dsl-rewrite-plan.md)、[V6 验证矩阵](../maintenance/network-traffic-v6-m3-verification-plan.md)。当前语言仍为 [M2 DSL v1](../reference/network-traffic-m2-dsl.md)。

## 1. SOLID 与依赖方向

M2 对已经捕获的事实做只读分析，M3 决定尚未发往上游的请求。两者共用字段语言与纯求值语义，读取时机、预算、失败和发送权限独立。

| 原则 | 具体设计 | 审查依据 |
| --- | --- | --- |
| S 单一职责 | 编译器产出程序；字段源读取原始输入；规划器产出候选；适配器提交网络；捕获器记录事实；页面处理操作 | 不让 TitaniumProxyRuntime 解析 DSL，不让 CaptureWorkspace 成为规则库/JSON 修改器 |
| O 开闭 | 有限动作模型和清晰分支；增加动作只扩展对应语义/校验/适配，不改运行所有权 | 不为可能出现的第三方动作建设动态注册平台 |
| L 替换 | 真实/Fake 字段源在 Missing、Invalid、预算、取消上同契约；预览因原始数据缺失返回 Unknown | Fake 不把缺 Body 当空 JSON，不用同步成功掩盖真实迟到/半写/发送失败 |
| I 接口隔离 | IFieldSource 只读字段；改写库存储只管加载/原子提交；目标检查只返回候选地址验证结果 | UI/预览不持发送会话；纯动作不用一类一个接口 |
| D 依赖倒置 | Application 只依赖领域类型和窄端口；Infrastructure 实现 Titanium/文件/DNS；Features 调用应用用例 | Application 不引用 Titanium、Avalonia、文件路径或 IServiceProvider |

使用手写有限文法、纯函数、构造注入、不可变快照、普通 MVVM 与必要适配器。共享协调器是一个容器内的服务，不是静态全局单例。模式用于明确边界，不用于增加层数。

关键中文注释至少说明：为什么所有表达式读取 original、为何不回退原目标、发送状态为何可能未知、Body 如何计费与释放、停用为何不撤销在途快照、v1 为什么不能补造原始头、h1 模式为何需启动前选择。测试注释说明观测点和错误实现会如何失败。

## 2. 模块和数据契约

```text
Features/Rewrite：草稿、验证、样本预览、保存、启用/停用
                  ↓
Application/Dsl：公共表达式 / v1 分析前端 / v2 改写前端
Application/Rewrite：原始输入 → 纯规划器 → RewritePlan 或 Blocked
                  ↓ 窄端口
Infrastructure/Rewrite：发送前字段读取、原子改写库存储、目标检查
Infrastructure/Proxy：单请求接纳 → 一次提交 → 上游传输
Infrastructure/Capture：原始/有效事实、轨迹、存档
```

以下是拟新增概念，命名可按仓库风格小调；职责与事实边界不能合并丢失。

| 类型/服务 | 必须包含的事实 | 所有权 |
| --- | --- | --- |
| RewriteProgram | languageVersion、字段及类型表、规则、有界动作、静态诊断 | 编译后不可变，不持流或 UI |
| RewriteRevision | revisionId、sourceHash、sourceText、parentRevisionId、createdUtc | 已保存后不可变 |
| ActiveRewriteSnapshot | proxyGeneration、activationGeneration、mode、revisionId/hash、program、enabled | 容器共享，接纳时一次读取 |
| OriginalRequest | requestId、method、原始转义 URI、有序多值头、原始 Body 读取句柄 | 单请求拥有；句柄不进入存档或 UI |
| RewritePlan | 候选 URI/头/Body、选定路由、实际动作、差异、资源租约 | 单请求候选；成功前不触及第三方请求 |
| RewriteTrace | 修订/代次、逐规则结论、动作来源/冲突、错误阶段、时间/预算、截断标志 | 捕获事实，不保存 SessionEventArgs |
| RewriteOutcome | Disabled/Unmatched/Applied/Blocked；发送状态独立 | Applied 表示计划提交成功，不等于服务器执行成功 |
| RewriteCoordinator | 保存后的显式启用、CAS 代次检查、停用及模式约束 | 一个插件容器一份；页面不拥有其停止权 |
| RewriteWorkspace | 草稿、诊断、预览、选中修订、局部忙碌/取消代次 | 每 Document 独立；全局运行状态只读投影 |

`DslProgram` 目前包含分析窗口与分组，不适合作为改写上下文。拆出只含字段/表达式类型的公共上下文，保持旧分析入口和测试；不要在 M3 构造假的 Filter/Window/GroupKeys。预算按调用场景注入，不能将分析的 QueryOperations/十秒期限搬到发送前回调。

## 3. DSL v2 的首版文法

### 3.1 版本与完整示例

v1 分析和 v2 改写使用不同编译入口与编辑区域，首版不允许同文件混合 view/rewrite。下例是**计划语法，当前编译器不能执行**；实施后必须成为编译和行为夹具。

```text
dsl 2;
field caller = first(original.headers["X-Caller"]);

rewrite "指定调用者切测试环境" {
    priority 10;
    for host "api.example.test";
    when caller == "tester-a";
    route to "https://stage.example.test:8443";
    set path "/v2/orders";
    set query["debug"] = "1";
    remove query["legacy"];
    set header["X-Environment"] = "stage";
    remove header["X-Old-Debug"];
}

rewrite "标记该调用者的 JSON 请求" {
    priority 20;
    for host "api.example.test";
    when caller == "tester-a" and original.method == "POST";
    set json("$.context.environment") = "stage";
    remove json("$.debugToken");
}
```

第二条规则显式要求 JSON 动作；因此同条件的非 JSON POST 会被阻断并说明不支持。不能遇到不支持正文就跳过动作后仍发送。需要区分接口时，用户应在 when 中限定 original.path 或其他确定字段。

```text
program       := "dsl" "2" ";" field* rewrite+
rewrite       := "rewrite" string "{" priority scope condition action+ "}"
priority      := "priority" unsignedInteger ";"
scope         := "for" "host" string ";"
condition     := "when" expression ";"
action        := "route" "to" string ";"
              | "set" "path" expression ";"
              | "set" ("query"|"header") "[" string "]" "=" expression ";"
              | "remove" ("query"|"header") "[" string "]" ";"
              | "set" "json" "(" string ")" "=" expression ";"
              | "remove" "json" "(" string ")" ";"
```

字段定义、字符串转义、函数白名单、运算符优先级、类型、三值逻辑及六位置读取继承 v1。大小写约定沿用现有语言；关键字使用上述小写。`priority` 必填，0～9999，程序内唯一；规则名非空且唯一。一个规则最多一个 route，至少一个动作。`when true;` 表示作用域内全量规则。

### 3.2 编译时必须拒绝

- v2 内 view/within/group/timeline、响应状态/响应头/响应体、effective.*；发送前不可获得这些事实。
- 动态目标主机、通配域名、正则、通用 JSONPath、循环、任意脚本/网络/文件/进程调用。
- 未知字段、依赖环、重复规则名/优先级、未显式从列表取值、非法动作类型。
- route 含 userinfo、非 HTTP(S)、路径（除 `/`）、Query 或 fragment；set path 可确定的非法常量。
- 协议控制头、单规则重复写同一个 Query/Header/JSON 路径、JSON 祖先/后代竞争、根节点替换/删除。

运行时才能确定的值仍要再次验证，不能只检查字面量。Query/Header/path 要求确定 String；JSON set 接受确定 String/Number/Boolean 或字面量 null，拒绝 List/Tuple/Json 对象。不做隐式数值字符串转换，动态 Null 不被误判为可用字符串。

诊断建议固定：`RW001` 版本/文法，`RW002` 名称/优先级，`RW003` 不可用字段/动作，`RW004` 类型，`RW005` URI/路径，`RW006` 保护头，`RW007` 写冲突，`RW008` 正文格式/完整性，`RW009` 预算，`RW010` 运行模式/循环。保留原有 DSL 诊断的字段依赖、跨度和中文建议，不因新增 v2 改变 v1 错误码。

## 4. 决策顺序、Unknown 与冲突

1. 接纳请求时固定运行代次、启用代次与不可变修订。禁用则走原捕获路径，记录 Disabled；启用但没有 host 作用域匹配则 Unmatched。
2. host 按 IDN ASCII 规范化、大小写不敏感、去除一个末尾点后精确比较；作用域不含端口，不做子域后缀匹配。URL 验证与连接 authority 另行处理。
3. 按 priority 从小到大对**原始输入**求值。字段按需计算，同请求缓存一次；False 跳过，True 收集动作，Unknown 记录原因并阻断请求。
4. 首条 True 且含 route 的规则确定目标。后续命中规则的 route 标为 Shadowed，不二次路由；其其他动作仍参与规划与冲突检查。
5. 所有动作表达式仍读取 original，不读前一动作的中间结果。先求值/校验，再检查写集合，最后构建候选。
6. 路由 → path → Query → Header → JSON → 协议派生字段重建为固定应用顺序；各阶段内按 priority/源码动作次序排序。由于写冲突被拒绝，顺序不承担隐式覆盖含义。
7. 完整候选通过协议/资源/目标校验才可提交一次；失败丢弃候选，原始快照保留。

冲突键：path 是一个整体键；Query 为解码后的大小写敏感键；Header 为小写名称；JSON 为解析后的属性/下标路径。不同规则共命中时，重复写同一键即冲突，哪怕值相同也拒绝；set/remove 竞争同样拒绝。JSON 相同或父子路径冲突；不同属性可以组合。route 的后续遮蔽是唯一明确的优先级选择，不按“最后写入获胜”处理其他动作。

首版固定 **block** 失败策略，无 bypass 开关。作用域内条件 Unknown、Body 不可读、值非法、冲突、超限和目标校验失败均阻断，不静默发送原环境。Missing/Null 继续按 v1 三值语义处理；用户可以使用 v1 已有的显式回退表达式将符合预期的缺失转为确定值。作用域外请求不受这些规则错误影响。

例如 `caller == "tester-a"` 遇到缺失 caller 会得到 Unknown 并阻断；如果业务约定缺失代表普通流量，应在字段定义中显式回退到确定字符串，再预览验证 False。界面必须说明这种差异，不能把所有未命中都混作同一种状态。历史样本预览中的 block 只是预测结论，不会阻断任何正在运行的请求。

## 5. 各动作的字节语义

| 动作 | 固定语义 | 必须保留/拒绝 |
| --- | --- | --- |
| route to | 同协议替换 origin，包括目标端口；无端口使用协议默认端口 | 未改 path/Query 的转义文本逐字符保留；运行时发现跨协议即 block |
| set path | UTF-8 URI 的 escaped absolute-path，必须以单 `/` 开头；合法 `%HH` 保留 | 不含 authority、`?`、`#`、反斜线/控制字符；拒绝双斜线开头及解码后完整 `.`/`..` 段；不二次编码 |
| set query | 键是解码后文本，区分大小写；删除同键全部旧项，在首个旧项位置插入单一新值，无旧项则尾加 | 新键/值按 UTF-8 百分号编码，空格为 `%20`，字面 `+` 为 `%2B`；未触及项原样、原序保留 |
| remove query | 删除全部同键项，缺失为显式 NoOp | 原 Query 中损坏转义导致动作无法可靠定位则 block；无 Query 动作时不重写原串 |
| set/remove header | 名称不分大小写；set 替换所有同名行，remove 删除所有同名行 | 不折叠未修改多值头；缺失删除为 NoOp；拒绝非法名称和 CR/LF/NUL 值 |
| set json | 固定路径，父节点必须存在；对象属性可新增，数组只替换已有索引 | 不自动建父对象，不扩张数组，不批量选取；严格类型，不静默转字符串 |
| remove json | 仅删除对象属性，末端属性缺失 NoOp，父路径缺失/类型错误为失败 | 不删除数组元素、不支持根节点，避免索引漂移影响后续动作 |

协议保护头包括 Host、Content-Length、Transfer-Encoding、Content-Encoding、Connection、Proxy-Connection、Keep-Alive、TE、Trailer、Upgrade、Expect 及 `:` 开头的伪头；还包括当前 Connection 指定的逐跳字段。Content-Type 首版也由原请求及正文处理器管理，不开放任意改写以免提取与发送类型不一致。

Cookie、Authorization、Origin、Referer 和普通业务签名头默认保持，允许显式 set/remove。域名切换不承诺凭据跨环境通用；不自动复制凭据到外部存储，不自动重签或替换 Origin。Body 改变时派生处理器移除 Content-MD5、Digest、Content-Digest、Repr-Digest 等旧实体摘要，并由适配器重建 Content-Length/framing；这组派生字段也禁止用户动作写入。业务自定义签名若需移除，由规则明确声明。

JSON 仅支持 `application/json` 或 `application/*+json`，无 charset 或 UTF-8，identity/无 Content-Encoding。严格 UTF-8、无 BOM、无注释/尾逗号，拒绝重复对象属性。输入数字必须在当前 DSL decimal 可表示范围内；序列化不得经 double 丢失精度。未修改 Body 不重新序列化；修改后空白/属性排版不保证逐字保留，保留原始字节用于追查。

## 6. Body 的发送前读取与资源契约

### 6.1 读取时机

host 与可短路的头条件先执行。只有到达需要 JSON/form 的表达式或 JSON 动作，才申请 Body 槽位和共享内存。大文件、multipart、压缩体及持续上传可以穿过**纯头规则**，不能为了确定“没有 Body 动作”而先读取全量 Body。

Body 字段首版支持 identity 编码、严格 UTF-8 的 JSON 和 urlencoded 表单；表单 `+` 解为空格，Query `+` 保持加号，与 M2 规则一致。只修改 JSON，不修改表单正文。对于依赖 Body 的请求，已知 Content-Length 超限先拒绝；未知长度最多读取上限加一个探测字节，超限立即结束当前请求。不能等待无限 chunked 请求结束。

客户端 `Expect: 100-continue` 可以得到本地中间响应以允许有界读取，但在候选完成前，上游端点不得观察到请求头、Body 或应用调用。发送前探针需验证第三方 API 的真实行为；不得先发上游头再将“未发 Body”冒充“未发送请求”。

需要缓冲但没有修改的 Body 也必须按原始字节发送；一次读取不能被多个字段、捕获回调或发送器重复消费。纯头路径仍流式转发，响应始终沿用 M1 的增量捕获，不受请求 JSON 缓冲策略牵连。

### 6.2 初始实现预算

以下为计划开发 profile，实施时在生产常量、`m3-profile.json`、测试和文档四处核对；是验收阈值，不是已测性能承诺。

| 项目 | 初始上限/阈值 | 超限行为 |
| --- | --- | --- |
| 规则文本/词法/表达式 | 沿用 64 KiB、8192 token、深度 32、字段 32、诊断 50 | 编译失败，旧生效修订保持 |
| 规则/动作 | 32 条规则；每规则 16 动作，程序合计 128 动作；优先级 0～9999 | 编译失败 |
| URI/头 | 最终 URI 8192 字符；原始/有效头各 64 KiB，最多 256 行 | 候选拒绝；禁止无界复制 |
| Body 输入/输出 | 各 256 KiB；未知长度额外最多 1 字节；JSON 深度 32/token 16384/路径 16 段 | 413/结构化预算错误，零上游发送 |
| 单请求 Body 预留 | 4 MiB，覆盖原始、候选、解析树与辅助对象；容器最多 4 个槽 | 从既有 32 MiB ContentMemoryBudget 预留；不足立即 503，不无限排队 |
| 求值 | 每请求合计 10000 操作；元信息路径单调期限 50 ms | RW009 + block；不每条规则重新获得额度 |
| Body 读取和规划 | 从开始读取计 2000 ms，内部计算继续受操作数限制 | 504/结构化超时；清理候选与租约 |
| 目标解析/检查 | 最长 1000 ms，最多 16 个候选地址 | 无可用或超限则 block，不使用原目标 |
| 在途请求 | 沿用 MaxActiveRequests≤20 | 503；不因 M3 增加无限队列 |
| 轨迹/差异 | 每记录最多 16 KiB，容器保留轨迹≤16 MiB | 保留关键结论，明示详情截断/淘汰；不标完整轨迹 |
| 预览 | 每页 1 项，容器最多 2 项，每项至多 20 样本 | 忙碌反馈；读取/解析使用相同共享内存额度 |
| 取消/停止 | 取消后 1000 ms 内释放业务工作；全局 Stop≤10000 ms | 测试失败并定位资源所有权 |

固定预留不是允许忽略真实对象开销；解析器在 token/节点创建前计费，测量解析树最坏开销，不能“先分配再检查”。取消和停止不得以放弃等待代替释放。网络写出缓冲的所有权延续到库不再引用它，不能在提交返回时提前归还并复用内存。

## 7. 代理协议与发送失败

固定 7.0.13 已有明确边界：普通捕获可协商 h2，目标映射运行仅 h1。V6 不承诺扩大该能力；M3 使用启动时确定的两种模式：

| 运行模式 | 行为 |
| --- | --- |
| 普通捕获 | 保持 M1/M2 和 h2 空闲仅提示策略；不能在运行中启用 M3 |
| 改写（HTTP/1.1） | 启动前固定仅协商 h1；允许运行中切换/停用规则；停用后仍保持 h1 至下次启动 |

UI 明示允许回退的客户端实际为 h1，exact h2 失败。切模式需用户停止并重新启动代理；热切修订不修改 ALPN、不断开其他请求。既有 M0 Mapping 只用于既有兼容测试路径，与 M3 程序互斥；不得先走 Mapping 再走 DSL。

目标连接、Host/authority、TLS SNI 和证书主机名校验由同一最终 origin 驱动；端点回执验证真实 SNI/Host/端口，不只检查内存 URL。保留证书校验，不以 AcceptAnyCertificate 或吞异常解决目标失败。

循环防护至少覆盖 localhost、IPv4/IPv6 回环、规范化主机和解析到本监听端口/地址的别名；上游代理转发仍关闭，不新增二次经过本代理的 HttpClient。目标检查与实际连接地址应一致。V6-02 验证公开 API 能否固定已验证地址；若不能，保守拒绝存在回环歧义的同监听端口目标，不声称一次 DNS 预检消除了连接时变化。测试包括别名/多地址和检查后变化，不通过修改系统 hosts 实现。

| 阶段 | 结果语义 | 客户端/记录 |
| --- | --- | --- |
| 未提交即语义/冲突失败 | Blocked + NotSent | 本地 502 与安全诊断码；原目标/候选目标接收次数均为 0 |
| 输入/输出超限 | Blocked + NotSent | 413；错误阶段和实际已读字节 |
| 无槽位/正在停止 | Blocked + NotSent | 503；不排无限队列 |
| 发送前 Body/目标检查超时 | Blocked + NotSent | 504；释放限额，不回退 |
| 候选已提交，库尚不能证明发出与否 | Applied + MayHaveBeenSent | 明确发送结果未知；不宣称取消撤销了请求 |
| 上游响应已到 | Applied + ResponseObserved | 实际 HTTP 状态/协议、传输完整性分别记录 |
| 发送中断开/超时/取消 | 保留 Applied，传输失败或取消，发送状态按观测 | 无自动重试、无回原环境、无 POST 重放 |

第三方没有“首字节已写”可靠回调时，从提交边界保守进入 MayHaveBeenSent。捕获记录的 HTTP 状态需区分 LocalProxy 与 Upstream，避免把本地 502 当成目标服务器回执。

## 8. 原始/有效快照与存档兼容

原始快照在任何修改前获取；有效快照在提交时获取。网络实际传输事实另列，不把预览结果填入实际请求。失败规划没有有效已发送请求，只可展示候选及失败位置。

| 数据 | 要求 |
| --- | --- |
| 原始/有效 method、URI、头 | 有序、多值、身份稳定；受限存储时明确 metadataPartial |
| 原始/有效 Body | 独立 BodyId/描述符；同字节可共享不可变文件引用并仅计一次磁盘；被修改的两份分别计费 |
| 规则追踪 | revisionId/hash、activationGeneration、proxyGeneration、RuleName/priority、Matched/False/Unknown/Shadowed、动作及 NoOp/冲突 |
| 差异 | 字段位置、修改类型、原/新状态及安全摘要；默认遮罩业务字段/正文值 |
| 发送结果 | NotSent/MayHaveBeenSent/ResponseObserved、失败阶段、响应来源、实际协议；无证据不推定到达 |
| 覆盖范围 | Body 完整性、存储水位、轨迹截断、磁盘/内存淘汰和旧格式缺失 |

body 方向 Request/Response 不足以区分 original/effective，须增加快照角色或明确请求快照引用。现有 `RequestBody` 的读者逐一迁移，不能让新有效 Body 被 M2 当成 original。OnRequestBody 观察到的是实际发送路径，不能覆盖此前保留的原始 Body 或累计两次字节。

新增原生存档 schema=2，规则库仍使用自己的 schema，不复用存档编号。v2 保存完整记录引用与可用正文；Freeze 固定元信息/轨迹修订及已提交正文水位，沿用租约和配额。调整文件数上限为记录数与**唯一正文引用数**推导的有界值，重新校验重复引用、摘要、截断、路径越界、符号链接/重解析点和中断恢复。

v1 读取路径保持兼容：沿用已保存 OriginalUri/EffectiveUri；只有一份头/Body 时标 LegacyUnknown，不能复制成两份后宣称“无差异”。历史 M2 分组保留既有 v1 读取契约并披露其原始头不能完全证明；对 v2 使用真正 original。未知 schema 拒绝，旧文件不原地升级；另存生成 v2 时仍保留 LegacyUnknown。

快照保存、重开、导出和 M2 重算全部零网络/零启用副作用。日志和自动证据只含合成样本、身份和安全摘要；真实正文仍按原捕获存储规则处理，不把它们复制进规则库或门禁报告。

## 9. 规则库、快照切换与页面寿命

独立 `RewriteLibrary` 保存有限个不可变修订（初始≤200）及命名配置（≤50），总文件≤8 MiB。读写两端校验版本、hash、引用、父修订环和可编译性。使用 expectedGeneration 原子提交，临时文件写完验证后替换，冲突不静默最后写入覆盖。

Draft、CompiledCandidate、SavedRevision、ActiveSnapshot 分开。保存只影响库；启用选定已保存修订且检查 expectedActivationGeneration。修订对象全部构造完成后一次原子交换；请求的接纳点在活动请求登记和快照读取的同一同步区间，切换与接纳以此建立先后关系。

停用交换为 Disabled，仅影响之后接纳的请求；已接纳的慢 Body/长响应继续固定旧修订。需要停止这些请求使用全局 Stop，不用停用规则冒充取消在途。库里旧修订被删除时，活动/在途引用仍有效；首版可禁止删除被引用修订以简化管理。

多页共享运行状态，编辑/预览各自独立。await 后和 UI 投递时复核页面、源、草稿及操作代次；旧 finally 只释放自己持有的忙碌资格/取消源。关闭任一 Document 取消该页预览，不停代理；插件 Shutdown 才拒绝接纳、取消有界工作并释放订阅/监听器。

进程重启默认 Disabled，保存视图和改写库加载不启动网络。自动加载错误不覆盖旧库，也不恢复旧启用状态。无隐式“启用上次规则”。

## 10. 固定样本与 UI 交付

受控测试用 A=`127.0.0.1`、B=`localhost`、C=另一受控 origin，随机端口、不同端点身份与有效测试证书。文档中的 `.example.test` 只表达语言，不联网解析。每个样本以唯一请求标识关联客户端、规划、端点和捕获记录。

| 样本 | 输入/规则 | 必须结果 |
| --- | --- | --- |
| E01 | A 请求、无匹配 host | A 接收 1，B/C 为 0；原始 URI/Body 不变 |
| E02 | A、`when true` 路由 B | B 接收 1，A/C 为 0；Host/SNI/端口指向 B |
| E03 | X-Caller=a 命中 B；b 不匹配 | 两条请求各到一个正确端点；不串共享目标 |
| E04 | 两个 True route，优先级 10/20 | 只路由一次到 B，后者 Shadowed；不路由到 C |
| E05 | 两条规则均写同一个 Header | Blocked，A/B/C 应用接收均 0；冲突列出两位置 |
| E06 | Body 中 caller 尚不完整/超限/编码不支持 | block，不能把 Unknown 当不匹配后发 A |
| E07 | Query 重复键和 `%2f`、`+`；只改 debug | 所有未触及项保持原文本；端点结果与候选一致 |
| E08 | JSON 嵌套修改，原始 Body 完整可存 | 原始与有效摘要不同且各正确；长度按 UTF-8 字节 |
| E09 | 已接纳旧修订后热切新修订 | 旧请求按旧目标，新请求按新目标；同一请求不混用 |
| E10 | B 连接/证书失败或已接收 POST 后断开 | A 接收 0；B 最多一次；发送结果按真实观测 |
| E11 | 大上传只命中头规则、响应 SSE | 请求不整包缓冲，首条事件在结束前可见 |
| E12 | 打开 v1/v2 存档并预览 | 零网络，v1 缺失不补造，v2 可追查修订/差异 |

改写编辑器提供验证/预览/保存/启用四个不同动作。预览展示样本身份、修订候选摘要、规则命中、目标、差异、Unknown 和 block 理由；所有结果带“预览，未发送”。真实详情展示运行修订与发送状态，不能复用预览徽标。

UI 固定布局为编辑器、请求差异详情各 3 个尺寸（1200×720、800×600、520×420）×两主题，共 12 组合；另覆盖诊断、阻断、原生存档缺失、启用代次冲突 4 张行为图。宽屏对照，窄屏逐层导航；停止和停用入口、模式说明、错误原因必须可达。截图和 Headless 操作不等于真实 Host/物理输入验收。

实现后的当前用法另写 reference/quick-start 和 DSL 第四章；本文件在实现偏离时补决策依据，不把尚未执行的阈值或示例标为已验证。
