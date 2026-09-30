# DSL v1 可核对样例

- [caller.dsl](caller.dsl)：六位置回退，按 caller 分组。
- [tenant-caller.dsl](tenant-caller.dsl)：相同字段提取，按 tenant+caller 分组。
- [invalid-type.dsl](invalid-type.dsl)：数值转布尔的静态类型错误，应给 DSL005。

L01/L02 直接读取这些文件验证；DSL 文件纳入开发 sourceHash。样例只含合成业务值，不连接真实服务。

以下为 GroupingTests.Sample 的固定12条输入。分析锚点为 2026-09-30T12:30:00Z，窗口 `[12:00,12:30)`。R编号用于说明，运行时 RecordId 单独生成。

| 记录 | 开始 | tenant | caller 位置和值 | 主统计 |
| --- | --- | --- | --- | --- |
| R0 | 11:59:59 | t1 | Query u1，响应仍接收 | 窗口前上下文 |
| R1 | 12:00 | t1 | Query u1 | t1/u1 |
| R2 | 12:01 | t1 | Header u1 | t1/u1 |
| R3 | 12:01 | t1 | Cookie u2 | t1/u2 |
| R4 | 12:02 | t2 | JSON u1 | t2/u1 |
| R5 | 12:03 | t2 | 表单 u1 | t2/u1 |
| R6 | 12:04 | t2 | /actors/u3 路径段 | t2/u3 |
| R7 | 12:05 | t1 | Query u1，相同业务内容不同 RecordId | t1/u1 |
| R8 | 12:06 | t1 | 缺失 | Value / Missing |
| R9 | 12:07 | t1 | JSON请求仍在接收 | Value / NotReady |
| R10 | 12:08 | t1 | Query u1，合成未知来源枚举 | 默认来源排除 |
| R11 | 12:30 | t1 | Query u1 | 位于右端点，排除 |

生产当前只有 Captured，因此 R10 用未知枚举验证默认排除；不伪装成已经实现的 replay 请求。按组合键的成员依次为 R1/R2/R7、R3、R4/R5、R6；只按 caller 的请求数为5、1、1。正常入组7条、身份未确定2条、来源排除1条、窗口外2条，保留总数12；上下文 R0 不额外加进主统计。

无效样例仅用于演示诊断，不能应用。字段缺失和 Invalid/Unavailable 不等价，不把部分正文当完整数据。
