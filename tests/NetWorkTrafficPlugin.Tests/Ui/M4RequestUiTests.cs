using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Features.Capture;
using NetWorkTrafficPlugin.Features.Requests;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Infrastructure.Content;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Requests;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;
using static NetWorkTrafficPlugin.Tests.Ui.M1CaptureUiTests;

namespace NetWorkTrafficPlugin.Tests.Ui;

[Collection("M1UI")]
public sealed class M4RequestUiTests
{
    [Theory]
    [InlineData(1200,720,false)] [InlineData(800,600,false)] [InlineData(520,420,false)]
    [InlineData(1200,720,true)] [InlineData(800,600,true)] [InlineData(520,420,true)]
    public Task 生产编辑预览发送和结果三视图六种布局(int width, int height, bool dark) => M1CaptureUiTests.Ui.Value.Dispatch(async () =>
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            using var certificate = ProxyCertificates.CreateRoot(); await using var origin = await ControlledOrigin.StartAsync("M4UI", certificate, https:false);
            var capture = new CaptureSessionStore(); await using var records = new DebugCaptureStore(Path.Combine(root,"capture"),capture.ContentBudget);
            using var bodies = new FileRequestBodyStore(Path.Combine(root,"bodies"));
            await using var executor = new RequestExecutionCoordinator(new HttpRequestTransport(),records);
            await using var archives = new FileCaptureArchiveStore();
            var results = new CaptureWorkspace(records.ViewStore,archives,new BoundedImageDecoder(capture.ContentBudget));
            using var page = new RequestWorkspace(new(capture.ContentBudget,bodies),executor,new FileRequestLibraryStore(root),bodies,records.Session,results);
            var view = new RequestView { DataContext = page };
            var window = new Window { Width=width, Height=height, Content=view, RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light };
            window.Show();
            try
            {
                Pump(window);
                var address = view.FindControl<TextBox>("AddressInput")!; address.Focus(); address.SelectAll(); window.KeyTextInput(new Uri(origin.Address,"/echo?a=%2B").AbsoluteUri);
                var method = view.FindControl<ComboBox>("MethodInput")!; method.SelectedItem="POST";
                Assert.Equal("a=%2B", page.QueryText);
                page.QueryText="a=%2B\na=2\n# disabled=not-sent";
                page.HeadersText="X-Enabled: yes\n# X-Disabled: no";
                page.BodyKind=RequestBodyKind.Json; page.BodyText="{\"message\":\"中文\"}";
                Pump(window); Assert.Equal("POST",page.Method);
                var send=view.FindControl<Button>("SendButton")!; Assert.False(send.IsEffectivelyEnabled);
                var preview=view.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"预览最终请求"));
                Click(window,preview); await page.PreviewCommand.ExecutionTask!; Pump(window);
                Assert.True(send.IsEffectivelyEnabled); Assert.Contains("a=%2B",page.PreviewText); Assert.Empty(origin.Receipts);
                var tabs=view.GetVisualDescendants().OfType<TabControl>().First();
                Save(window,view,"editor",width,height,dark);
                tabs.SelectedIndex=1; Pump(window); Save(window,view,"preview",width,height,dark);
                Click(window,send); await page.SendCommand.ExecutionTask!; Pump(window);
                var receipt = Assert.Single(origin.Receipts); Assert.Equal(RequestExecutionState.Completed,page.CurrentAttempt!.Snapshot.State);
                Assert.EndsWith("?a=%2B&a=2", receipt.Path); Assert.False(receipt.Headers.ContainsKey("X-Disabled"));
                Assert.False(send.IsEffectivelyEnabled); tabs.SelectedIndex=2; Pump(window); Save(window,view,"result",width,height,dark);
                Assert.Single(records.Session.Snapshot().Records); Assert.Equal(TrafficSource.Manual,records.Session.Snapshot().Records[0].Source);
                // 显式模板保存和载入也不能产生额外请求；使用生产命令，恢复后发送仍需重新预览。
                page.TemplateName="界面模板"; await page.SaveTemplateCommand.ExecuteAsync(null); Assert.Single(page.Templates);
                page.SelectedTemplate=page.Templates[0]; page.LoadTemplateCommand.Execute(null); Assert.Single(origin.Receipts);
                Assert.Contains("# disabled=not-sent", page.QueryText);
                page.TemplateName="新修订"; await page.UpdateTemplateCommand.ExecuteAsync(null);
                Assert.Equal(2, Assert.Single(page.Templates).Revision); Assert.Equal("新修订", page.Templates[0].Name);
                page.EnvironmentName="环境A"; page.EnvironmentText="baseUrl=https://a.example.test"; await page.SaveEnvironmentCommand.ExecuteAsync(null);
                page.EnvironmentText="baseUrl=https://b.example.test"; await page.UpdateEnvironmentCommand.ExecuteAsync(null);
                Assert.Equal(2, Assert.Single(page.Environments).Revision);
                await page.PreviewCommand.ExecuteAsync(null); Assert.True(page.SendCommand.CanExecute(null));
                tabs.SelectedIndex=0; Pump(window);
                Click(window,view.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"清除环境选择")));
                Assert.Null(page.SelectedEnvironment); Assert.False(page.SendCommand.CanExecute(null)); Assert.Single(page.Environments);
                page.SelectedEnvironment=page.Environments[0];
                await page.DeleteEnvironmentCommand.ExecuteAsync(null); Assert.Empty(page.Environments);
                await page.DeleteTemplateCommand.ExecuteAsync(null); Assert.Empty(page.Templates); Assert.Single(origin.Receipts);
                Assert.Equal('●', page.VariablesMask);
                page.BodyKind=RequestBodyKind.Form; page.BodyText=" key = value \n# disabled=no";
                await page.PreviewCommand.ExecuteAsync(null); Assert.Contains("+key+=+value+", page.PreviewText);
                Assert.Single(origin.Receipts);
            }
            finally { window.Close(); }
        }
        finally { TemporaryArtifacts.Delete(root); }
        return true;
    }, default);
    private static void Save(Window window, RequestView view, string kind, int width, int height, bool dark)
    {
        Assert.True(view.Bounds.Width <= width && view.Bounds.Height <= height);
        foreach(var button in view.GetVisualDescendants().OfType<Button>().Where(b=>b.IsEffectivelyVisible && b.Content is string caption && caption is "预览最终请求" or "发送一次" or "取消本次请求"))
        {
            var point=button.TranslatePoint(new Point(),window)!.Value;
            Assert.True(point.X>=0 && point.X+button.Bounds.Width<=window.Bounds.Width+1);
        }
        var directory=Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS");
        if(string.IsNullOrEmpty(directory)) return;
        using var bitmap=window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
        var name=$"m4-{kind}-{(dark?"dark":"light")}-{width}.png"; bitmap.Save(Path.Combine(directory,name),PngBitmapEncoderOptions.Default);
        File.WriteAllText(Path.Combine(directory,name+".json"),System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion=1,runId=Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_RUN_ID"),
            sourceHash=Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_SOURCE_HASH"),realHost=false,kind,width,height,dark,
            sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(directory,name))))
        }));
    }
}
