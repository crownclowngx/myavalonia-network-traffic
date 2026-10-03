# 第四章 · DSL v2 改写基础与当前边界

更新：2026-10-03。v2编译器和纯规划器已实现，**尚未接入代理或编辑页面**。现有“分组”页面仍只接受 `dsl 1;`。本章不是启用M3的操作说明；生产接入受 [V6-02能力缺口](../maintenance/network-traffic-v6-body-capability.md)阻断。

## 完整示例

可编译样例：[route-and-json.dsl](../examples/m3/route-and-json.dsl)、[original-only.dsl](../examples/m3/original-only.dsl)。反例：[invalid-conflict.dsl](../examples/m3/invalid-conflict.dsl)。它们由真实编译器测试，首个样例还验证纯规划的目标、Host、Query和JSON结果，不发送网络请求。样例与源码一起进入开发门禁sourceHash。

```text
dsl 2;
field caller = firstNonEmpty(first(original.headers["X-Caller"]), "normal");
rewrite "测试调用者" {
    priority 10;
    for host "api.test";
    when caller == "tester";
    route to "https://stage.test:8443";
    set path "/v2/orders";
    set query["debug"] = "1";
    remove query["legacy"];
    set header["X-Environment"] = "stage";
}
```

字段表达式、显式多值处理、三值逻辑和转换函数继承[第二章](02-syntax-reference.md)。v1与v2使用独立顶层入口，共享词法、表达式解析、类型绑定和求值；v2不构造假的分析视图，不接受 `view`、`statusCode` 或 `effective.*`。

## 规则与动作

每条规则依次写名称、唯一优先级0～9999、精确主机、布尔when、至少一个动作。规则按优先级升序执行，不按文件排列决定路由。主机规范化为IDN ASCII、忽略大小写并去一个末尾点；不匹配子域后缀。

| 动作 | 当前纯规划语义 |
| --- | --- |
| `route to "https://stage.test:8443";` | 同协议替换origin；首个命中路由有效，后续路由Shadowed，其他动作仍参与 |
| `set path "/orders/%E4%B8%AD";` | 转义绝对路径，拒绝空格、双斜线起始、控制字符、Query/fragment、解码后完整点段 |
| `set query["x"] = "中 +";` | 替换所有同名键，在首个位置插入；空格编码为%20、加号为%2B，其他项原文本/顺序保留 |
| `remove query["x"];` | 删除全部同名键，区分大小写，缺失NoOp；实际改Query时损坏转义阻断 |
| `set header["X"] = "value";` | 不区分头名大小写，替换所有同名行，保留其他多值头顺序 |
| `remove header["X"];` | 删除全部同名行，缺失NoOp；保护协议控制头和Connection声明的逐跳字段 |
| `set json("$.context.name") = "中";` | 只写标量/null；父对象必须存在，对象可增末端属性，数组仅替换已有索引 |
| `remove json("$.token");` | 仅删对象属性，缺失末端NoOp；不删数组元素、根节点或自动创建父对象 |

所有条件和动作表达式读取同一原始输入，不读取前一动作结果。跨命中规则重复写同一Query/Header/JSON目标会整次阻断，JSON父子路径也冲突；优先级不代表最后覆盖。Unknown、非法动态值、超限、格式错误和冲突不返回半份候选。取消向调用者传播，必须等真实工作退出再释放租约。

JSON只接受identity编码、UTF-8的application/json或application/*+json，拒绝重复属性、BOM、损坏数字和不完整数据；数字沿用v1定点规则，不接受指数表示，小数位及去前导零后数字总位数各不超过28，无double中转。修改后重算UTF-8长度并移除旧实体摘要。全部JSON动作均NoOp时保留原正文与摘要，不为排版重新序列化。

## 预算与所有权

生产常量与 [m3-profile.json](../../tools/m3-profile.json)有一致性测试。语言预算继承v1；改写32规则、每条16动作、合计128动作，原始/候选头各64 KiB/256行，最终URI8192字符，正文输入/输出各256 KiB，4个正文槽各预留共享内容额度4 MiB。字段单值继续受v1的4096字符限制。候选每步校验，不能先累积成巨大头/URI再最终检查。

只有访问正文的字段或命中JSON动作时才读正文；纯头路径、作用域外及可短路条件不读。多个字段共享一次正文读取，同一字段只求值一次。字段源持有租约，纯规划返回不等于网络库已不再引用内存，实际接入必须证明完整生命周期。

profile中的轨迹16 KiB等仍含后续集成要求；当前简单规则轨迹和位置差异还没有双快照存档、全局保留预算、动作来源及UI展示，不构成完整M3追查交付。真实取消、Continue、物理预读要求未被Fake测试替代。

## 修订与共享快照

`rewrites.json` 独立于分析的 `rules.json`，最多200个不可变修订、50个命名配置、8 MiB。读写验证格式、源码摘要、编译结果及父修订链。磁盘提交点前取消保留原库，提交点后返回已提交事实；保存不启用。

`RewriteCoordinator` 核心比较代理代次与启用代次后交换快照。接纳和快照读取在同一同步区间；停用只影响之后接纳的请求，旧引用仍固定旧修订。普通捕获模式不能启用，Mapping与改写模式互斥，停止重启默认Disabled。此协调器尚未注入实际代理/UI，测试证明的是核心状态语义。

## 本地验证

在插件仓库执行：

```powershell
dotnet test NetWorkTrafficPlugin.slnx -c Debug --filter "FullyQualifiedName~Rewrite|FullyQualifiedName~M3BodyCapabilityTests|FullyQualifiedName~DslCompilerTests|FullyQualifiedName~FieldExtractionTests"
pwsh -NoProfile -File tools/verify-development.ps1 -Mode M2
```

第二个命令完整回归既有M2及前置阶段，普通测试中同时包含本期基础测试和缺口探针。`-Mode M3` 尚不存在；72个新增场景、累计279场景、M3资源/UI及12组门禁自测仍未完成。测试通过与M3完成不能互换，实际报告见[本期实施记录](../archive/2026-10-03-v6-m3-foundation.md)。
