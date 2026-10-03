namespace NetWorkTrafficPlugin.Application.Dsl;

/// <summary>v1 分析入口保持原有文法和诊断；共享前端不意味着接受有发送意图的 v2 程序。</summary>
public static class DslCompiler
{
    public static Compilation Compile(string source, CancellationToken token = default)
    {
        try { return new DslSyntax.Parser(source, token).Compile(); }
        catch (DslSyntax.SyntaxFailure ex) { return new(null, [ex.Diagnostic]); }
    }
}
