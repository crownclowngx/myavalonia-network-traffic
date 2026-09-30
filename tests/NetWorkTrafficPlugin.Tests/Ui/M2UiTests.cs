using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Features.Analysis;
using NetWorkTrafficPlugin.Features.Capture;
using NetWorkTrafficPlugin.Features.Main;
using NetWorkTrafficPlugin.Features.ProxyStatus;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Content;
using NetWorkTrafficPlugin.Infrastructure.Rules;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;
using static NetWorkTrafficPlugin.Tests.Ui.M1CaptureUiTests;

namespace NetWorkTrafficPlugin.Tests.Ui;

[Collection("M1UI")]
public sealed class M2UiTests
{
    private sealed class Screen : IDisposable
    {
        public string Root {get;}=TemporaryArtifacts.Create();
        public M2Fixture Fixture {get;}=new();
        public AnalysisResources Resources {get;}=new();
        public ProxySessionCoordinator Owner {get;}=new(()=>new TitaniumProxyRuntime());
        public ProxyPanelModel Proxy {get;}
        public AnalysisWorkspace Page {get;}
        public MainDocument Document {get;}
        public MainView Main {get;}
        public AnalysisView View=>Main.FindControl<AnalysisView>("AnalysisWorkspaceView")!;
        public Window Window {get;}
        public Screen()
        {
            for(var i=0;i<100;i++) Fixture.Add($"https://example.test/中文路径/{i}?user=u{i%4}");
            Fixture.Add("https://example.test/missing");
            Fixture.Records[0]=Fixture.Records[0] with {ElapsedMilliseconds=10000,ResponseBody=Fixture.Records[0].ResponseBody with {Transport=TransportState.Receiving}};
            Page=new(new(Resources),new FileRuleLibraryStore(Root)); Page.SetSource(Fixture.Source()); Page.Editor.Text=M2Fixture.Program();
            Proxy=new(Owner){RecordDirectory=Path.Combine(Root,"capture")};
            Document=new(Proxy,analysis:Page){AnalysisVisible=true}; Main=new(){DataContext=Document};
            Window=new(){Width=800,Height=600,Content=Main}; Window.Show(); Pump(Window);
        }
        public Button Button(string caption)=>Main.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,caption)&&b.IsEffectivelyVisible);
        public void Click(string caption)=>M1CaptureUiTests.Click(Window,Button(caption));
        public async Task Apply() {Click("应用/刷新"); await Page.CurrentTask; Pump(Window); Assert.NotNull(Page.Result);}
        public void Dispose(){Window.Close(); Document.Dispose(); Owner.Dispose(); TemporaryArtifacts.Delete(Root);}
    }
    private static Task Run(Func<Screen,Task> action)=>M1CaptureUiTests.Ui.Value.Dispatch(async()=>{using var screen=new Screen(); await action(screen); return true;},default);
    [Fact] public Task UI01EditorInputAndDiagnostic()=>Run(async s=>
    {
        await s.Apply(); var old=s.Page.Result; s.Click("规则");
        var editor=s.View.FindControl<TextBox>("DslText")!; editor.Focus(); editor.SelectAll(); s.Window.KeyTextInput("dsl 2;"); s.Click("验证");
        Assert.NotEmpty(s.Page.Editor.Diagnostics); var list=s.View.FindControl<ListBox>("DiagnosticList")!;
        var item=list.GetVisualDescendants().OfType<ListBoxItem>().First(); var point=item.TranslatePoint(new Point(20,10),s.Window)!.Value;
        s.Window.MouseDown(point,MouseButton.Left); s.Window.MouseUp(point,MouseButton.Left); Pump(s.Window);
        Assert.Equal(s.Page.Editor.Diagnostics[0].Span.Start,editor.SelectionStart); Assert.True(editor.IsFocused);
        Assert.Same(old,s.Page.Result); editor.SelectAll(); s.Window.KeyTextInput(M2Fixture.Program()); s.Click("验证"); Assert.Empty(s.Page.Editor.Diagnostics);
        M2Fixture.Evidence("layout","UI01",new {diagnosticFocused=true,oldResultPreserved=true,repaired=true});
    });
    [Fact] public Task UI02FormAndPreview()=>Run(async s=>
    {
        s.Click("规则");
        var expander=s.View.GetVisualDescendants().OfType<Expander>().Single(); expander.IsExpanded=true; Pump(s.Window);
        s.Click("生成片段"); Assert.Contains("field businessId",s.Page.Editor.Fragment);
        s.Click("插入片段"); Assert.Contains("field businessId",s.Page.Editor.Text); s.Click("验证"); Assert.Empty(s.Page.Editor.Diagnostics);
        s.Click("样本预览"); await s.Page.PreviewCommand.ExecutionTask!; Assert.Contains("样本 20 / 保留 101",s.Page.PreviewText); Assert.Null(s.Page.Result);
        M2Fixture.Evidence("layout","UI02",new {previewSamples=20,applied=false,fragment=s.Page.Editor.Fragment});
    });
    [Fact] public Task UI03SaveAndLoadFromControls()=>Run(async s=>
    {
        s.Click("规则"); s.Click("保存视图"); await s.Page.SaveCommand.ExecutionTask!; Assert.Single(s.Page.SavedViews);
        s.Page.ViewName="另一个业务视图"; s.Click("另存视图"); await s.Page.SaveAsCommand.ExecutionTask!; Assert.Equal(2,s.Page.SavedViews.Count);
        s.Click("加载视图库"); await s.Page.ReloadCommand.ExecutionTask!;
        var combo=s.View.GetVisualDescendants().OfType<ComboBox>().Single(c=>ReferenceEquals(c.ItemsSource,s.Page.SavedViews)); combo.SelectedIndex=0; Pump(s.Window);
        s.Click("载入草稿"); Assert.Equal("调用过程",s.Page.ViewName); Assert.Null(s.Page.Result);
        M2Fixture.Evidence("layout","UI03",new {views=s.Page.SavedViews.Count,loaded=s.Page.ViewName,applied=false});
    });
    [Fact] public Task UI04TimelineAndIdentityNavigation()=>Run(async s=>
    {
        await s.Apply(); var list=s.View.FindControl<ListBox>("TimelineList")!;
        var row=list.GetVisualDescendants().OfType<ListBoxItem>().Skip(1).First(); var point=row.TranslatePoint(new Point(30,10),s.Window)!.Value;
        s.Window.MouseDown(point,MouseButton.Left); s.Window.MouseUp(point,MouseButton.Left); Pump(s.Window);
        var selected=s.Page.SelectedRequest!.RecordId; Guid? opened=null; s.Page.OpenRequest+=r=>opened=r.RecordId;
        s.Click("打开请求详情"); Assert.Equal(selected,opened); Assert.NotEmpty(list.GetVisualDescendants().OfType<TimelineStrip>());
        s.Window.Width=520; s.Window.Height=420; Pump(s.Window); s.Click("返回组列表"); Assert.True(s.View.FindControl<ListBox>("GroupList")!.IsEffectivelyVisible);
        // 真实CaptureView绑定下验证隐藏行详情；只检查ViewModel会漏掉ListBox回写和定时刷新清空。
        await using var fixture=new M1SessionFixture();
        var hidden=await fixture.AddAsync("hidden body"u8.ToArray(),uri:"https://example.test/hidden?user=u");
        await fixture.AddAsync("visible body"u8.ToArray(),uri:"https://example.test/visible?user=u");
        var store=new CaptureSessionStore(); store.Publish(fixture.Session);
        await using var archives=new FileCaptureArchiveStore();
        using var capture=new CaptureWorkspace(store,archives,new BoundedImageDecoder(store.ContentBudget));
        capture.Keyword="visible"; await Task.Delay(220); await capture.RefreshAsync();
        var captureView=new CaptureView {DataContext=capture};
        var detailWindow=new Window {Width=800,Height=600,Content=captureView}; detailWindow.Show(); Pump(detailWindow);
        try
        {
            Assert.True(await capture.OpenAnalyzedRecordAsync(fixture.Session,hidden.Id)); Pump(detailWindow);
            await capture.RefreshAsync(); Pump(detailWindow);
            Assert.Equal(hidden.Id,capture.Selected!.RecordId); Assert.Null(capture.ListSelected);
            Assert.Equal("hidden body",capture.BodyText); Assert.Equal("visible",capture.Keyword); Assert.Single(capture.Rows);
            var visible=captureView.FindControl<ListBox>("TrafficList")!.GetVisualDescendants().OfType<ListBoxItem>().First();
            var location=visible.TranslatePoint(new Point(20,10),detailWindow)!.Value;
            detailWindow.MouseDown(location,MouseButton.Left); detailWindow.MouseUp(location,MouseButton.Left); await capture.DetailTask;
            Assert.NotEqual(hidden.Id,capture.Selected!.RecordId); Assert.Equal("visible body",capture.BodyText);
        }
        finally {detailWindow.Close();}
        M2Fixture.Evidence("layout","UI04",new {selected,opened,compactBack=true});
    });
    [Fact] public Task UI05InvalidWindowAndUnknownRange()=>Run(async s=>
    {
        await s.Apply(); var old=s.Page.Result; var window=s.View.GetVisualDescendants().OfType<TextBox>().Single(t=>t.PlaceholderText?.StartsWith("留空")==true);
        window.Focus(); s.Window.KeyTextInput("invalid"); s.Click("应用/刷新"); await s.Page.CurrentTask; Assert.Same(old,s.Page.Result);
        window.SelectAll(); s.Window.KeyTextInput("last(30m)"); await s.Apply(); s.Click("未确定"); Assert.Single(s.Page.Requests); Assert.Contains("Missing",s.Page.SelectionText);
        Assert.Null(s.Page.SelectedGroup); Assert.Contains("未确定",s.Page.PaneTitle);
        var groups=s.View.FindControl<ListBox>("GroupList")!;
        var first=groups.GetVisualDescendants().OfType<ListBoxItem>().First();
        var p=first.TranslatePoint(new Point(20,10),s.Window)!.Value;
        s.Window.MouseDown(p,MouseButton.Left); s.Window.MouseUp(p,MouseButton.Left); Pump(s.Window);
        Assert.Equal(25,s.Page.Requests.Count); Assert.Same(s.Page.Groups[0],s.Page.SelectedGroup);
        s.Click("未确定");
        M2Fixture.Evidence("layout","UI05",new {invalidPreserved=true,unknownRows=s.Page.Requests.Count,coverage=s.Page.Result!.Coverage});
    });
    [Fact] public Task UI06MaskAndReset()=>Run(async s=>
    {
        await s.Apply(); Assert.Contains("••••",s.Page.SelectionText); Assert.Equal(4,s.Page.Groups.Count);
        var check=s.View.GetVisualDescendants().OfType<CheckBox>().Single(c=>Equals(c.Content,"显示本次原值"));
        var point=check.TranslatePoint(new Point(10,10),s.Window)!.Value; s.Window.MouseDown(point,MouseButton.Left); s.Window.MouseUp(point,MouseButton.Left); Pump(s.Window);
        Assert.True(s.Page.RevealValues); Assert.Contains("u0",s.Page.SelectionText);
        s.Page.SetSource(s.Fixture.Source()); Assert.False(s.Page.RevealValues); Assert.Empty(s.Page.Groups);
        M2Fixture.Evidence("layout","UI06",new {groupsBefore=4,reset=true});
    });
    [Fact] public Task UI07AttachAndPageSwitch()=>Run(async s=>
    {
        await s.Apply(); var result=s.Page.Result; var toggle=s.Main.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t=>Equals(t.Content,"分组"));
        Click(s.Window,toggle); Assert.False(s.Document.AnalysisVisible); Click(s.Window,toggle); Assert.True(s.Document.AnalysisVisible);
        for(var i=0;i<20;i++){s.Window.Content=null; Pump(s.Window); s.Window.Content=s.Main; Pump(s.Window);}
        Assert.Same(result,s.Page.Result); Assert.Equal(4,s.Page.Groups.Count);
        M2Fixture.Evidence("layout","UI07",new {rounds=20,preserved=true,active=s.Resources.Active});
    });
    [Fact] public Task UI08LayoutsAndScreenshots()=>Run(async s=>
    {
        await s.Proxy.StartCommand.ExecuteAsync(null); await s.Apply(); var shots=new List<object>();
        foreach(var width in new[]{1200,800,520}) foreach(var dark in new[]{false,true}) foreach(var editor in new[]{false,true})
        {
            s.Window.Width=width; s.Window.Height=width==1200?720:width==800?600:420;
            s.Window.RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light;
            s.Page.ShowEditor=editor; Pump(s.Window);
            var stop=s.Button("停止代理"); AssertInside(stop,s.Window);
            var action=s.Button(editor?"验证":"应用/刷新"); AssertInside(action,s.Window);
            var key=$"{(editor?"editor":"analysis")}-{(dark?"dark":"light")}-{width}";
            shots.Add(Screenshot(s,key,new {stop=Bounds(stop,s.Window),action=Bounds(action,s.Window),width,height=s.Window.Height,dark,editor}));
        }
        s.Window.Width=800; s.Window.Height=600; s.Window.RequestedThemeVariant=ThemeVariant.Dark;
        s.Page.ShowEditor=true; s.Page.Editor.Text="dsl 2;"; s.Page.Editor.Validate(); Pump(s.Window); shots.Add(Screenshot(s,"diagnostic",new {errors=s.Page.Editor.Diagnostics.Count}));
        s.Page.ShowEditor=false; s.Page.ShowUnknownCommand.Execute(null); Pump(s.Window); shots.Add(Screenshot(s,"unresolved",new {rows=s.Page.Requests.Count}));
        s.Fixture.IsArchive=true; s.Page.SetSource(s.Fixture.Source()); s.Page.Editor.Text=M2Fixture.Program(); await s.Page.ApplyAsync(); Pump(s.Window); shots.Add(Screenshot(s,"archive",new {s.Page.Result!.To}));
        s.Fixture.IsArchive=false; s.Page.SetSource(s.Fixture.Source()); await s.Page.ApplyAsync(); Pump(s.Window); shots.Add(Screenshot(s,"parallel",new {receiving=s.Page.Groups.Sum(g=>g.Receiving)}));
        Assert.Equal(16,shots.Count); M2Fixture.Evidence("layout","UI08",new {shots});
    });
    private static object Bounds(Control control,Window window)
    {var p=control.TranslatePoint(default,window)!.Value;return new{x=p.X,y=p.Y,width=control.Bounds.Width,height=control.Bounds.Height};}
    private static object Screenshot(Screen s,string key,object measurements)
    {
        var root=Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS")??Path.Combine(ProbeProcess.Repository,"TestResults/M2UiPreview");
        Directory.CreateDirectory(root); Pump(s.Window); using var frame=s.Window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var name="m2-"+key+".png"; var path=Path.Combine(root,name); frame.Save(path,PngBitmapEncoderOptions.Default);
        var proof=new {key,name,width=frame.PixelSize.Width,height=frame.PixelSize.Height,scaling=s.Window.RenderScaling,renderer="Avalonia Headless / Skia",realHost=false,
            sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),measurements};
        File.WriteAllText(path+".json",JsonSerializer.Serialize(proof));return proof;
    }
}
