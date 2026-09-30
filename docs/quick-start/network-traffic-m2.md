# M2 · DSL 分组快速开始

先按 [M1 快速开始](network-traffic-m1.md)接入本机显式代理，或打开已有原生存档。完整语义见 [M2 当前说明](../reference/network-traffic-m2-dsl.md)。

第一次编写规则，建议从 [DSL 入门教程](../dsl/01-tutorial.md)开始；需要查函数和类型时看 [语法手册](../dsl/02-syntax-reference.md)，架构与取舍见 [设计意图](../dsl/03-design-rationale.md)。

## 分析当前记录

1. 在主 Document 顶部点“分组”。代理停止按钮继续位于顶栏。
2. 点“规则”，复制 [caller.dsl](../examples/m2/caller.dsl)。字段名和位置可修改；“字段辅助 / 视图选项”可生成片段，预览后插入草稿。
3. 点“验证”。点诊断可跳到实际编辑位置；输入停止200ms后也会检查语法。验证不读取捕获。
4. 点“样本预览”，查看最多20条的字段类型、状态、来源和分组键。在“字段辅助 / 视图选项”修改“预览样本起始序号”（例如21），再次预览可换一组样本。预览默认遮罩，不自动应用。
5. 返回分组。窗口覆盖留空即使用 DSL 的 last(30m)，也可输入 `last(5m)` 或含时区的 `range("2026-09-30T12:00:00Z","2026-09-30T12:30:00Z")`。
6. 点“应用/刷新”。选择组查看时间线；窄屏可“返回组列表”。“未确定”和“上下文”有独立入口。
7. 选请求后点“打开请求详情”，按身份跳回请求页。原请求已淘汰或源已改变会提示原因。

字段需要查看实际值时勾“显示本次原值”。新结果或换源会恢复遮罩。分组依据真实类型化值，显示相同遮罩的组不会合并。

## 保存后重算

在规则面板填写视图名，点“保存视图”；“另存视图”生成另一个 ViewId。保存不是应用。另一个页面仍使用其已应用修订；“规则库已更新”提示出现时可手动加载。

关闭重开后点“加载视图库”，选视图并“载入草稿”，检查窗口、来源和显示列，再点击应用。打开历史存档后按同样步骤重算；历史 last 按最后请求定位，不按今天时间，也不发网络请求。

“删除选中视图”只删除对应定义；修订和当前已应用结果保留。规则库冲突时加载后另存或重试；坏文件不会被自动覆盖为空库。

## 两种分组演示

先使用 caller.dsl，再使用 [tenant-caller.dsl](../examples/m2/tenant-caller.dsl)。对同一组合成数据，前者组请求数为5、1、1；后者为3、1、2、1，两者均有2条未确定身份。详见 [12条合成记录](../examples/m2/README.md)。这说明“组数”取决于键，不能解释为人数。

未知原因是 NotReady 时，请等捕获完整后手动刷新；历史未结束的记录为 Unavailable，重算不会把缺失字节补回来。无效规则或总预算失败时，工作台继续显示上次成功结果及原修订。

## 本地开发检查

在仓库根目录执行：

```powershell
pwsh -NoProfile -File tools/verify-development.ps1 -Mode M2
```

这会锁定依赖恢复、Debug 警告视为错误构建、运行普通测试、同轮 M0/M1/UX 资源阶段及 M2 专项资源，校验207项映射和全部证据。包含多项十分钟持续测量，请预留时间。结果在 `TestResults/NetworkTrafficM2/<runId>/verification.json`。

只改 Markdown 时可运行 `-Mode Docs`；它不代表业务测试通过。普通快速回归：

```powershell
dotnet test tests/NetWorkTrafficPlugin.Tests -c Debug --filter 'Category!=ResourceProbe&Category!=M1ResourceProbe&Category!=NativeUiProbe&Category!=M2ResourceProbe'
```

默认模式仍为 M0。该流程不使用 AIFLOW、Windows CI、发布覆盖率、Release、打包或部署门禁。真实 Host、真实客户端与人工验收状态单独登记在实施记录。
