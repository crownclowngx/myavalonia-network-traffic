# Workflow Action Provider 与 Consumer 接入

当前模板精确引用 Plugin SDK `3.4.1`；其中的 Workflow Action 契约保持兼容。独立的 Workflow SDK `3.4.1`
提供 Schema、引用路径、
保守可赋值与 Catalog revision。通用模板仍只生成一个普通 Document，不会替开发者选择
Provider 或 Consumer 角色。这样创建出来的插件保持最小职责，也不会因为示例代码意外取得跨插件调用能力。

## 角色和所有权

- **Provider** 声明动作并实现 scoped `IWorkflowActionHandler`。每次调用由 Host 在动作所有者的私有
  Provider 中创建和释放独立 Scope。
- **Consumer** 只调用 `UseWorkflowActionGateway()` 请求 caller-bound Gateway。CallerId、RunId、
  InvocationId 和授权结果全部由 Host 生成，插件不能提交或伪造。
- 首版明确禁止同一插件同时成为 Provider 和 Consumer，避免递归调用和不清晰的授权所有权。需要端到端
  验证时应创建两个独立插件项目，并通过真实 ZIP 与候选 Host 组合。

## Provider 最小示例

下面的示例只演示契约边界。业务 DTO、服务和错误处理应留在插件内部；输入输出跨 ALC 时只使用 SDK、
BCL 和 `JsonElement`。

```csharp
using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;

public sealed class EchoHandler : IWorkflowActionHandler
{
    public ValueTask<JsonElement> InvokeAsync(
        JsonElement arguments,
        WorkflowActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new
        {
            echoed = arguments.GetProperty("value").GetString(),
            caller = context.CallerId.Value,
        }));
    }
}

// 放入当前插件 Module.Configure；ActionId 必须属于当前 PluginId 的 .workflow. 命名空间。
using var input = JsonDocument.Parse(
    """{"type":"object","properties":{"value":{"type":"string","maxLength":64}},"required":["value"],"additionalProperties":false}""");
using var output = JsonDocument.Parse(
    """{"type":"object","properties":{"echoed":{"type":"string","maxLength":64},"caller":{"type":"string","maxLength":128}},"required":["echoed","caller"],"additionalProperties":false}""");
registration.AddWorkflowAction<EchoHandler>(new WorkflowActionDescriptor(
    new WorkflowActionId("myavalonia.plugin.example.workflow.echo"),
    "回显",
    "返回输入文本和可信调用者身份。",
    input.RootElement,
    output.RootElement,
    WorkflowActionRiskFlags.None,
    WorkflowActionConfirmationPolicy.Never));
```

## Consumer 最小示例

Consumer 的 Module 只声明需要 Gateway；真实调用代码通过构造注入取得 `IWorkflowActionGateway`，每次工作流
运行创建一个 Run，并在结束时异步释放。请求中没有 CallerId 或授权字段。

```csharp
public void Configure(IPluginRegistration registration)
{
    registration.UseWorkflowActionGateway();
}

public sealed class ActionClient(IWorkflowActionGateway gateway)
{
    public async Task<WorkflowActionInvocationResult> EchoAsync(
        string value,
        CancellationToken cancellationToken)
    {
        await using var run = gateway.CreateRun();
        return await run.InvokeAsync(
            new WorkflowActionInvocationRequest(
                new WorkflowActionId("myavalonia.plugin.provider.workflow.echo"),
                JsonSerializer.SerializeToElement(new { value })),
            progress: null,
            cancellationToken);
    }
}
```

Standalone 不应复制 Host 的授权、目录或跨 ALC 实现。需要预览 Consumer UI 时，注入一个范围受控的 Fake
Gateway；真实所有者路由、调用 Scope、关闭排空和诊断脱敏只能在候选 Host 中验收。
