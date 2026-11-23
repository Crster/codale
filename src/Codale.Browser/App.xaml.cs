using Microsoft.UI.Xaml;

namespace Codale.Browser;

/// <summary>The WinUI application: opens the browser window once the runtime is up.</summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
