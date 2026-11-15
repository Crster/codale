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

    public ApplicationServices Services { get; } = new();

    public App(ActivationRequest request, AppInstance instance)
    {
        _request = request;
        _instance = instance;

        UnhandledErrors.Install();
        Services.Settings.AttachUiThread(action => _dispatcherQueue.TryEnqueue(() => action()), () => _dispatcherQueue.HasThreadAccess);
        InitializeComponent();

        // Fires when another launch was redirected here because this instance already
        // owns the project. The answer is always "show me the window I already have".
        _instance.Activated += OnRedirectedActivation;
    }

    public static new App Current => (App)Application.Current;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Services.Syntax.Start();
        Services.Smart.Start();
        _window = new MainWindow(_request, Services);
        _window.Closed += (_, _) =>
        {
            _instance.Activated -= OnRedirectedActivation;
            _window = null;
        };
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