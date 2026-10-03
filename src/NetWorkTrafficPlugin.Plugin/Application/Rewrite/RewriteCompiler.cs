using NetWorkTrafficPlugin.Application.Dsl;

namespace NetWorkTrafficPlugin.Application.Rewrite;

public static class RewriteCompiler
{
    public static RewriteCompilation Compile(string source, CancellationToken token = default)
    {
        try { return new DslSyntax.Parser(source, token).CompileRewrite(); }
        catch (DslSyntax.SyntaxFailure ex) { return new(null, [ex.Diagnostic]); }
    }
}
