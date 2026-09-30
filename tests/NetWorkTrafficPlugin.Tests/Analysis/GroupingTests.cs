using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Features.Rules;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Analysis;

public sealed class GroupingTests
{
    public static (M2Fixture Fixture, string Text) Sample()
    {
        var f = new M2Fixture();
        f.Add("https://example.test/api?tenant=t1&user=u1", minute: 0);
        f.Records[0] = f.Records[0] with { StartedUtc = M2Fixture.Anchor.AddMinutes(-30).AddSeconds(-1), ResponseBody = f.Records[0].ResponseBody with { Transport = TransportState.Receiving } };
        f.Add("https://example.test/api?tenant=t1&user=u1", minute: 0);
        f.Add("https://example.test/api?tenant=t1", headers: [new("x-business-user","u1")], minute: 1);
        f.Add("https://example.test/api?tenant=t1", headers: [new("Cookie","business_user=u2")], minute: 1);
        f.Add("https://example.test/api?tenant=t2", """{"identity":{"user":"u1"}}""", "application/json", minute: 2);
        f.Add("https://example.test/api?tenant=t2", "user=u1", "application/x-www-form-urlencoded", minute: 3);
        f.Add("https://example.test/actors/u3?tenant=t2", minute: 4);
        f.Add("https://example.test/api?tenant=t1&user=u1", minute: 5);
        f.Add("https://example.test/api?tenant=t1", minute: 6);
        f.Add("https://example.test/api?tenant=t1", "{\"identity\":", "application/json", minute: 7, bodyState: ValueState.NotReady);
        f.Add("https://example.test/api?tenant=t1&user=u1", minute: 8);
        f.Records[10] = f.Records[10] with { Source = (TrafficSource)99 };
        f.Add("https://example.test/api?tenant=t1&user=u1", minute: 30);
        var text = RuleEditorModel.DefaultText.Replace("field caller", "field tenant = first(original.query[\"tenant\"]);\nfield caller")
            .Replace("group by caller;", "group by tuple(tenant,caller);").Replace("columns caller;", "columns tenant,caller;");
        return (f, text);
    }
    public static Task<AnalysisResult> Run(M2Fixture f, string text, AnalysisResources resources, AnalysisOptions? options = null, IAnalysisSource? source = null)
    {
        var program = M2Fixture.Compile(text);
        return new AnalysisQueryEngine(resources).RunAsync(source ?? f.Source(), program, Guid.NewGuid(), AnalysisQueryEngine.Hash(text), options ?? new(), default);
    }
    [Fact] public async Task Q01TwoGroupingsHaveExactMembers()
    {
        var (f, text) = Sample(); var resources = new AnalysisResources();
        using var a = await Run(f, text, resources, new(IncludeContext: true));
        Assert.Equal(new[] { 3, 1, 2, 1 }, a.Groups.Select(g => g.Count)); Assert.Equal(7, a.Coverage.GroupedRecords); Assert.Equal(2, a.Unresolved.Count);
        Assert.Equal(new[] { f.Records[1].RecordId, f.Records[2].RecordId, f.Records[7].RecordId }, a.Groups[0].Requests.Select(r => r.RecordId));
        using var b = await Run(f, text.Replace("group by tuple(tenant,caller);", "group by caller;"), resources);
        Assert.Equal(new[] { 5, 1, 1 }, b.Groups.Select(g => g.Count)); Assert.Single(a.Context);
        Assert.Equal(new[] { "Value / Missing", "Value / NotReady" }, a.Unresolved.Select(r => r.Unresolved));
        M2Fixture.Evidence("grouping", "Q01", new { groups = a.Groups.Select(g => g.Count), alternate = b.Groups.Select(g => g.Count), a.Coverage, members = a.Groups.Select(g => g.Requests.Select(r => r.RecordId)), expectedMembers = new[] { new[]{1,2,7},new[]{3},new[]{4,5},new[]{6} }.Select(indices => indices.Select(i=>f.Records[i].RecordId)), a.AnalysisId, a.SourceId, a.SessionId, a.SnapshotRevision, a.RuleRevisionId, a.RuleHash, a.From, a.To, unresolved = a.Unresolved.Select(r => r.Unresolved), context = a.Context.Count });
    }
    [Fact] public async Task Q02TypedKeysDoNotCollide()
    {
        var a = AnalysisQueryEngine.Canonical([FieldValue.Text("a|b"), FieldValue.Text("c")]);
        var b = AnalysisQueryEngine.Canonical([FieldValue.Text("a"), FieldValue.Text("b|c")]); Assert.NotEqual(a,b);
        Assert.NotEqual(AnalysisQueryEngine.Canonical([FieldValue.Text("1")]), AnalysisQueryEngine.Canonical([FieldValue.Number(1)]));
        Assert.Equal(AnalysisQueryEngine.Canonical([FieldValue.Number(1)]), AnalysisQueryEngine.Canonical([FieldValue.Number(1.00m)]));
        var (f, text) = Sample(); var resources = new AnalysisResources();
        using var tuple = await Run(f, text, resources); using var columns = await Run(f, text.Replace("tuple(tenant,caller)", "tenant,caller"), resources);
        Assert.Equal(tuple.Groups.Select(g => g.Count), columns.Groups.Select(g => g.Count));
        M2Fixture.Evidence("grouping", "Q02", new { collisions = 0, equivalent = tuple.Groups.Count });
    }
    [Fact] public async Task Q03CoverageConservesAllRecords()
    {
        var f = new M2Fixture(); f.Add("https://example.test/?user=a&flag=true"); f.Add("https://example.test/?user=b&flag=false"); f.Add("https://example.test/?user=c");
        using var result = await Run(f, M2Fixture.Program(filter:"toBool(first(original.query[\"flag\"]))"), new());
        Assert.Equal(1,result.Coverage.FilterTrue); Assert.Equal(1,result.Coverage.FilterFalse); Assert.Equal(1,result.Coverage.FilterUnknown);
        Assert.Equal(result.Coverage.Retained, result.Coverage.SourceExcluded + result.Coverage.OutsideWindow + result.Coverage.Evaluated);
        M2Fixture.Evidence("grouping", "Q03", result.Coverage);
    }
    [Fact] public async Task Q04DuplicateBusinessRequestsRemain()
    {
        var f = new M2Fixture(); f.Add(); f.Add();
        using var result = await Run(f, M2Fixture.Program(), new()); Assert.Equal(2, Assert.Single(result.Groups).Count);
        f.Records.Add(f.Records[0] with { Revision = 2 });
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(f, M2Fixture.Program(), new()));
        M2Fixture.Evidence("grouping", "Q04", new { businessCount = 2, duplicateIdentityRejected = true });
    }
    [Fact] public async Task Q05UnknownSourcesExcludedByDefault()
    {
        var f = new M2Fixture(); f.Add(); f.Add("https://example.test/?user=changed-token"); f.Add(); f.Records[2] = f.Records[2] with { Source = (TrafficSource)99 };
        using var defaults = await Run(f,M2Fixture.Program(),new()); Assert.Equal(2,defaults.Groups.Count); Assert.Equal(1, defaults.Coverage.SourceExcluded);
        using var explicitSource = await Run(f,M2Fixture.Program(),new(),new(Sources:["unknown"])); Assert.Equal(1,explicitSource.Coverage.GroupedRecords);
        M2Fixture.Evidence("grouping", "Q05", new { groups = defaults.Groups.Count, excluded = defaults.Coverage.SourceExcluded, explicitlyIncluded = explicitSource.Coverage.GroupedRecords });
    }
    [Fact] public async Task Q06ColumnsAndStableSorting()
    {
        var f = new M2Fixture(); f.Add("https://example.test/?user=a"); f.Add("https://example.test/?user=b"); f.Add("https://example.test/?user=b");
        using var result = await Run(f,M2Fixture.Program("field user=first(original.query[\"user\"]); field capital=upper(user);"),new(),new(SortByCount:true,Columns:["capital"]));
        Assert.Equal(2,result.Groups[0].Count); Assert.Equal("B", result.Groups[0].Requests[0].Fields["capital"].Data); Assert.Equal("••••",result.Groups[0].Requests[0].Fields["capital"].Display());
        await Assert.ThrowsAsync<ArgumentException>(() => Run(f,M2Fixture.Program(),new(),new(Columns:["missing"])));
        M2Fixture.Evidence("grouping", "Q06", new { firstCount=result.Groups[0].Count, columns=result.Groups[0].Requests[0].Fields.Keys });
    }
    [Fact] public async Task Q07InterfaceAndErrorDefinitions()
    {
        var f=new M2Fixture(); f.Add("https://example.test/a?user=u1"); f.Add("https://example.test/a?user=u1&x=2"); f.Add("https://example.test/b?user=u1");
        f.Records[0]=f.Records[0] with { StatusCode=500, ResponseBody=f.Records[0].ResponseBody with {Transport=TransportState.Failed} };
        f.Records[1]=f.Records[1] with { ResponseBody=f.Records[1].ResponseBody with {Transport=TransportState.Receiving} };
        using var result=await Run(f,M2Fixture.Program(),new()); var group=Assert.Single(result.Groups);
        Assert.Equal(2,group.Interfaces); Assert.Equal(1,group.Errors); Assert.Equal(1,group.Receiving);
        M2Fixture.Evidence("grouping","Q07",new { group.Count,group.Interfaces,group.Errors,group.Receiving });
    }
    [Fact] public async Task Q08LimitsRejectWholeCandidate()
    {
        var f=new M2Fixture(); for(var i=0;i<5001;i++) f.Add($"https://example.test/?user={i}");
        var resources=new AnalysisResources(); await Assert.ThrowsAsync<AnalysisLimitException>(()=>Run(f,M2Fixture.Program(),resources));
        Assert.Equal(0,resources.Memory.Reserved); Assert.Equal(0,f.Leases);
        while(f.Records.Count<10001) f.Add();
        await Assert.ThrowsAsync<AnalysisLimitException>(()=>Run(f,M2Fixture.Program(),resources));
        M2Fixture.Evidence("grouping","Q08",new { records=f.Records.Count, reservedAfter=resources.Memory.Reserved, leases=f.Leases });
    }
}
