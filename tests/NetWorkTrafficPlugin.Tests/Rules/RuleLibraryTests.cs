using System.Text.Json;
using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Rules;
using NetWorkTrafficPlugin.Features.Analysis;
using NetWorkTrafficPlugin.Features.Rules;
using NetWorkTrafficPlugin.Infrastructure.Rules;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Rules;

public sealed class RuleLibraryTests
{
    private static RuleLibrary Candidate(string? text = null)
    {
        var revision = RuleRevision.Create(text ?? M2Fixture.Program());
        return RuleLibrary.Empty.Save(revision, new(Guid.NewGuid(), "业务视图", revision.RevisionId, new()));
    }
    [Fact] public async Task S01DraftPreviewAndAppliedAreIndependent()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var f = new M2Fixture(); f.Add(); var resources = new AnalysisResources();
            using var page = new AnalysisWorkspace(new(resources),new FileRuleLibraryStore(root)); page.SetSource(f.Source()); page.Editor.Text = M2Fixture.Program();
            await page.ApplyAsync(); var old = page.Result; Assert.NotNull(old);
            page.Editor.Text = "dsl 2;"; await page.ApplyAsync(); Assert.Same(old,page.Result);
            page.Editor.Text = M2Fixture.Program(filter:"false"); await page.PreviewAsync(); Assert.Same(old,page.Result);
            Assert.Contains("条件 False",page.PreviewText); Assert.Contains("String/Value = ••••",page.PreviewText); Assert.DoesNotContain("u1",page.PreviewText);
            // 无自定义显示列时仍解释内置键；tuple逐项保留确定/未知状态，禁止只输出一个整体遮罩。
            var engine=new AnalysisQueryEngine(resources);
            var tuple=M2Fixture.Compile(M2Fixture.Program(fields:"",group:"tuple(original.host, first(original.query[\"missing\"]))",columns:""));
            var preview=await engine.PreviewAsync(f.Source(),tuple,default);
            Assert.Contains("String/Value = example.test",preview); Assert.Contains("String/Missing",preview);
            Assert.Contains("样本 0 / 保留 1",await engine.PreviewAsync(f.Source(),tuple,default,1));
            var namedTuple=M2Fixture.Compile(M2Fixture.Program("field user=tuple(original.host,\"private-constant\");"));
            var maskedPreview=await engine.PreviewAsync(f.Source(),namedTuple,default);
            Assert.DoesNotContain("private-constant",maskedPreview); Assert.DoesNotContain("example.test",maskedPreview);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>engine.PreviewAsync(f.Source(),tuple,default,-1));
            Assert.Equal(0,f.Leases);
            await page.ApplyAsync(); Assert.Empty(page.Result!.Groups); Assert.NotSame(old,page.Result);
            M2Fixture.Evidence("rules-storage","S01",new { retainedOnFailure=true,previewApplied=false,finalGroups=page.Result.Groups.Count });
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Fact] public void S02FormNeverReformatsDraft()
    {
        var editor=new RuleEditorModel {Text="// 中文说明; 分号仍是注释\n"+M2Fixture.Program(),FieldName="business",Argument="id"};
        foreach(var location in editor.Locations)
        {
            editor.Location=location; editor.Argument=location=="json"?"$.id":location=="pathSegment"?"1":"id";
            Assert.Contains("field business",editor.GenerateFragment());
        }
        editor.Location="query"; editor.Argument="id"; editor.InsertFragment(); Assert.StartsWith("// 中文说明",editor.Text); Assert.Contains("field business",editor.Text);
        Assert.True(editor.Validate().Success);
        editor.FieldName="source"; Assert.Throws<ArgumentException>(()=>editor.GenerateFragment());
        M2Fixture.Evidence("rules-storage","S02",new { locations=editor.Locations.Length,commentsPreserved=true });
    }
    [Fact] public async Task S03ImmutableRevisionHash()
    {
        var root=TemporaryArtifacts.Create();
        try
        {
            var store=new FileRuleLibraryStore(root); var initial=Candidate(); var saved=await store.CommitAsync(initial,0,default);
            var loaded=await store.LoadAsync(default); Assert.Equal(saved.Revisions[0],loaded.Revisions[0]);
            var altered=loaded with {Revisions=[loaded.Revisions[0] with {SourceText=M2Fixture.Program(filter:"false")}]};
            await Assert.ThrowsAsync<InvalidDataException>(()=>store.CommitAsync(altered,1,default));
            Assert.Equal(initial.Revisions[0].SourceHash,AnalysisQueryEngine.Hash(loaded.Revisions[0].SourceText));
            M2Fixture.Evidence("rules-storage","S03",new { loaded.Generation,revision=loaded.Revisions[0].RevisionId,hash=loaded.Revisions[0].SourceHash });
        }
        finally {TemporaryArtifacts.Delete(root);}
    }
    [Fact] public async Task S04SavedViewRestoresPinnedRevision()
    {
        var root=TemporaryArtifacts.Create();
        try
        {
            var initial=Candidate(); initial=initial with {Views=[initial.Views[0] with {Options=new(new(TimeSpan.FromMinutes(5)),["capture"],true,true,["user"])}]};
            await new FileRuleLibraryStore(root).CommitAsync(initial,0,default);
            var f=new M2Fixture(); f.Add(minute:28);
            using var page=new AnalysisWorkspace(new(new()),new FileRuleLibraryStore(root)); page.SetSource(f.Source()); await page.ReloadAsync();
            page.SelectedView=Assert.Single(page.SavedViews); page.LoadViewCommand.Execute(null); Assert.Null(page.Result);
            Assert.True(page.IncludeContext); Assert.Equal("last(300s)",page.WindowText); await page.ApplyAsync();
            Assert.Equal(initial.Revisions[0].RevisionId,page.AppliedRevision!.RevisionId); Assert.Equal(1,page.Result!.Coverage.GroupedRecords);
            var json=await File.ReadAllTextAsync(Path.Combine(root,"rules.json")); Assert.DoesNotContain("synthetic.bin",json); Assert.DoesNotContain("RecordId",json);
            M2Fixture.Evidence("rules-storage","S04",new { pinned=page.AppliedRevision.RevisionId,windowSeconds=300,restored=true });
        }
        finally {TemporaryArtifacts.Delete(root);}
    }
    [Fact] public async Task S05AtomicFailureAndCancellation()
    {
        var root=TemporaryArtifacts.Create();
        try
        {
            var store=new FileRuleLibraryStore(root); var saved=await store.CommitAsync(Candidate(),0,default); var before=await File.ReadAllBytesAsync(Path.Combine(root,"rules.json"));
            var next=saved.Save(RuleRevision.Create(M2Fixture.Program(filter:"false")),saved.Views[0]);
            var failing=new FileRuleLibraryStore(root,stage=> {if(stage=="written") throw new IOException("合成磁盘失败");});
            await Assert.ThrowsAsync<IOException>(()=>failing.CommitAsync(next,1,default)); Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"rules.json")));
            using var cancel=new CancellationTokenSource(); var cancelling=new FileRuleLibraryStore(root,stage=> {if(stage=="written") cancel.Cancel();});
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelling.CommitAsync(next,1,cancel.Token));
            Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"rules.json"))); Assert.Empty(Directory.GetFiles(root,"*.tmp"));
            using var late=new CancellationTokenSource(); var committed=new FileRuleLibraryStore(root,stage=> {if(stage=="committed") late.Cancel();});
            var result=await committed.CommitAsync(next,1,late.Token); Assert.Equal(2,result.Generation);
            M2Fixture.Evidence("rules-storage","S05",new { unchangedOnFailure=true,lateCancellationCommitted=result.Generation,temporaryFiles=Directory.GetFiles(root,"*.tmp").Length });
        }
        finally {TemporaryArtifacts.Delete(root);}
    }
    [Fact] public async Task S06ConcurrentGenerationConflict()
    {
        var root=TemporaryArtifacts.Create();
        try
        {
            var a=new FileRuleLibraryStore(root); var b=new FileRuleLibraryStore(root); var first=await a.CommitAsync(Candidate(),0,default);
            await Assert.ThrowsAsync<RuleLibraryConflictException>(()=>b.CommitAsync(Candidate(),0,default));
            using(var held=new FileStream(Path.Combine(root,"rules.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                await Assert.ThrowsAsync<RuleLibraryConflictException>(()=>b.CommitAsync(first,1,default));
            Assert.Equal(first.Generation,(await b.LoadAsync(default)).Generation);
            M2Fixture.Evidence("rules-storage","S06",new { conflicts=2,generation=first.Generation });
        }
        finally {TemporaryArtifacts.Delete(root);}
    }
    [Fact] public async Task S07CorruptLibraryNeverBecomesEmpty()
    {
        var root=TemporaryArtifacts.Create();
        try
        {
            var store=new FileRuleLibraryStore(root); Assert.Empty((await store.LoadAsync(default)).Views); Directory.CreateDirectory(root);
            var path=Path.Combine(root,"rules.json");
            foreach(var json in new[] {"{broken",JsonSerializer.Serialize(Candidate() with {SchemaVersion=99}),JsonSerializer.Serialize(Candidate() with {Revisions=[]})})
            {
                await File.WriteAllTextAsync(path,json); await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default)); Assert.Equal(json,await File.ReadAllTextAsync(path));
            }
            M2Fixture.Evidence("rules-storage","S07",new { rejected=3,originalPreserved=true });
        }
        finally {TemporaryArtifacts.Delete(root);}
    }
    [Fact] public async Task S08LimitsAndExplicitDeletion()
    {
        var root=TemporaryArtifacts.Create();
        try
        {
            var initial=Candidate(); var revision=initial.Revisions[0]; var fifty=initial with {Views=Enumerable.Range(0,50).Select(i=>new SavedView(Guid.NewGuid(),"同名",revision.RevisionId,new())).ToArray()};
            RuleLibraryValidation.Validate(fifty,default);
            Assert.Throws<InvalidDataException>(()=>RuleLibraryValidation.Validate(fifty with {Views=[..fifty.Views,new(Guid.NewGuid(),"超额",revision.RevisionId,new())]},default));
            Assert.Throws<InvalidDataException>(()=>RuleLibraryValidation.Validate(initial with {Revisions=Enumerable.Range(0,201).Select(_=>RuleRevision.Create(M2Fixture.Program())).ToArray()},default));
            var store=new FileRuleLibraryStore(root); var saved=await store.CommitAsync(fifty,0,default);
            var deleted=await store.CommitAsync(saved with {Views=saved.Views.Skip(1).ToArray()},1,default);
            Assert.Equal(49,deleted.Views.Length); Assert.Single(deleted.Revisions);
            await File.WriteAllBytesAsync(Path.Combine(root,"rules.json"),new byte[RuleLibraryValidation.MaximumBytes+1]);
            await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
            M2Fixture.Evidence("rules-storage","S08",new { views=deleted.Views.Length,revisions=deleted.Revisions.Length,oversizedRejected=true });
        }
        finally {TemporaryArtifacts.Delete(root);}
    }
}
