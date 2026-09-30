# 02 · DSL v1 语法手册

[返回手册首页](README.md) · [入门教程](01-tutorial.md) · [设计意图](03-design-rationale.md)

本手册描述当前生产编译器与求值器接受的语法。关键实现见 [DslCompiler](../../src/NetWorkTrafficPlugin.Plugin/Application/Dsl/DslCompiler.cs)、[FieldEvaluator](../../src/NetWorkTrafficPlugin.Plugin/Application/Dsl/FieldEvaluator.cs) 和 [RequestFieldReader](../../src/NetWorkTrafficPlugin.Plugin/Infrastructure/Analysis/RequestFieldReader.cs)。

## 1. 完整程序结构

以下是结构说明，尖括号中的中文是占位说明，不能原样粘贴执行：

```text
dsl 1;
field <字段名> = <表达式>;          // 可以没有字段，也可以声明多个
view "<视图名称>" {
    where <布尔表达式>;
    within <时间窗口>;
    group by <键表达式>, <其他键表达式>;
    columns <字段名>, <其他字段名>; // 整个 columns 子句可以省略
    timeline by startedAt;
}
```

- 程序先声明 `dsl 1;`，接着是 0～32 个字段，最后恰好一个 `view`。
- view 内子句顺序固定：`where → within → group by → 可选 columns → timeline`。
- 每条字段声明和每个子句以分号结束；view 的右花括号后不加分号。
- `where`、`within`、`group by`、`timeline` 都不能省略。不需要过滤时写 `where true;`。
- `group by` 接受 1～4 个标量键，或者单独一个 `tuple` 表达式。
- `columns` 只能列出已声明的字段名，最多 8 个且不能重复；不能直接写访问器或任意表达式。省略后没有额外业务显示列，但仍会计算分组键。
- `startedAt` 是 timeline 子句的固定名称，不能作为普通表达式读取；当前没有可自定义的 timeline 排序表达式。

## 2. 词法、字面量与字段引用

| 项目 | 规则 | 示例 |
| --- | --- | --- |
| 字段名 | 首字符 ASCII 字母或下划线，其余为 ASCII 字母、数字、下划线；区分大小写 | `caller`、`tenant_id`、`_tag2` |
| 关键字与函数 | 名称和大小写固定，不能作为自定义字段名 | `field`、`firstNonEmpty` |
| 字符串 | 双引号，使用 JSON 字符串转义；内容可含中文，不允许直接跨行 | `"订单"`、`"a\"b"`、`"\n"`、`"\u4E2D"` |
| Number | 有限十进制定点数，可带负号；用点作小数点 | `0`、`-2`、`12.50` |
| Boolean | 小写字面量 | `true`、`false` |
| Null | 小写字面量，用于明确空值或条件分支 | `null` |
| 字面量列表 | 最多 128 个同类型、非 null 标量字面量；允许空列表 | `["GET", "POST"]`、`[200, 201]`、`[]` |
| 注释 | `//` 到行尾；不支持块注释 | `// 提取调用者` |
| 空白 | 标记之间可空格、换行 | 多行条件、函数参数 |

数字不支持正号前缀、指数、NaN、Infinity、千分位、`.5` 或 `1.`；小数位最多 28 位，当前数字解析器还限制去除前导零后的数字总位数不超过 28。`toNumber` 使用相同规则，不会自动去掉首尾空白，需要时先 `trim`。

列表不能写成 `[caller, "u2"]`、`[1, "2"]`、`[null]` 或嵌套列表。需要两个字段组成分组身份时，使用 `tuple`。

字段可以引用前面或后面声明的字段；编译器检查所有字段，包括未被 view 使用的字段。重名、未知字段和循环引用都会使整份规则编译失败。

保留名包括下面两组，不能写成 `field <保留名> = ...;`：

```text
dsl field view where within group by columns timeline startedAt
source statusCode original true false null and or not in last range
route rewrite send effective response

first all firstNonEmpty if trim lower upper contains startsWith endsWith
toString toNumber toBool isMissing isNull tuple
```

`route`、`rewrite`、`send`、`effective`、`response` 是预留边界，并不代表当前存在对应功能。

## 3. 类型与状态是两个维度

静态类型由编译器推导，不需要写类型注解：

| 类型 | 含义 | 用法 |
| --- | --- | --- |
| String | 字符串 | 身份、Host、Method、Path |
| Number | decimal 数值 | 状态码、金额、显式转换的数字 |
| Boolean | 布尔值 | where、if 条件 |
| List | 有界列表，通常是重复参数的值 | 用 first 选值，用 all 保留列表 |
| Json | JSON 路径在编译时尚未确定的实际类型 | 显式转换为标量；数组先 first 再转换 |
| Tuple | 2～4 个标量组成的有序组合键 | 单独用作 group by |
| Null | null 字面量的类型 | 与兼容的 if 分支或 firstNonEmpty 配合 |

普通标量指 String、Number、Boolean。Json、List、Tuple、Null 不属于普通标量。`Json` 是编译期类型；运行时成功读取的 JSON 标量或数组会呈现其实际类型，因此预览看到 String/Number/List 并不矛盾。

每个值还携带独立状态：

| 状态 | 意义 | 常见原因 |
| --- | --- | --- |
| Value | 有确定载荷 | 字符串 `"u1"`、数值 200、false；空字符串也是 Value |
| Missing | 已知输入中没有这个值 | Query 无对应键、JSON 缺属性或数组越界、格式明确不适用 |
| Null | 输入明确为空值 | JSON null；Query 的 `?user`，没有等号 |
| NotReady | 当前输入仍可能继续到达 | 实时请求正文仍在接收 |
| Unavailable | 本次数据无法提供可靠的完整值 | 部分正文、未保存、历史未结束、文件缺失、不支持的编码 |
| Invalid | 已提供的数据或运行时类型无效 | 损坏编码、重复 JSON 属性、非法转换、路径经过错误结构 |
| BudgetExceeded | 单值或单记录处理达到预算 | 字符串、列表、正文或解析工作超限 |

Missing、NotReady 等没有对应的可书写字面量，它们由读取和求值产生。运行时缺失的 String 字段仍保留 String 类型。不要用空字符串代替所有未知状态，也不要使用 `x == null`；判定明确空值使用 `isNull(x)`。

整轮操作数、累计字节、整体分析内存、时间等总预算耗尽会终止本轮操作，不是把剩余记录标成 Missing 后宣告成功。

## 4. 可读取的位置

| 表达式 | 静态类型 | 规则 |
| --- | --- | --- |
| `source` | String | 当前生产值 `"capture"`；无法识别的未来来源映射为 `"unknown"` |
| `statusCode` | Number | 已记录的响应状态码；没有状态码时为 NotReady 或 Unavailable |
| `original.host` | String | 原始 URI 的 Host，IDN 形式、小写，不包含端口 |
| `original.method` | String | 已记录的方法名，统一大写 |
| `original.path` | String | URI 的 AbsolutePath，保留转义形式，不含 Query |
| `original.pathSegment(1)` | String | 非负整数下标，从 0 开始，百分号解码一次 |
| `original.query["user"]` | List of String | 名称区分大小写；重复参数按原顺序保留 |
| `original.headers["X-User"]` | List of String | 名称不区分大小写；重复 Header 行保留，不按逗号拆值 |
| `original.cookies["user"]` | List of String | 只解析请求 Cookie，名称区分大小写，不读响应 Set-Cookie |
| `original.json("$.identity.user")` | Json | 只读取请求正文中的有限 JSON 路径 |
| `original.form["user"]` | List of String | 只解析 application/x-www-form-urlencoded，名称区分大小写 |

`original` 指当前捕获中保留的原始请求事实。当前没有另一套可供 DSL 选择的请求改写后 Header/Body，也没有 `effective` 或 `response` 访问器；`statusCode` 只是独立公开的响应元数据。

### 重复值与解码

- Query 和表单按 `&` 分项、首个 `=` 分开名称和值，严格解码一次。Query 的 `+` 保留为加号，表单的 `+` 解释为空格。
- `?user` 的值为 Null，`?user=` 的值为空字符串，完全没有 user 时为 Missing。
- Header 不进行 URL 解码。Cookie 按请求头的分号分项、首个等号分开，值不再做 URL 解码。
- `/actors/u1` 的第 0 段为 `actors`，第 1 段为 `u1`。`/actors//u1` 的第 1 段是空字符串；根路径 `/` 没有段。内部空段和末尾空段不会自动忽略。
- 损坏的百分号或 UTF-8 输入不能通过宽容解码冒充身份；访问器可能因此返回 Invalid，而不是只忽略坏片段。

### JSON 路径

路径从 `$` 开始，只支持属性和固定的非负数组下标，最多 16 段：

| 路径意图 | DSL 表达式片段 |
| --- | --- |
| 普通属性 | `original.json("$.identity.user")` |
| 数组中第一个对象的属性 | `original.json("$.items[0].id")` |
| 带连字符的属性名 | `original.json("$[\"user-id\"]")` |
| 中文属性名 | `original.json("$[\"用户\"]")` |
| 根元素本身 | `original.json("$")` |

点号属性使用 ASCII 标识符；其他名称使用方括号内的双引号属性。外层也是 DSL 双引号字符串，因此内部双引号必须转义。不支持单引号路径、通配符、递归下降、切片、负下标或过滤表达式。

读取不存在的属性或越界下标为 Missing；例如对字符串继续读取 `.user`，属于结构不匹配，为 Invalid。对象不能直接转换成普通字段值；可以继续用路径选出其标量属性。

数组只接受同类型的标量成员，可包含 Null；嵌套数组、对象成员或混合标量类型会得到 Invalid。从标量数组取值的片段为 `toString(first(original.json("$.users")))`；直接调用 `toString(original.json("$.users"))` 不会把数组序列化为字符串。

### 请求正文可用条件

正文提取要求适用且完整的请求正文。支持 JSON、`+json` 类型和 URL 编码表单，使用严格 UTF-8；支持 identity、已解码实体，或单层 gzip/br/deflate。显式非 UTF-8 字符集、未知正文表示和不支持的内容编码返回 Unavailable。

判断先考虑数据是否存在、格式是否适用。例如没有正文是 Missing；JSON 访问器遇到明确非 JSON 类型是 Missing；表单访问器遇到 multipart 是 Unavailable。不能把“不适用”一律解释为正文损坏。

适用正文仍在实时接收时为 NotReady；存档中的未结束正文为 Unavailable。响应仍在接收不妨碍使用已经完整的请求正文。重复 JSON 属性被拒绝，不默默选最后一个。元数据被裁剪的记录，对 original 访问采取保守的 Unavailable。

## 5. 运算符与三值条件

| 优先级，从高到低 | 写法 | 操作数 |
| --- | --- | --- |
| 括号、访问、函数 | `(...)`、`first(...)` | 按相应规则 |
| 否定 | `not x` | Boolean |
| 比较与成员判断 | `== != < <= > >= in` | 相等要求同类型标量；大小比较仅 Number；in 为标量与兼容列表 |
| 与 | `and` | Boolean |
| 或 | `or` | Boolean |

比较不能连续书写：`1 < amount < 100` 无效，应写 `amount > 1 and amount < 100`。否定一个比较时写 `not (statusCode >= 400)`；`not` 优先于比较。重复否定需要括号，例如 `not (not isTest)`。

String 相等和字符串函数使用 Ordinal 规则，不自动忽略大小写；Number 1 和 Number 1.00 相等，但 Number 1 与 String `"1"` 不允许直接比较。日期字符串不能用大小运算符比较。

`in` 示例：`original.method in ["GET", "POST"]`。左值确定且右侧为空列表时为 False；有确定匹配时为 True；没有匹配但列表含未知成员时为 Unknown。列表字面量不能含 null，但实际提取的列表可能含 Null。

这里的 Unknown 是“布尔结论暂时或永久无法确定”，实际值对象保留 Missing、NotReady 等具体状态，并不是新增的字段状态：

| A | B | A and B | A or B |
| --- | --- | --- | --- |
| True | True | True | True |
| True | False | False | True |
| True | Unknown | Unknown | True |
| False | True | False | True |
| False | False | False | False |
| False | Unknown | False | Unknown |
| Unknown | True | Unknown | True |
| Unknown | False | False | Unknown |
| Unknown | Unknown | Unknown | Unknown |

`not True = False`，`not False = True`，`not Unknown = Unknown`。`and` 在左侧 False 时短路，`or` 在左侧 True 时短路；左侧 Unknown 时仍可能通过右侧确定结论。`where` 只接纳 True。

## 6. 函数全集

下表中的 T 表示相同的标量类型，参数 x 可以是表达式或字段引用。

| 函数 | 输入与输出 | 关键语义 |
| --- | --- | --- |
| `first(list)` | List 或运行时为数组的 Json → 首项 | 空列表为 Missing；保留第一项 Null，不寻找下一项 |
| `all(list)` | List 或运行时为数组的 Json → 原列表 | 保留顺序和多值；不展开组、不拼接 |
| `firstNonEmpty(a, b, ...)` | 2～8 个相同 T 或 null → T | 跳过 Missing、Null、空字符串；遇其他状态立即返回；全跳过后为 Missing |
| `if(condition, yes, no)` | Boolean 与兼容分支 → 分支类型 | 两分支同类型，或其中一支为 null；只求值选中的分支，Unknown 条件不选分支 |
| `trim(x)` | String → String | 只去掉首尾空格、tab、CR、LF |
| `lower(x)` | String → String | 使用固定文化规则转小写 |
| `upper(x)` | String → String | 使用固定文化规则转大写 |
| `contains(text, part)` | 两个 String → Boolean | 区分大小写的包含判断 |
| `startsWith(text, prefix)` | 两个 String → Boolean | 区分大小写的前缀判断 |
| `endsWith(text, suffix)` | 两个 String → Boolean | 区分大小写的后缀判断 |
| `toString(x)` | String / Number / Boolean / Json 标量 → String | Number 用无文化差异的规范文本，Boolean 为小写 true/false |
| `toNumber(x)` | Number / 合法数字 String / 相应 Json 标量 → Number | 不支持 Boolean；文本遵守定点数规则 |
| `toBool(x)` | Boolean / 精确 String "true" 或 "false" / 相应 Json 标量 → Boolean | 不支持数值 0/1、"TRUE" 或首尾带空白的文本 |
| `isMissing(x)` | 任意值 → Boolean | Missing 为 True，Value/Null 为 False；其他未知状态仍未知 |
| `isNull(x)` | 任意值 → Boolean | Null 为 True，Value/Missing 为 False；其他未知状态仍未知 |
| `tuple(a, b, ...)` | 2～4 个标量 → Tuple | 保留每个分量的类型、状态、来源；不嵌套、不接受列表 |

除表中专门处理状态的函数外，非 Value 状态通常向外传播。静态已知不合法的 `toBool(1)`、`toNumber(true)` 在编译时失败；Json 的真实类型要读数据后才知道，不合约时得到 Invalid。

短路只影响运行时求值。编译器仍检查 if 的两个分支，以及 and/or 两侧；把未知函数放进 `if(false, ...)` 的未选分支，也不能绕过静态检查。

常用字段片段如下，使用前按需放入 view 之前；它们不是一份完整程序：

```text
field caller = first(original.headers["x-business-user"]);
field normalized = lower(trim(caller));
field amount = toNumber(first(original.query["amount"]));
field firstUser = toString(first(original.json("$.users")));
field tags = all(original.query["tag"]);
field routeUser = if(startsWith(original.path, "/actors/"), original.pathSegment(1), null);
```

`normalized` 是否适合作为身份，需要业务先确认大小写是否等价。`tags` 可以作为显示列，但 List 不能直接分组。

## 7. 分组、列、窗口与界面选项

### 分组键

以下为子句片段，分别使用：

```text
group by caller;
group by tenant, caller;
group by tuple(tenant, caller);
group by original.host, original.method;
```

键必须为确定标量。任一分量非 Value 时，请求进入“身份未确定”，保留诸如 `Value / NotReady` 的逐分量解释。Tuple 按分量展开；不能写 `group by tuple(tenant, caller), original.host;`。

键相等依据类型和值，不依据显示文字。Number 1 与 1.00 同组；字符串中的分隔符不造成组合键碰撞。不同 RecordId 不做业务去重；快照中重复 RecordId 会使整轮查询失败，避免重复计数或猜测哪个修订正确。

### 时间窗口

| 写法 | 含义 |
| --- | --- |
| `last(30s)` | 最近 30 秒 |
| `last(15m)` | 最近 15 分钟 |
| `last(2h)` | 最近 2 小时 |
| `last(7d)` | 最近 7 天，最大窗口 |
| `range("2026-09-30T12:00:00Z", "2026-09-30T12:30:00Z")` | 指定 UTC 区间 |
| `range("2026-09-30T20:00:00+08:00", "2026-09-30T20:30:00+08:00")` | 与上一行相同的时间范围 |

`last` 的数量为正整数，单位只能是小写 `s/m/h/d`，不能写 `1.5h`、`1h30m` 或 `30min`。range 起点必须早于终点，跨度最多七天，字符串必须含 `Z` 或明确的 `±HH:mm` 时区。推荐使用表中的 ISO 8601 格式。

窗口为 `[from, to)`，依据 StartedUtc。实时锚点取冻结时刻；存档锚点取最大 StartedUtc 加一个 tick（100ns），不使用存档复制完成时间。空存档不展示虚构的业务时间。

### 界面选项不等于 DSL 语句

“窗口覆盖”优先于 within；“显示字段”非空时覆盖 columns。来源选择还会在 where 之前筛选记录，默认仅 capture。因此把 where 改成 true，也不会绕过界面来源选择。

“包含窗口前上下文”和“按请求数排序”是视图选项，DSL 中没有对应语句。保存视图会保存这些选项及所引用的规则修订。预览解释 DSL 字段、条件和候选键，不等同于已经套用全部视图选项的正式统计。

## 8. 编译诊断与运行问题

| 诊断码 | 含义 | 修正方向 |
| --- | --- | --- |
| DSL001 | 不支持的语言版本 | 使用 `dsl 1;` |
| DSL002 | 无法识别的字符、字符串/数字格式错误 | 检查双引号、JSON 转义、数值格式 |
| DSL003 | 语法结构错误 | 检查固定子句顺序、括号、分号、比较连写 |
| DSL004 | 未知字段/函数或使用保留字段名 | 核对拼写、声明与函数白名单 |
| DSL005 | 类型、参数数量或键/列约束错误 | 先 first，再显式转换；检查 tuple、columns 等约束 |
| DSL006 | 字段重复声明 | 保留唯一字段名 |
| DSL007 | 字段依赖循环 | 删除自引用或间接依赖环 |
| DSL008 | 不支持的 original 访问器或 JSON 路径 | 使用当前访问器与有限路径语法 |
| DSL009 | 源码、标记、深度、字段数或编译时间超限 | 缩小规则和表达式规模 |
| DSL010 | 时间窗口不合法 | 检查正时长、单位、时区、起止顺序与七天上限 |

诊断提供行列和可选中位置，编辑器按 UTF-16 偏移定位。先修复第一处结构错误，后续诊断可能随之消失；编译器只在部分字段声明边界恢复，不承诺一次列出所有错误。

Invalid / Unavailable 等是**具体记录的运行状态**，不必然产生 DSL00x 编译诊断。“验证通过”只说明规则合法，不保证每条记录都能提取出确定值。

## 9. 资源限制

限制让 DSL 可预测地使用捕获工作台的资源；它们是硬边界，不是任意设备上的性能承诺。

| 范围 | 当前限制 |
| --- | --- |
| 编译 | UTF-8 源码 64 KiB、8192 标记、表达式深度 32、32 字段、最多 50 条诊断、500ms |
| 字段值 | 字符串 4096 个 UTF-16 代码单元（部分 emoji 占两个）、列表 128 项、来源轨迹最多 8 项 |
| 结构 | tuple 最多 4 分量、显示列最多 8 个；JSON 路径 16 段、深度 32、标记 16384 |
| 正文 | 每记录编码字节和解码字节各最多 256 KiB；整轮各最多 32 MiB |
| 查询 | 最多 10000 条保留记录、5000 组、每记录 10000 次求值、整轮 5000000 次、10 秒 |
| 预览 | 每次最多 20 条、2 秒；共享字段管线和资源配额 |
| 内存与并发 | 共享内容预算 32 MiB；分析预算 64 MiB 包含候选和保留结果；每页最多一个操作、容器最多两个操作 |
| 规则库 | 最多 50 个视图、200 个修订、文件 8 MiB；删除视图不会自动清理修订 |

预览起始序号为 1～10000；超出当前保留条数得到 0 条样本。每次预览重新冻结记录，因此不能用连续序号当作长期稳定的请求标识。

## 10. 当前明确不支持的语法

没有算术、正则、自定义函数、循环、变量赋值、通用脚本、文件/网络访问、通用 JSONPath、列表展开分组，也没有 SQL 的 SELECT、JOIN、ORDER BY 或任意聚合表达式。接口数、请求数、错误数等由工作台按固定统计口径计算。

M2 不提供路由、改写、发送、重放或响应正文访问。后续规划中的词汇在当前版本可能被保留或直接拒绝；要确认功能状态，请查 [M2 当前说明](../reference/network-traffic-m2-dsl.md)，不要仅依据早期产品设想。
