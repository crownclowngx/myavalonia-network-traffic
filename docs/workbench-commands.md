# Workbench Command 开发说明

Plugin SDK `3.4.1` 允许插件把少量高价值的用户意图声明为 Workbench Command，使同一语义动作可由
Host 菜单、快捷键或后续 Command Palette 投影。Command 不是 Avalonia `ICommand` 的替代品；按钮点击、
表单编辑、拖放和只对单个控件有意义的局部交互，继续使用插件自己的命令或业务用例即可。

## 模板示例采用的最小设计

模板只声明一条 `ApplyWorkbenchMessage`：

```text
PluginIds 中的 CommandId / CommandPlacementId
    → Module 冻结 CommandDescriptor 与 Tools 菜单贡献
    → Host 根据活动 Document 路由
    → 当前 MainDocument 实例实现 IWorkbenchDocumentCommandTarget
    → CanExecute / ExecuteAsync / CommandStateChanged
```

这条链遵守四个边界：

1. 注册阶段只保存稳定身份、展示元数据和目标 `DocumentTypeId`，不保存实例、回调、Provider 或 `ICommand`；
2. 状态与执行属于当前 `MainDocument` 实例，同类型的两个文档可以拥有不同状态；
3. `ExecuteAsync` 返回真实可等待的 `ValueTask` 并首先观察取消，不能用 `async void` 隐藏未完成任务；
4. 状态变化事件只携带受影响的 `CommandId`，Host 负责 UI 线程切换、去重和退订。

## 为什么默认不注册快捷键

快捷键是整个工作台共享的稀缺资源。模板自动占用快捷键会让大量新插件产生无意义冲突，也会把示例选择
误当成平台规范。因此模板只把命令放到 `WorkbenchMenuLocations.ToolsShared`，并明确不调用
`AddKeyBindingContribution`。真实插件只有在命令语义稳定、冲突政策清楚且用户确实需要高频访问时，才应增加
`KeyBindingContributionDescriptor`。

## 适配既有局部命令

已有 Document 可以在 Target 内委托同一个业务用例或可等待命令，但公共身份始终是 `CommandId`。如果现有
入口只有 `ICommand.Execute` 并启动 `async void`，应先提取可等待业务方法或使用明确的异步命令 API；不能让
Host Executor 在真实工作尚未完成时误报成功。

不要在 Target 中解析 Host 或插件根容器。需要的依赖应由 Document Scope 在构造时显式注入；Target 只表达
当前实例能够做什么，不承担 Catalog、菜单排序、快捷键冲突或 Dock 生命周期。

## 测试清单

- 已知与未知 `CommandId` 的 `CanExecute`；
- 当前实例执行后不影响同类型的另一个实例；
- `CommandStateChanged` 只通知真实变化的命令；
- 取消令牌在修改业务状态前生效；
- 重复执行、未知身份和业务失败有明确异常；
- Module 只为自己拥有的 Document 注册命令；
- 默认模板没有快捷键贡献；
- 最终 ZIP 由真实 Host 在独立 ALC 中加载，SDK 仍来自 Default ALC。

Standalone 只承载同一份 `MainDocument` 和 View，不模拟 Host 的 Catalog、活动 Document 路由或菜单投影。
这些行为必须用正式 ZIP 和真实 Host 验收。
