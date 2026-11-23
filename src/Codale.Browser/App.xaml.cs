using Microsoft.UI.Xaml;

namespace Codale.Browser;

/// <summary>The WinUI application: opens the browser window once the runtime is up.</summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        // A stray exception in an event handler should not take the window (and every agent's tab) down with it.
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "browser-crash.log"), $"{DateTime.Now} (handled){Environment.NewLine}{e.Exception}{Environment.NewLine}");
            }
            catch (Exception) { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
