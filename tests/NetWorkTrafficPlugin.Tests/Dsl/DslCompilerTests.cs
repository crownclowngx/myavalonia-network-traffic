using System.Globalization;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Dsl;

public sealed class DslCompilerTests
{
    [Fact] public void L01ValidPrograms()
    {
        string[] expressions = ["first(original.query[\"user\"])", "first(original.headers[\"X-User\"])", "first(original.cookies[\"user\"])",
            "toString(original.json(\"$.identity.user\"))", "first(original.form[\"user\"])", "original.pathSegment(1)"];
        foreach (var expr in expressions) { var p = M2Fixture.Compile(M2Fixture.Program("field user = " + expr + ";")); Assert.Equal(DslType.String, p.FieldTypes["user"]); }
        var combination = M2Fixture.Compile(M2Fixture.Program("field a = b; field b = \"业务\";", group: "tuple(a,b)", columns: "a,b", window: "range(\"2026-09-30T12:00:00Z\",\"2026-09-30T12:30:00Z\")"));
        Assert.Equal(2, combination.Fields.Count);
        foreach (var name in new[] { "caller.dsl", "tenant-caller.dsl" })
            M2Fixture.Compile(File.ReadAllText(Path.Combine(ProbeProcess.Repository, "docs/examples/m2", name)));
        M2Fixture.Evidence("language", "L01", new { extractors = expressions.Length, fields = combination.Fields.Count });
    }
    [Fact] public void L02InvalidLexicalInput()
    {
        string[] invalid = ["dsl 2;", "dsl 1; field a = \"broken", M2Fixture.Program("field user = 1.2.3;"), M2Fixture.Program("field user = \"\\q\";"), M2Fixture.Program("field user = @;")];
        foreach (var text in invalid) Assert.False(DslCompiler.Compile(text).Success);
        Assert.False(DslCompiler.Compile(File.ReadAllText(Path.Combine(ProbeProcess.Repository, "docs/examples/m2/invalid-type.dsl"))).Success);
        M2Fixture.Evidence("language", "L02", new { rejected = invalid.Length });
    }
    [Fact] public async Task L03PrecedenceAndReservedNames()
    {
        var f = new M2Fixture(); var r = f.Add(); Assert.True((await f.Value("true or false and false", r)).Truth);
        Assert.False((await f.Value("(true or false) and false", r)).Truth);
        Assert.False(DslCompiler.Compile(M2Fixture.Program(filter: "1 < 2 < 3")).Success);
        Assert.False(DslCompiler.Compile(M2Fixture.Program("field source = \"x\";")).Success);
        M2Fixture.Evidence("language", "L03", new { precedence = true, chainRejected = true });
    }
    [Fact] public void L04TypesAndKeys()
    {
        string[] invalid = [M2Fixture.Program(filter: "\"1\" == 1"), M2Fixture.Program(group: "all(original.query[\"user\"])") , M2Fixture.Program(group: "original.json(\"$.user\")"), M2Fixture.Program(group: "tuple(tuple(user,user),user)"), M2Fixture.Program(filter: "null == null")];
        foreach (var text in invalid) Assert.Contains(DslCompiler.Compile(text).Diagnostics, d => d.Code == "DSL005");
        M2Fixture.Evidence("language", "L04", new { rejected = invalid.Length });
    }
    [Fact] public void L05FunctionWhitelist()
    {
        string[] invalid = ["eval(\"1+1\")", "readFile(\"x\")", "contains(\"a\")", "if(true, 1)", "tuple(1)", "firstNonEmpty(1,\"x\")", "response.json(\"$\")"];
        foreach (var expression in invalid) Assert.False(DslCompiler.Compile(M2Fixture.Program("field user = " + expression + ";")).Success);
        M2Fixture.Evidence("language", "L05", new { rejected = invalid.Length });
    }
    [Fact] public void L06DependencyCycles()
    {
        var p = M2Fixture.Compile(M2Fixture.Program("field user = x; field x = y; field y = \"u\";")); Assert.Equal(3, p.FieldTypes.Count);
        foreach (var fields in new[] { "field user = user;", "field user = a; field a = user;", "field user = \"u\"; field unused = missing;" }) Assert.False(DslCompiler.Compile(M2Fixture.Program(fields)).Success);
        Assert.Contains(DslCompiler.Compile(M2Fixture.Program("field user=1; field user=2;")).Diagnostics, d => d.Code == "DSL006");
        M2Fixture.Evidence("language", "L06", new { forwardFields = p.FieldTypes.Count, cyclesRejected = 2 });
    }
    [Fact] public void L07CompilerLimits()
    {
        foreach (var text in new[] { new string(' ', 65537), M2Fixture.Program("field user=" + new string('(', 40) + "1" + new string(')', 40) + ";"),
            M2Fixture.Program(string.Join(' ', Enumerable.Range(0, 33).Select(i => $"field x{i}=1;")), group: "x0", columns: "x0") }) Assert.False(DslCompiler.Compile(text).Success);
        Assert.True(DslCompiler.Compile(M2Fixture.Program() + new string(' ', 65536 - System.Text.Encoding.UTF8.GetByteCount(M2Fixture.Program()))).Success);
        M2Fixture.Evidence("language", "L07", new { sourceLimit = DslLimits.SourceBytes, depthLimit = DslLimits.Depth, rejected = 3 });
    }
    [Fact] public async Task L08CancellationAndWorkLimits()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DslCompiler.Compile(M2Fixture.Program(), cancelled.Token));
        var work = new EvaluationBudget(default, int.MaxValue);
        Assert.Throws<AnalysisLimitException>(() => { for (var i = 0; i <= DslLimits.QueryOperations; i++) work.Step(); });
        var f = new M2Fixture(); var r = f.Add(); var p = M2Fixture.Compile(M2Fixture.Program());
        var evaluator = new FieldEvaluator(p, new ConstantSource(), new(default), default);
        // 使用已经绑定的真实where节点；生产求值只接收本程序AST，不临时注入另一棵无类型树。
        FieldValue? result = null; for (var i = 0; i <= DslLimits.RecordOperations; i++) result = await evaluator.EvaluateAsync(p.Filter);
        Assert.Equal(ValueState.BudgetExceeded, result!.State);
        Assert.Equal(DslType.Boolean,result.Type);
        M2Fixture.Evidence("language", "L08", new { queryOperations = work.Operations, recordState = result.State.ToString(), cancelled = true });
    }
    [Fact] public void L09DiagnosticSpansAndRecovery()
    {
        var input = "dsl 1;\r\n// 中文😀\r\nfield user = @;";
        var d = Assert.Single(DslCompiler.Compile(input).Diagnostics); Assert.Equal(input.IndexOf('@'), d.Span.Start); Assert.Equal(3, d.Span.Line);
        var errors = DslCompiler.Compile("dsl 1; field a = ; field b = ; view \"x\" { where true; within last(1m); group by 1; timeline by startedAt; }");
        Assert.Equal(2, errors.Diagnostics.Count);
        M2Fixture.Evidence("language", "L09", new { start = d.Span.Start, expectedStart = input.IndexOf('@'), errors = errors.Diagnostics.Count });
    }
    [Fact] public async Task L10InvariantConversions()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            var f = new M2Fixture(); var r = f.Add();
            foreach (var culture in new[] { "zh-CN", "fr-FR", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal("1.25", (await f.Value("toString(toNumber(\"1.25\"))", r)).Data);
                Assert.Equal(ValueState.Invalid, (await f.Value("toNumber(\"1,25\")", r)).State);
                Assert.Equal("I", (await f.Value("upper(\"i\")", r)).Data);
            }
            Assert.False(DslNumbers.TryParse("0.00000000000000000000000000001", out _));
        }
        finally { CultureInfo.CurrentCulture = old; }
        M2Fixture.Evidence("language", "L10", new { cultures = 3, exactDecimal = true });
    }
    private sealed class ConstantSource : IFieldSource
    { public ValueTask<FieldValue> ReadAsync(AccessExpr access, CancellationToken token) => ValueTask.FromResult(FieldValue.Text("unused")); }
}
