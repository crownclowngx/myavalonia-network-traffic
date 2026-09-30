# 01 · 从第一条规则到业务分组

[返回手册首页](README.md) · [查询语法](02-syntax-reference.md) · [理解设计](03-design-rationale.md)

本教程使用合成业务值 `t1`、`u1` 和 `example.test`。示例描述已捕获的数据，不要求向这些地址发送请求。使用现有捕获或原生存档即可；代理接入方式见 [M1 快速开始](../quick-start/network-traffic-m1.md)。

## 1. 用最小规则认识五个子句

假设保留记录中有下列请求，均在选定窗口内，元数据完整，来源为 `capture`：

| 请求 | 原始地址 | 期望 |
| --- | --- | --- |
| A | `https://example.test/orders?user=u1` | 调用者 u1 |
| B | `https://example.test/profile?user=u1` | 调用者 u1 |
| C | `https://example.test/orders?user=u2` | 调用者 u2 |
| D | `https://example.test/orders` | 调用者缺失 |

复制完整规则：

```dsl
dsl 1;
// Query 可能同名重复，显式取第一个值，避免偷偷合并多个身份。
field caller = first(original.query["user"]);
view "调用者入门" {
    where source == "capture";
    within last(30m);
    group by caller;
    columns caller;
    timeline by startedAt;
}
```

| 部分 | 作用 | 可以怎样改 |
| --- | --- | --- |
| `dsl 1;` | 声明语言版本 | 当前固定为 1 |
| `field caller = ...;` | 为每条请求定义字段 | 把 `"user"` 改成实际参数名 |
| `where ...;` | 过滤符合条件的请求 | 增加 Host、Method 或业务条件 |
| `within ...;` | 限制请求开始时间 | 改为 `last(5m)` 或明确起止时间 |
| `group by caller;` | 相同字段值放入同组 | 改用两个字段组成业务键 |
| `columns caller;` | 在选中请求的字段信息中显示该字段 | 增加其他已声明字段；也可省略整个子句 |
| `timeline by startedAt;` | 按请求开始时间组织时间线 | 当前固定写法 |

先清空界面的窗口覆盖和显示字段覆盖，来源保持 `capture`。点击“验证”，应无编译诊断；点击“样本预览”，观察 `caller` 的类型、状态及 `query[user]` 来源。然后“返回分组 → 应用/刷新”。

对上表四条记录，结果应是两组：u1 有 A、B 两条，u2 有 C 一条；D 出现在“未确定”。正常展示会遮罩字段原值，通过组别名区分。需要核对时，可在分组页面勾选“显示本次原值”；它不会让样本预览显示原值，新结果或切换来源后也会恢复遮罩。

## 2. 给业务身份增加回退位置

实际系统可能在旧接口使用 Query，在新接口使用 Header。可以按优先级尝试：

```dsl
dsl 1;
field caller = firstNonEmpty(
    first(original.query["user"]),
    first(original.headers["x-business-user"])
);
view "兼容两种调用者位置" {
    where source == "capture";
    within last(30m);
    group by caller;
    columns caller;
    timeline by startedAt;
}
```

`firstNonEmpty` 从左往右求值，跳过 `Missing`、`Null` 和空字符串；遇到第一个非空确定值即停止。它**不会跳过不完整、无效或超预算的数据**。

| Query 情况 | Header | caller 结果 |
| --- | --- | --- |
| `?user=u1` | u2 | u1，Query 优先 |
| 没有 user 参数 | u2 | u2，Query 为 Missing |
| `?user`，没有等号 | u2 | u2，Query 首项为 Null |
| `?user=` | u2 | u2，Query 首项为空字符串 |
| `?user=%ZZ`，百分号编码错误 | u2 | Invalid，停止回退 |
| `?user=u1&user=u2` | u3 | u1，只取 Query 第一项 |
| `?user&user=u2` | u3 | u3，first 保留首项 Null，再回退到 Header |

最后一行尤其容易误解：`first` 不负责“查找第一个非空列表项”，`firstNonEmpty` 也不会继续遍历刚才那个 Query 列表。

需要覆盖 Query、Header、Cookie、JSON、表单、Path 六种位置时，可直接使用 [caller.dsl](../examples/m2/caller.dsl)。回退顺序代表业务优先级；例如 JSON 请求正文还未收完时，JSON 分支会得到 `NotReady`，此时不会继续用 Path 猜一个身份。

## 3. 从 JSON 提取字段，并显式处理类型

假设请求正文为完整的 UTF-8 `application/json`：

```json
{
  "identity": { "user": "u1" },
  "order": { "amount": 128.50 },
  "test": false
}
```

下面只选择非测试、金额至少 100 的请求：

```dsl
dsl 1;
field caller = toString(original.json("$.identity.user"));
field amount = toNumber(original.json("$.order.amount"));
field isTest = toBool(original.json("$.test"));
view "高金额正式调用" {
    where source == "capture" and not isTest and amount >= 100;
    within last(30m);
    group by caller;
    columns caller, amount, isTest;
    timeline by startedAt;
}
```

JSON 路径的实际值要读到记录后才能确定，因此编译时类型为 `Json`。用 `toString`、`toNumber`、`toBool` 明确业务契约，再做比较、过滤或分组。

对上述正文，`amount` 为 Number 128.5，`isTest` 为 Boolean false，条件通过。若 `amount` 改成字符串 `"128.50"`，显式数值转换仍成功；改成 `"一百"` 会得到 Invalid。若缺少 `test`，条件为 Unknown，该请求计入“条件 Unknown”，不会进入业务组或“身份未确定”。

这里有两个不同的问题：**条件能否确定为 True**，以及**通过条件后能否确定分组身份**。只有第二种失败才进入“未确定”列表。

## 4. 用租户和调用者组成业务键

只按 `caller` 分组，会把不同租户中同名的 u1 放在一起。使用组合键：

```dsl
dsl 1;
field tenant = first(original.query["tenant"]);
field caller = firstNonEmpty(
    first(original.query["user"]),
    first(original.headers["x-business-user"])
);
view "租户内调用过程" {
    where source == "capture";
    within last(30m);
    group by tuple(tenant, caller);
    columns tenant, caller;
    timeline by startedAt;
}
```

此时 `(t1, u1)` 与 `(t2, u1)` 是两个组。也可以把分组子句写成 `group by tenant, caller;`，两者生成相同顺序的键分量。不要同时把一个 tuple 和其他键并列，也不要嵌套 tuple。

使用业务键前先明确其含义：`(tenant, caller)` 表示租户内调用者，`(tenant, orderId)` 表示租户内订单，它们都不自动等于“一个人”或“一次会话”。不同 RecordId 的两条相同业务请求仍计为两次请求。

不要通过拼接显示文字构造组合键。引擎会分别保留每个分量的类型和值，避免分隔符碰撞；Number 1 与 String `"1"` 也不会自动合并。当前语言本身不提供字符串加法。

## 5. 先用便宜的条件缩小分析范围

下面筛选目标 Host 中 GET/POST 的订单接口，再提取调用者：

```dsl
dsl 1;
field caller = first(original.headers["x-business-user"]);
view "订单接口调用" {
    where source == "capture"
        and original.host == "example.test"
        and original.method in ["GET", "POST"]
        and startsWith(original.path, "/orders");
    within last(15m);
    group by caller;
    columns caller;
    timeline by startedAt;
}
```

`and` 从左向右短路，前面的条件已经确定为 False 时，不再读取后面的字段。把来源、Host、Method 放在需要读取正文的条件之前，通常能减少正文读取。

`startsWith("/orders")` 是纯字符串前缀判断，也会匹配 `/orders-v2`。只要 `/orders` 及其子路径时，使用以下条件片段替换最后一项：

```text
and (original.path == "/orders" or startsWith(original.path, "/orders/"))
```

`original.path` 不包含 Query，保留 URI 转义形式。需要某个解码后的业务路径段时，用 `original.pathSegment(下标)`，下标从 0 开始。

## 6. 选择合适的时间窗口

`last(30m)` 表示相对于本轮分析锚点的最近 30 分钟；它按**请求开始时间**判定，区间左闭右开。锚点为 12:30 时，12:00 的请求属于窗口，12:30 的请求不属于窗口。

实时捕获的锚点来自冻结时刻。存档的相对窗口以最后一条请求的开始时间再加一个最小时间刻度定位，便于回看旧数据；它不会按照今天的时间把旧存档全部排除。

复盘固定故障时段时，建议使用带时区的绝对窗口：

```dsl
dsl 1;
view "固定时段按站点复盘" {
    where source == "capture";
    within range("2026-09-30T12:00:00Z", "2026-09-30T12:30:00Z");
    group by original.host;
    timeline by startedAt;
}
```

例子没有自定义字段，也没有 `columns`，仍是完整规则。请按实际记录时间修改日期；也可使用 `+08:00` 这样的明确时区偏移。单个窗口最长七天。

界面“窗口覆盖”优先于规则里的 `within`。如果结果范围与文本不一致，先检查这里。启用“包含窗口前上下文”后，可额外查看开始于窗口之前、但与窗口起点重叠且通过条件的请求；这些请求仍属于窗口外，不重复计入主统计。

## 7. 读懂一次分析的结果

正常组中的时间线按开始时间、请求序列、RecordId 稳定排序。条带的开放端表示冻结时响应仍在接收，重叠只表示时间重叠，不能据此断言请求之间有因果关系。

页面覆盖统计满足：

```text
保留记录 = 来源排除 + 窗口外 + 条件 False + 条件 Unknown + 条件 True
条件 True = 已分组请求 + 身份未确定请求
```

因此，“没有出现在业务组里”需要逐层排查，而不是一律归因于字段缺失。总捕获数、淘汰数用于解释保留范围，不能直接当作本次分组的分母。

可以用 [固定 12 条样例](../examples/m2/README.md)核对：在样例给定锚点与窗口下，`caller.dsl` 得到 3 组，请求数为 5、1、1；`tenant-caller.dsl` 得到 4 组，请求数为 3、1、2、1。两者均有 7 条正常入组、2 条身份未确定、1 条来源排除、2 条窗口外。测试夹具中的合成记录不会自动出现在你的实际捕获中。

## 8. 保存、载入和刷新

1. 在规则面板填写视图名，点击“保存视图”；需要保留另一套方案时点击“另存视图”。
2. 保存记录了规则修订和视图选项，不会自动应用，也不保存分组结果或请求正文。
3. 以后点击“加载视图库”，选择一个视图，再点击“载入草稿”，检查窗口、来源和显示列，最后点击“应用/刷新”。
4. 新捕获记录到来时，页面显示“源数据已更新”。确认需要后手动刷新，不会在阅读中自动重排当前结果。
5. “删除选中视图”删除视图定义，保留已有修订和当前成功结果。保存冲突时先重新加载，必要时另存。

规则中的字符串常量会随规则文本保存，因此不要把密钥当作筛选常量写进规则库。字段原值的界面遮罩不等于规则文本加密。

## 9. 常见问题排查

| 现象 | 应先检查 |
| --- | --- |
| `DSL005` 类型错误 | Query/Header 是否漏了 `first`；JSON 是否显式转换；Number 是否与 String 比较 |
| 预览有值，正式结果没有 | 预览只解释样本；检查来源、窗口覆盖、where、条件 Unknown 计数 |
| 样本预览一直不是想看的请求 | 修改从 1 开始的“预览样本起始序号”；每次最多 20 条且会重新冻结，序号不是固定请求身份 |
| 字段为 Missing | 完整输入中是否有这个键；大小写是否一致；访问器是否适用于该 Content-Type |
| 字段为 NotReady | 当前请求正文是否还在接收；完整后手动刷新 |
| 字段为 Unavailable | 正文是否未保存、仅有部分、存档未结束，或字符集/编码不支持 |
| 字段为 Invalid | 检查百分号编码、JSON 格式/重复属性、JSON 路径形状和转换值 |
| 正文在请求详情里能看见，DSL 仍读不到身份 | 查看器能展示部分字节，DSL 身份提取要求适用且完整的数据 |
| 很多组看起来都是 `••••` | 遮罩只影响展示；看组别名，必要时查看本次原值 |
| 验证通过却应用失败 | 验证不读取数据；检查全轮预算、并发占用、取消或来源切换提示 |
| 应用失败后仍看到结果 | 页面保留上次成功结果；核对结果修订和状态，不要把旧结果当作新规则结果 |

下一步可以用 [语法手册](02-syntax-reference.md)查函数和诊断码，或阅读 [设计意图](03-design-rationale.md)理解为什么这些错误不会被静默转换为空值。
