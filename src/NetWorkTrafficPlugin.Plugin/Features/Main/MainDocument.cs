using CommunityToolkit.Mvvm.ComponentModel;
using NetWorkTrafficPlugin.Constants;
using MyAvaloniaManagement.PluginSdk;
using NetWorkTrafficPlugin.Features.ProxyStatus;

namespace NetWorkTrafficPlugin.Features.Main;

public sealed partial class MainDocument :
    ObservableObject,
    IPluginDocument,
    IWorkbenchDocumentCommandTarget, IDisposable
{
    public MainDocument(ProxyPanelModel proxy) => Proxy = proxy;
    public ProxyPanelModel Proxy { get; }
    public void Dispose() => Proxy.Dispose();
    private DocumentPresentationState _presentation = new("示例文档");
    private int _workbenchMessageApplied;

    [ObservableProperty]
    private string _message = "Hello from NetWorkTrafficPlugin";

    public DocumentPresentationState Presentation => _presentation;

    public event EventHandler? PresentationChanged;

    /// <summary>当当前文档实例中示例命令的可执行状态变化时发生。</summary>
    /// <remarks>
    /// 事件只通知受影响的 <see cref="CommandId"/>。Host 负责线程切换和成对退订，
    /// Document 不持有菜单、快捷键、Catalog、Provider 或 Dock 对象。
    /// </remarks>
    public event EventHandler<WorkbenchCommandStateChangedEventArgs>? CommandStateChanged;

    public ValueTask InitializeAsync(
        DocumentActivation activation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(activation.Title))
        {
            _presentation = new DocumentPresentationState(activation.Title);
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>查询当前文档实例是否仍可执行模板示例命令。</summary>
    /// <param name="commandId">Host 正在查询的稳定命令身份。</param>
    /// <returns>命令属于本示例且尚未执行时为 <see langword="true"/>。</returns>
    public bool CanExecute(CommandId commandId)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        return commandId == PluginIds.ApplyWorkbenchMessage &&
            Volatile.Read(ref _workbenchMessageApplied) == 0;
    }

    /// <summary>在当前文档实例中执行模板示例命令。</summary>
    /// <param name="commandId">Host 已路由到当前实例的稳定命令身份。</param>
    /// <param name="cancellationToken">Document 关闭或 Host 退出时使用的协作取消令牌。</param>
    /// <returns>表示本次状态修改已经真实完成的可等待操作。</returns>
    /// <exception cref="ArgumentOutOfRangeException">命令不属于当前 Document Target。</exception>
    /// <exception cref="InvalidOperationException">同一实例中的一次性示例命令已经执行。</exception>
    public ValueTask ExecuteAsync(CommandId commandId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        cancellationToken.ThrowIfCancellationRequested();

        if (commandId != PluginIds.ApplyWorkbenchMessage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandId),
                commandId,
                "当前示例 Document Target 不拥有该工作台命令。");
        }

        if (Interlocked.Exchange(ref _workbenchMessageApplied, 1) != 0)
        {
            // Host Executor 会在执行前重新查询 CanExecute；这里仍保留实例内防御，避免插件内部调用、
            // 并发点击或错误适配绕过最终状态检查后重复修改业务状态。Interlocked 只保护本实例的
            // 一次性状态，不引入跨 Document 全局锁，也不把业务调度职责推给 Host。
            throw new InvalidOperationException("当前实例的工作台示例命令已经执行。");
        }

        Message = "Workbench Command 已在当前文档实例执行";
        CommandStateChanged?.Invoke(
            this,
            new WorkbenchCommandStateChangedEventArgs(PluginIds.ApplyWorkbenchMessage));
        return ValueTask.CompletedTask;
    }
}
