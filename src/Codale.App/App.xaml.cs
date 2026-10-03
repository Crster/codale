using Codale.App.Services;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

using Windows.ApplicationModel.Activation;

namespace Codale.App;

public partial class App : Application
{
    private readonly ActivationRequest _request;
    private readonly AppInstance _instance;
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    private MainWindow? _window;

    public App(ActivationRequest request, AppInstance instance)
    {
        _request = request;
        _instance = instance;

        CrashLog.Install();
        AppSettings.AttachUiThread();
        Codale.Terminal.TerminalSession.Log = message => CrashLog.Debug("pty", message);
        InitializeComponent();

        // Fires when another launch was redirected here because this instance already
        // owns the project. The answer is always "show me the window I already have".
        _instance.Activated += OnRedirectedActivation;
    }

    public static new App Current => (App)Application.Current;

    /// <summary>HWND of the project window, for APIs that must be told their owner (pickers, dialogs).</summary>
    public IntPtr? MainWindowHandle =>
        _window is null ? null : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    /// <summary>The window itself, for cross-element wiring (title bar ↔ workspace page).</summary>
    public MainWindow? MainWindow => _window;

    /// <summary>
    /// Opens a project in the existing window instead of launching a second one. The
    /// welcome content is simply replaced by the workspace.
    /// </summary>
    public void OpenProjectInPlace(string projectPath)
    {
        if (_window is not null)
        {
            _window.OpenProject(projectPath);
        }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Services.SyntaxSelection.Start();
        _window = new MainWindow(_request);
        _window.Activate();
    }

    private void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        // Arrives on a background thread.
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_window is null)
            {
                return;
            }

            // If the launcher window is told to open a concrete project, honour it
            // rather than just flashing an empty window at the user.
            if (_request.ProjectPath is null &&
                e.Kind == ExtendedActivationKind.Protocol &&
                e.Data is IProtocolActivatedEventArgs protocol &&
                ActivationRequest.FromUri(protocol.Uri) is { } path)
            {
                _window.OpenProject(path);
            }

            _window.BringToFront();
        });
    }
}
