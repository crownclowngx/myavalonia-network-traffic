using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;
using static NetWorkTrafficPlugin.Tests.Analysis.GroupingTests;

namespace NetWorkTrafficPlugin.Tests.Analysis;

public sealed class TimelineTests
{
    [Fact] public async Task T01HalfOpenWindow()
    {
        var f=new M2Fixture(); var first=f.Add(minute:0); f.Add(minute:30); f.Add(minute:31); f.Add(minute:-1);
        using var result=await Run(f,M2Fixture.Program(),new()); Assert.Equal(first.RecordId, Assert.Single(Assert.Single(result.Groups).Requests).RecordId); Assert.Equal(3,result.Coverage.OutsideWindow);
        Assert.False(DslCompiler.Compile(M2Fixture.Program(window:"range(\"2026-09-30T12:00:00\",\"2026-09-30T12:30:00\")")).Success);
        var offset=M2Fixture.Compile(M2Fixture.Program(window:"range(\"2026-09-30T20:00:00+08:00\",\"2026-09-30T20:30:00+08:00\")"));
        Assert.Equal(M2Fixture.Anchor.AddMinutes(-30),offset.Window.From);
        M2Fixture.Evidence("timeline","T01",new { result.From,result.To,result.Coverage.OutsideWindow,included=first.RecordId });
    }
    [Fact] public async Task T02StableStartOrder()
    {
        var f=new M2Fixture(); f.Add(minute:2); f.Add(minute:1,sequence:3); f.Add(minute:1,sequence:2); f.Add(minute:1,sequence:2);
        f.Records[0]=f.Records[0] with {CompletedUtc=M2Fixture.Anchor.AddMinutes(-28)};
        using var result=await Run(f,M2Fixture.Program(),new());
        var expected=f.Records.OrderBy(r=>r.StartedUtc).ThenBy(r=>r.Sequence).ThenBy(r=>r.RecordId).Select(r=>r.RecordId).ToArray();
        Assert.Equal(expected,result.Groups[0].Requests.Select(r=>r.RecordId));
        M2Fixture.Evidence("timeline","T02",new { expected,actual=result.Groups[0].Requests.Select(r=>r.RecordId) });
    }
    [Fact] public async Task T03ObservedDurationAndStates()
    {
        var f=new M2Fixture(); f.Add(); f.Add(); f.Records[0]=f.Records[0] with { ElapsedMilliseconds=120000,CompletedUtc=M2Fixture.Anchor.AddDays(-1),ResponseBody=f.Records[0].ResponseBody with {Transport=TransportState.Receiving} };
        f.Records[1]=f.Records[1] with {ElapsedMilliseconds=0,ResponseBody=f.Records[1].ResponseBody with {Transport=TransportState.Cancelled}};
        using var result=await Run(f,M2Fixture.Program(),new()); Assert.Equal(120000,result.Groups[0].Requests[0].ElapsedMilliseconds); Assert.Equal(TransportState.Receiving,result.Groups[0].Requests[0].Transport); Assert.Equal(0,result.Groups[0].Requests[1].ElapsedMilliseconds);
        M2Fixture.Evidence("timeline","T03",new { rows=result.Groups[0].Requests.Select(r=>new {r.RecordId,r.StartedUtc,r.ElapsedMilliseconds,state=r.Transport.ToString()}) });
    }
    [Fact] public async Task T04ContextNeverInflatesGroups()
    {
        var f=new M2Fixture(); f.Add(minute:0); f.Add(minute:-1); f.Add(minute:-1);
        f.Records[1]=f.Records[1] with {ElapsedMilliseconds=60000}; f.Records[2]=f.Records[2] with {ElapsedMilliseconds=60001};
        using var result=await Run(f,M2Fixture.Program(),new(),new(IncludeContext:true));
        Assert.Single(result.Context); Assert.Equal(f.Records[2].RecordId,result.Context[0].RecordId); Assert.Equal(1,result.Coverage.GroupedRecords);
        M2Fixture.Evidence("timeline","T04",new { context=result.Context.Count,grouped=result.Coverage.GroupedRecords,outside=result.Coverage.OutsideWindow });
    }
    [Fact] public async Task T05SnapshotDoesNotMoveWithNewData()
    {
        var f=new M2Fixture(); f.Add(); var resources=new AnalysisResources(); var source=f.Source();
        using var first=await Run(f,M2Fixture.Program(),resources,source:source); f.Add();
        Assert.Equal(1,first.Coverage.Retained);
        using var second=await Run(f,M2Fixture.Program(),resources,source:source); Assert.Equal(2,second.Coverage.Retained); Assert.Equal(first.From,second.From);
        M2Fixture.Evidence("timeline","T05",new { firstCount=first.Coverage.Retained,secondCount=second.Coverage.Retained,first.From,second.To });
    }
    [Fact] public async Task T06ArchiveAnchorIsRepeatable()
    {
        var f=new M2Fixture {IsArchive=true}; var last=f.Add(minute:20);
        using var first=await Run(f,M2Fixture.Program(),new()); using var second=await Run(f,M2Fixture.Program(),new());
        Assert.Equal(last.StartedUtc.AddTicks(1),first.To); Assert.Equal(first.To,second.To); Assert.Equal(1,first.Coverage.GroupedRecords);
        f.Records.Clear(); using var empty=await Run(f,M2Fixture.Program(),new()); Assert.Empty(empty.Groups);
        f.Add(); f.Records[0]=f.Records[0] with {StartedUtc=DateTimeOffset.MaxValue};
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>Run(f,M2Fixture.Program(),new())); Assert.Equal(0,f.Leases);
        M2Fixture.Evidence("timeline","T06",new { first.To,reopenedTo=second.To,empty=empty.Groups.Count,leases=f.Leases });
    }
}
