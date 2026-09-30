using System.Text;
using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Features.Analysis;
using NetWorkTrafficPlugin.Features.Capture;
using NetWorkTrafficPlugin.Features.Rules;
using NetWorkTrafficPlugin.Infrastructure.Analysis;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Content;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Rules;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

public sealed class M2IntegrationTests
{
    [Fact] public async Task I01RealCaptureSixLocations()
    {
        var root=TemporaryArtifacts.Create();
        try
        {
            using var ca=ProxyCertificates.CreateRoot(); await using var origin=await ControlledOrigin.StartAsync("m2",ca,https:false);
            await using var proxy=new ProxyFixture(); await proxy.StartAsync(new(Capture:CaptureMode.BoundedBody,RecordDirectory:root,Limits:CaptureLimits.M1()));
            using var client=proxy.Client();
            for(var i=0;i<6;i++)
            {
                using var request=new HttpRequestMessage(i is 3 or 4?HttpMethod.Post:HttpMethod.Get,new Uri(origin.Address,i==0?"/echo?user=u1":i==5?"/actors/u1":"/echo"));
                if(i==1) request.Headers.Add("x-business-user","u1");
                if(i==2) request.Headers.Add("Cookie","business_user=u1");
                if(i==3) request.Content=new StringContent("""{"identity":{"user":"u1"}}""",Encoding.UTF8,"application/json");
                if(i==4) request.Content=new StringContent("user=u1",Encoding.UTF8,"application/x-www-form-urlencoded");
                using var response=await client.SendAsync(request); response.EnsureSuccessStatusCode(); await response.Content.ReadAsByteArrayAsync();
            }
            var session=proxy.Runtime.Capture!; await proxy.Runtime.StopAsync();
            var resources=new AnalysisResources(); var memory=new ContentMemoryBudget();
            var program=M2Fixture.Compile(RuleEditorModel.DefaultText);
            using(var result=await new AnalysisQueryEngine(resources).RunAsync(new CaptureAnalysisSource(session,memory),program,Guid.NewGuid(),AnalysisQueryEngine.Hash(program.Source),new(),default))
            {
                Assert.Equal(6,Assert.Single(result.Groups).Count); Assert.Empty(result.Unresolved);
                Assert.Equal("u1",result.Groups[0].Key[0].Data);
                M2Fixture.Evidence("lifetime","I01",new { receipts=origin.Receipts.Count,grouped=result.Coverage.GroupedRecords,bodyBytes=result.EncodedBytes,recordIds=result.Groups[0].Requests.Select(r=>r.RecordId) });
            }
            Assert.Equal(0,resources.Memory.Reserved); Assert.Equal(0,memory.Reserved);
        }
        finally {TemporaryArtifacts.Delete(root);}
    }
    [Fact] public async Task I02FreezeWatermarkAndLease()
    {
        await using var f=new M1SessionFixture(); var original=await f.AddAsync("first"u8.ToArray(),finish:false,uri:"https://example.test/?user=u");
        var adapter=new CaptureAnalysisSource(f.Session,new()); using(var frozen=await adapter.FreezeAsync(default))
        {
            var record=Assert.Single(frozen.Records); var committed=record.ResponseBody.CommittedBytes; Assert.True(frozen.Anchor<=DateTimeOffset.UtcNow);
            f.Recorder.Observe(original.Response,"later"u8); f.Session.ResponseBytes(original.Id,"later"u8);
            Assert.Equal(committed,record.ResponseBody.CommittedBytes); Assert.Single(frozen.Records);
            await f.AddAsync([],uri:"https://example.test/?user=next"); Assert.Single(frozen.Records);
            M2Fixture.Evidence("lifetime","I02",new {committed,frozenCount=frozen.Records.Count,currentCount=f.Session.Snapshot().Records.Length,frozen.Anchor});
        }
        await f.StopAsync();
        // 快照释放后才允许生产会话清理；清理成功证明租约没有遗留。
        f.Session.DeleteStoppedSession();
    }
    [Fact] public async Task I03ArchiveRecomputePreservesBytes()
    {
        var output=TemporaryArtifacts.Create();
        try
        {
            await using var f=new M1SessionFixture(); await f.AddAsync("body"u8.ToArray(),uri:"https://example.test/?user=u"); await f.StopAsync();
            await using var archives=new FileCaptureArchiveStore(); var saved=await archives.SaveAsync(f.Session,Path.Combine(output,"saved"),default);
            var hashes=Directory.GetFiles(saved.Directory).ToDictionary(p=>Path.GetFileName(p)!,p=>AnalysisQueryEngine.Hash(Convert.ToBase64String(File.ReadAllBytes(p))));
            var source=await archives.OpenAsync(saved.Directory,default); var program=M2Fixture.Compile(M2Fixture.Program());
            using var result=await new AnalysisQueryEngine(new()).RunAsync(new CaptureAnalysisSource(source,new()),program,Guid.NewGuid(),AnalysisQueryEngine.Hash(program.Source),new(),default);
            Assert.Equal(1,result.Coverage.GroupedRecords); Assert.True(source.IsArchive); Assert.False(source.Snapshot().IsRunning);
            foreach(var entry in hashes) Assert.Equal(entry.Value,AnalysisQueryEngine.Hash(Convert.ToBase64String(File.ReadAllBytes(Path.Combine(saved.Directory,entry.Key!)))));
            M2Fixture.Evidence("lifetime","I03",new {records=result.Coverage.GroupedRecords,files=hashes.Count,unchanged=true,running=source.Snapshot().IsRunning});
        }
        finally {TemporaryArtifacts.Delete(output);}
    }
    [Fact] public async Task I04IndependentPagesAndSharedSlots()
    {
        var directory=TemporaryArtifacts.Create();
        try
        {
            var f=new M2Fixture(); f.Add(); f.Add("https://example.test/?user=other");
            var resources=new AnalysisResources(); using(var a=resources.Enter()) using(var b=resources.Enter()) Assert.Throws<AnalysisLimitException>(()=>resources.Enter());
            var store=new FileRuleLibraryStore(directory);
            using var first=new AnalysisWorkspace(new(resources),store); using var second=new AnalysisWorkspace(new(resources),store);
            first.SetSource(f.Source()); second.SetSource(f.Source()); first.Editor.Text=M2Fixture.Program(); second.Editor.Text=M2Fixture.Program(group:"original.host");
            await Task.WhenAll(first.ApplyAsync(),second.ApplyAsync()); Assert.Equal(2,first.Groups.Count); Assert.Single(second.Groups);
            await first.SaveAsync(); second.ObserveLibraryGeneration(); Assert.True(second.HasLibraryUpdates);
            first.ClearCommand.Execute(null); Assert.Single(second.Groups); Assert.Equal(0,resources.Active);
            M2Fixture.Evidence("lifetime","I04",new {secondGroups=second.Groups.Count,firstGroups=first.Groups.Count,active=resources.Active,maxConcurrent=2});
        }
        finally {TemporaryArtifacts.Delete(directory);}
    }
    [Fact] public async Task I05LateSourceCannotCommit()
    {
        var directory=TemporaryArtifacts.Create();
        try
        {
            var f=new M2Fixture(); f.Add(); var slow=new DelayedSource(f.Source()); var resources=new AnalysisResources();
            using var page=new AnalysisWorkspace(new(resources),new FileRuleLibraryStore(directory)); page.Editor.Text=M2Fixture.Program(); page.SetSource(slow);
            var old=page.ApplyAsync(); await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var current=f.Source(); page.SetSource(current); var next=page.ApplyAsync(); slow.Release.TrySetResult(); await Task.WhenAll(old,next);
            Assert.Equal(current.InstanceId,page.Result!.SourceId); Assert.NotEqual(slow.InstanceId,page.Result.SourceId);
            page.Dispose(); Assert.Equal(0,resources.Memory.Reserved); Assert.Equal(0,resources.Active); Assert.Equal(0,f.Leases);
            M2Fixture.Evidence("lifetime","I05",new {oldApplied=0,active=resources.Active,reservedAfter=resources.Memory.Reserved,leases=f.Leases});
        }
        finally {TemporaryArtifacts.Delete(directory);}
    }
    [Fact] public async Task I06DetailsUseIdentityAndClosingDoesNotStopCapture()
    {
        await using var f=new M1SessionFixture(); var row=await f.AddAsync("detail"u8.ToArray(),uri:"https://example.test/?user=u");
        var store=new CaptureSessionStore(); store.Publish(f.Session); await using var archive=new FileCaptureArchiveStore();
        using var page=new CaptureWorkspace(store,archive,new BoundedImageDecoder(store.ContentBudget));
        Assert.True(await page.OpenAnalyzedRecordAsync(f.Session,row.Id)); Assert.Equal(row.Id,page.Selected!.RecordId); Assert.Equal("detail",page.BodyText);
        Assert.False(await page.OpenAnalyzedRecordAsync(f.Session,Guid.NewGuid()));
        page.Dispose(); Assert.True(f.Session.Snapshot().IsRunning); Assert.Equal(0,store.ContentBudget.Reserved);
        M2Fixture.Evidence("lifetime","I06",new {recordId=row.Id,running=f.Session.Snapshot().IsRunning,reservedAfter=store.ContentBudget.Reserved});
    }
    public sealed class DelayedSource(IAnalysisSource inner) : IAnalysisSource
    {
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid InstanceId {get;}=Guid.NewGuid();
        public bool IsArchive=>inner.IsArchive;
        public async Task<AnalysisSnapshot> FreezeAsync(CancellationToken token)
        { Entered.TrySetResult(); await Release.Task; return await inner.FreezeAsync(default); }
    }
}
