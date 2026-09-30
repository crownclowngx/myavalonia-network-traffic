# M2 · DSL 分组当前行为

系统学习与查阅请进入 [DSL 专用手册](../dsl/README.md)，包含逐步教程、完整语法和设计意图。本文保留当前行为摘要与运行边界。

更新：2026-09-30。本文对应 V5 实现；语言版本是 `dsl 1;`，与方案编号、插件版本分开。自动验证和真实环境未验项见 [V5 实施记录](../archive/2026-09-30-v5-m2-implementation.md)，操作步骤见 [快速开始](../quick-start/network-traffic-m2.md)。

## 输入、执行与边界

“分组”使用当前实时捕获或已打开的只读存档。每次应用固定整个保留集，不继承请求页的筛选、清空水位或滚动位置。读取原始 URI、请求头和请求体；不启动代理、不补发请求、不改写流量，也不读取响应体推断业务身份。现有捕获只有一份请求头/正文，不能把它称为未来改写前后两份事实。

编辑后 200ms 消抖只编译规则；验证、最多20条样本预览、正式应用相互独立。编译和读取都受预算限制。新候选成功后才替换旧结果；无效草稿、取消、超限、换源竞争不会把半批数据作为成功结果。当前结果的源、来源选择、修订和实际 UTC 窗口持续显示，不随保存提示变化。

样本预览按 StartedUtc、Sequence、RecordId 排序；“字段辅助 / 视图选项”可指定从第几条开始（1～10000），每次最多20条。超出当前保留数时明确显示0条。每次预览重新冻结，动态源的样本序号不代表永久记录身份；输出显示请求序列、字段类型/状态/来源、DSL 条件与各分组键分量，始终遮罩敏感值。预览不计算正式窗口/来源的全量覆盖，键可解释不代表请求已经入组。

新捕获只提示更新，点击“应用/刷新”才重算。默认来源为 capture；当前生产只提供 capture 和未知枚举对应的 unknown。manual/replay 只是分析契约预留名称，并不表示已经实现 M4 发送。

## DSL v1

```text
dsl 1;
field caller = firstNonEmpty(
    first(original.query["user"]),
    first(original.headers["x-business-user"]),
    first(original.cookies["business_user"]),
    toString(original.json("$.identity.user")),
    first(original.form["user"]),
    if(startsWith(original.path, "/actors/"), original.pathSegment(1), null)
);
view "调用过程" {
    where source == "capture";
    within last(30m);
    group by caller;
    columns caller;
    timeline by startedAt;
}
```

可复制的有效与无效文件见 [样例说明](../examples/m2/README.md)。声明顺序为版本、字段、一个 view；view 内依次 where、within、group by、可选 columns、timeline by startedAt。每项用分号结束；`//` 是行注释。名称使用 ASCII 字母/下划线开头，可含数字；保留字、内置字段和函数名不能作字段名。字段可以前向引用，循环、重名、未声明引用均拒绝。

| 语法 | 语义 |
| --- | --- |
| 字符串 | 双引号及 JSON 转义；Ordinal 比较，无隐式类型转换 |
| Number | invariant decimal 定点数；不接受指数、NaN、Infinity、千分位或外侧空白 |
| 比较 | 同类型相等/不等；大小比较只接受 Number |
| and / or / not | 三值逻辑、短路；Unknown 取反仍是 Unknown；只有 True 入组 |
| in | 标量和同类型列表；未命中但含未知成员时是 Unknown |
| first / all | 明确选择多值源的首项或全部；first 不跳过首项 Null |
| firstNonEmpty | 2～8个同类型标量；只跳过 Missing、Null、空字符串 |
| if | Boolean 条件，分支同类型；只读选中分支，未知条件不猜分支 |
| trim / lower / upper | 字符串规整；大小写转换不依赖设备语言 |
| contains / startsWith / endsWith | 两个字符串，Ordinal 判断 |
| toString / toNumber / toBool | 显式转换；Number 不能转 Boolean，Boolean 不能转 Number |
| isMissing / isNull | 检查相应状态；其他未知状态继续传播 |
| tuple | 2～4个标量组成类型化键；不得嵌套或展开列表 |

`toBool` 只接受 Boolean 或小写字符串 true/false；`toNumber` 接受 Number 或全串有效数字。JSON 的静态类型是 JsonValue；作为普通比较或组键前显式转换，数组须先 first。静态已知的非法组合报 DSL005，JSON 实际类型不符合时为 Invalid。没有 regex、任意 JSONPath、脚本、文件访问、网络访问、反射、响应/effective 字段或改写动作。

## 字段提取与状态

| 位置 | 访问 | 细节 |
| --- | --- | --- |
| Query | original.query["name"] | 键区分大小写；重复有序；严格解码一次；加号保留 |
| Header | original.headers["name"] | 名称不区分大小写；重复逐行保留，不拆逗号 |
| Cookie | original.cookies["name"] | 仅请求 Cookie；不拿 Set-Cookie 当请求值 |
| JSON | original.json("$.identity.user") | 属性、固定下标及引号属性；最多16段；拒绝重复属性 |
| 表单 | original.form["name"] | 仅 urlencoded；加号解为空格；重复有序 |
| 路径段 | original.pathSegment(1) | 从0开始；解码一次；中间空段保留 |

请求体必须完整、格式适用且在预算内。仅处理严格 UTF-8、identity/已解码实体或单层 gzip/br/deflate；不对部分正文容错猜值。已完成请求体可以在响应仍接收时使用。媒体类型明确不适用时为 Missing，可继续回退；multipart、未知类型/编码和非 UTF-8 为 Unavailable。一个记录的多个 JSON 字段共用一次读取和解析，结束就释放正文额度。解压输出逐块计入整轮预算，单体超限或损坏不能免计已消耗的字节。

| 状态 | 含义 | firstNonEmpty 是否继续 |
| --- | --- | --- |
| Value | 确定的类型和值 | 仅空字符串继续 |
| Missing | 完整输入中不存在或格式明确不适用 | 是 |
| Null | `?x` 无等号或 JSON null | 是 |
| NotReady | 实时请求体仍在接收 | 否 |
| Unavailable | 部分、未保存、历史未结束、缺文件或不支持格式 | 否 |
| Invalid | 损坏编码/JSON、重复属性或运行期类型错误 | 否 |
| BudgetExceeded | 单记录值、正文、解析或求值超限 | 否 |

取消、整轮字节/操作/内存超限是操作失败，不计为某条记录“缺失”。所有自定义字段默认遮罩；显示原值只对本页当前结果有效，换源或新结果重置。来源信息只包含访问位置/路径/状态，不复制原始正文。

## 计数、时间与身份

组键包含类型、长度和值；数值1与字符串"1"不同，decimal 1与1.00相同，分隔符不造成键碰撞。相同业务请求只要 RecordId 不同就分别计数。冻结快照如果重复提供同一 RecordId，则拒绝整次候选：生产冻结源应已唯一，不能猜选哪个修订。

未知分量进入未确定区域，逐条展示状态向量，例如 Value / Missing；不计为普通业务组或用户。组别名区分相同遮罩，组数仅代表本次规则的业务键数量。接口数按 Method+Host+Path，忽略 Query；错误数按 HTTP>=400 或传输 Failed 的并集，接收中/取消另列。

计数按“来源→窗口→条件→键”互斥分桶：

```text
保留 = 来源排除 + 窗口外 + 条件False + 条件Unknown + 条件True
条件True = 已分组请求 + 身份未确定请求
```

Total/Evicted 另述捕获历史，不把保留范围称为全历史。窗口前在途上下文是窗口外的额外投影，不再计入组数和主统计。

last 支持 s/m/h/d，最大七天；range 的两端必须带时区，采用 `[from,to)`。实时锚点在捕获锁内冻结记录时采集一次。V3 存档没有冻结时刻，其 CreatedUtc 是写入完成时间，所以历史 last 固定使用最后 StartedUtc 加1tick；空存档显示无记录，异常溢出拒绝分析。

时间线按 StartedUtc、Sequence、RecordId 排序。条带长短用现有单调耗时；重叠表示并行而非因果。接收中画开放端；零耗时仍能选择。墙钟完成时间与单调耗时差异超过1秒时提示异常，不用完成顺序重排。

## 修订、保存视图与寿命

规则库路径为 `%LOCALAPPDATA%/MyAvalonia/NetworkTraffic/Rules/rules.json`，与工作目录无关。保存视图绑定不可变 RevisionId，连同窗口覆盖、来源、列和排序原子提交。规则文本可含用户写入常量，库不保存捕获正文、提取结果或会话路径。

同目录临时文件、flush、原子替换、独占锁和 generation 比较防止半写或覆盖。冲突时先加载再重试或另存。损坏 JSON、未知版本、摘要错误和缺修订保留原文件，只有首次不存在才是空库。

共享存储公布已知代次，由已有页面节拍提示更新；不自动加载或替换其他页面的已应用修订。跨进程更新在加载/提交时发现。删除只针对选中的 ViewId，不删除修订；没有修订清理 UI。最多50视图、200修订、8MiB，达到上限明确拒绝。

每页最多一个分析/预览，容器最多两个；满额立即提示忙碌，不排无限队列。取消、换源、关页观察相同令牌，并复核操作/草稿/源实例代次。源租约在分析完成时释放，结果只保留有界投影；打开请求详情按当前源和 RecordId 再读取。关闭分析页不停止共享代理。

## 开发预算

生产常量在 DslLimits，独立 [M2 profile](../../tools/m2-profile.json)由测试核对；本地门禁固定检查阈值。

| 项目 | 上限/目标 |
| --- | --- |
| 编译 | UTF-8 64KiB、8192 token、深度32、字段32、诊断50、500ms |
| 值/集合 | 4096字符、128成员、来源8条、tuple4分量、显示列8个 |
| JSON | 深度32、16384 token、路径16段 |
| 正文 | 单记录编码/解码各256KiB；整轮读取/解码各32MiB |
| 共享额度 | 现有内容32MiB；分析候选与仍持有结果合计64MiB |
| 分析 | 10000记录、5000组、每记录10000/整轮500万求值、10秒 |
| 预览 | 最多20条、2秒，与正式查询共享资源 |
| 开发测量 | 10000条/32字段/5000组，暖身2轮后10轮 p95<=2秒 |
| 持续桌面测量 | 600秒、20并发含5流、20轮页面、每10秒刷新 |
| 继承资源 | 私有内存增量<=256MiB；UI p95<=200ms/max<=1秒；停止<=10秒 |

额度是拒绝边界，不承诺所有最大值可同时装入内存。超限候选整批失败；可缩小保留集或清除本页旧结果后重试。M0/M1/UX 的协议、图像、流式和原生存档限制继续适用。
