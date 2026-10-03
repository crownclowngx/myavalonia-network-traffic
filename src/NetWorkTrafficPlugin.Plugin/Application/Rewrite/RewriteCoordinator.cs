namespace NetWorkTrafficPlugin.Application.Rewrite;

public enum RewriteRunMode { Stopped, Capture, RewriteHttp11 }
public sealed record ActiveRewriteSnapshot(long ProxyGeneration, long ActivationGeneration, RewriteRunMode Mode,
    RewriteRevision? Revision, RewriteProgram? Program)
{
    public bool Enabled => Mode == RewriteRunMode.RewriteHttp11 && Program is not null;
}

/// <summary>
/// 一个插件容器一份的快照协调器，不拥有代理、页面或正文。编译在锁外完成，锁内只校验代次与交换引用。
/// 新请求在同一把锁中登记并取快照，使接纳与启停具有明确顺序；在途请求不再回读 Current。
/// </summary>
public sealed class RewriteCoordinator(IRewriteLibraryStore store)
{
    private readonly object _sync = new();
    private ActiveRewriteSnapshot _current = new(0, 0, RewriteRunMode.Stopped, null, null);
    public ActiveRewriteSnapshot Current { get { lock (_sync) return _current; } }

    public ActiveRewriteSnapshot StartRun(long proxyGeneration, RewriteRunMode mode, bool mappingEnabled = false)
    {
        lock (_sync)
        {
            if (_current.Mode != RewriteRunMode.Stopped || proxyGeneration <= _current.ProxyGeneration || mode == RewriteRunMode.Stopped)
                throw new InvalidOperationException("代理必须先停止，且新运行代次必须递增。");
            if (mode == RewriteRunMode.RewriteHttp11 && mappingEnabled) throw new InvalidOperationException("旧 Mapping 与 M3 改写不能同时启用。");
            // 每次启动均为 Disabled，加载磁盘配置本身永远不会开启流量改写。
            return _current = new(proxyGeneration, checked(_current.ActivationGeneration + 1), mode, null, null);
        }
    }

    public async Task<ActiveRewriteSnapshot> ActivateAsync(Guid revisionId, long expectedProxyGeneration,
        long expectedActivationGeneration, CancellationToken token)
    {
        var library = await store.LoadAsync(token);
        var revision = library.Revisions.SingleOrDefault(r => r.RevisionId == revisionId) ?? throw new InvalidOperationException("只能启用已保存的改写修订。");
        var compilation = RewriteCompiler.Compile(revision.SourceText, token);
        if (!compilation.Success) throw new InvalidDataException("已保存的改写修订无法编译。");
        lock (_sync)
        {
            token.ThrowIfCancellationRequested(); CheckGeneration(expectedProxyGeneration, expectedActivationGeneration);
            if (_current.Mode != RewriteRunMode.RewriteHttp11) throw new InvalidOperationException("请先以 HTTP/1.1 改写模式启动代理。");
            return _current = new(_current.ProxyGeneration, checked(_current.ActivationGeneration + 1), _current.Mode, revision, compilation.Program);
        }
    }

    public ActiveRewriteSnapshot Disable(long expectedProxyGeneration, long expectedActivationGeneration)
    {
        lock (_sync)
        {
            CheckGeneration(expectedProxyGeneration, expectedActivationGeneration);
            return _current = _current with { ActivationGeneration = checked(_current.ActivationGeneration + 1), Revision = null, Program = null };
        }
    }

    public ActiveRewriteSnapshot StopRun(long expectedProxyGeneration)
    {
        lock (_sync)
        {
            if (_current.ProxyGeneration != expectedProxyGeneration) throw new RewriteGenerationConflictException("停止请求属于旧代理运行。");
            return _current = new(_current.ProxyGeneration, checked(_current.ActivationGeneration + 1), RewriteRunMode.Stopped, null, null);
        }
    }

    /// <summary>登记回调必须是短同步操作，不能做 I/O 或 await；返回值和活动登记使用同一个快照引用。</summary>
    public ActiveRewriteSnapshot Admit(long expectedProxyGeneration, Action<ActiveRewriteSnapshot> register)
    {
        lock (_sync)
        {
            if (_current.Mode == RewriteRunMode.Stopped || _current.ProxyGeneration != expectedProxyGeneration)
                throw new RewriteGenerationConflictException("代理已停止或运行代次已变化。");
            var admitted = _current; register(admitted); return admitted;
        }
    }
    private void CheckGeneration(long proxy, long activation)
    {
        if (_current.ProxyGeneration != proxy || _current.ActivationGeneration != activation)
            throw new RewriteGenerationConflictException("代理或启用代次已变化，请刷新状态后重试。");
    }
}
