using Codale.Agents;
using Codale.App.Controls;
using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Codale.App.Views;

/// <summary>
/// A centre tab that runs an agent CLI (<c>claude</c>, <c>codex</c>, <c>agy</c>...): its TUI in a rounded
/// card beside the <see cref="TerminalAgentSessionPanel"/> that mirrors the session - todos, file
/// changes, artifacts, usage and resumable history. The CLI lives exactly as long as the tab.
/// A plain shell is a <see cref="TerminalTab"/>.
/// </summary>
public sealed partial class CliTerminalTab : UserControl, ITerminalHost
{
    private TerminalViewModel? _viewModel;
    private bool _initialized;
    private bool _subscribed;

    public CliTerminalTab()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        Terminal.Response += (_, text) => _ = _viewModel?.WriteAsync(text);
        Terminal.SubmitRewriter = line => _viewModel?.RewriteSubmitted(line);
        Terminal.Resized += (_, size) => _viewModel?.Resize((short)size.Cols, (short)size.Rows);
        Terminal.LinkActivated += (_, link) => LinkActivated?.Invoke(this, link);

        SessionPanel.FileDiffRequested += (_, path) => FileDiffRequested?.Invoke(this, path);
        SessionPanel.ArtifactRequested += (_, artifact) => ArtifactRequested?.Invoke(this, artifact);
        SessionPanel.TaskPeekRequested += (_, task) => TaskPeekRequested?.Invoke(this, task);
        SessionPanel.UpdateCliRequested += (_, command) => UpdateCliRequested?.Invoke(this, command);
        SessionPanel.ResumeRequested += (_, resume) => ResumeRequested?.Invoke(this, resume);

        PointerPressed += (_, _) => FocusTerminal();
        GotFocus += (_, _) => FocusTerminal();
    }

    public event EventHandler<LinkMatch>? LinkActivated;

    /// <summary>A file change was clicked in the session panel: show its diff.</summary>
    public event EventHandler<string>? FileDiffRequested;

    /// <summary>An artifact was clicked in the session panel.</summary>
    public event EventHandler<SessionArtifact>? ArtifactRequested;

    /// <summary>A running task was clicked to peek at it.</summary>
    public event EventHandler<RunningTaskItem>? TaskPeekRequested;

    /// <summary>The CLI's update command should run in a new terminal.</summary>
    public event EventHandler<string>? UpdateCliRequested;

    /// <summary>A past conversation was picked: relaunch its CLI with these arguments.</summary>
    public event EventHandler<(CliDescriptor Cli, string Arguments)>? ResumeRequested;

    /// <summary>The parent workspace.</summary>
    public WorkspaceViewModel? Workspace
    {
        get => SessionPanel.Workspace;
        set => SessionPanel.Workspace = value;
    }

    public TerminalViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            UnsubscribeFromViewModel();
            _viewModel = value;
            SessionPanel.Session = _viewModel?.AgentSession;
        }
    }

    /// <summary>Tears the display down; called only when the tab is actually closing, not when it is hidden.</summary>
    public void CloseHost() => UnsubscribeFromViewModel();

    public void ApplyAppSettings() => Terminal.ApplyAppSettings();

    private void FocusTerminal()
    {
        if (Terminal.FocusState == FocusState.Unfocused)
        {
            Terminal.Focus(FocusState.Programmatic);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        // Loaded fires again every time the tab is selected; the CLI takes the keyboard on each switch.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => Terminal.Focus(FocusState.Programmatic));

        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            _viewModel.OutputReceived += OnOutputReceived;
            _viewModel.Exited += OnExited;
            _subscribed = true;

            if (_viewModel.StartupError is { Length: > 0 } error)
            {
                Terminal.Write($"\x1b[31m{error}\x1b[0m\r\n\x1b[90m[session ended]\x1b[0m\r\n");
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Terminal", "cli terminal tab load failed", ex);
            throw;
        }
    }

    private void OnOutputReceived(object? sender, string chunk) => Terminal.Write(chunk);

    private void OnExited(object? sender, EventArgs e) =>
        Terminal.Write("\r\n\x1b[90m[session ended]\x1b[0m\r\n");

    private void OnTerminalContextRequested(UIElement sender, ContextRequestedEventArgs args) =>
        ShowContextMenu(Terminal, args);

    public void ShowContextMenu(UIElement anchor, ContextRequestedEventArgs args) =>
        TerminalContextMenu.Show(Terminal, args, anchor: anchor);

    private void UnsubscribeFromViewModel()
    {
        if (!_subscribed || _viewModel is null)
        {
            return;
        }

        _subscribed = false;
        _viewModel.OutputReceived -= OnOutputReceived;
        _viewModel.Exited -= OnExited;
    }
}
