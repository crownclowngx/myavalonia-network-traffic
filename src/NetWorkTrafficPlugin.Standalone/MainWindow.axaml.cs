using Avalonia.Controls;
using NetWorkTrafficPlugin.Features.Main;
using MyAvaloniaManagement.PluginSdk;

namespace NetWorkTrafficPlugin.Standalone;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var document = new MainDocument();
        document.InitializeAsync(
            new NewDocumentActivation("NetWorkTrafficPlugin Standalone"),
            CancellationToken.None).GetAwaiter().GetResult();
        DataContext = document;
    }
}
