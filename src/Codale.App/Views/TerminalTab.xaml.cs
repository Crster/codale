using Codale.App.Controls;
using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Codale.App.Views;

/// <summary>
/// A ConPTY terminal, hosted in a centre-area tab and created on demand from the
/// + button. The display is Codale's own native <see cref="TerminalView"/> - Win2D
/// rendering over the TerminalEmulator in Codale.Terminal, no WebView involved.
/// The shell lives exactly as long as the tab: closing it terminates the session.
/// </summary>
public sealed partial class TerminalTab : UserControl
{
    private TerminalViewModel? _viewModel;
    private bool _initialized;
    private bool _subscribed;

    public TerminalTab()
    {
        CrashLog.Trace("terminal: tab ctor begin");
        InitializeComponent();
        Loaded += OnLoaded;

        Terminal.Response += (_, text) => _ = _viewModel?.WriteAsync(text);
        Terminal.SubmitRewriter = line => _viewModel?.RewriteSubmitted(line);
        Terminal.Resized += (_, size) => _viewModel?.Resize((short)size.Cols, (short)size.Rows);
        Terminal.LinkActivated += (_, link) => LinkActivated?.Invoke(this, link);

        SessionPanel.CollapseRequested += (_, _) => SetSessionPanelOpen(false);
        SessionPanel.FileDiffRequested += (_, path) => FileDiffRequested?.Invoke(this, path);
        SessionPanel.ArtifactRequested += (_, artifact) => ArtifactRequested?.Invoke(this, artifact);
        SessionPanel.TaskPeekRequested += (_, task) => TaskPeekRequested?.Invoke(this, task);
        SessionPanel.UpdateCliRequested += (_, cmd) => UpdateCliRequested?.Invoke(this, cmd);

        PointerPressed += (_, _) =>
        {
            if (Terminal.FocusState == FocusState.Unfocused)
            {
                Terminal.Focus(FocusState.Programmatic);
            }
        };
        GotFocus += (_, _) =>
        {
            if (Terminal.FocusState == FocusState.Unfocused)
            {
                Terminal.Focus(FocusState.Programmatic);
            }
        };
        CrashLog.Trace("terminal: tab ctor end");
    }

    /// <summary>A URL or file path in the output was Ctrl+clicked.</summary>
    public event EventHandler<LinkMatch>? LinkActivated;

    /// <summary>True when this terminal tab hosts an agent CLI session.</summary>
    public bool IsAgentCli => _viewModel?.AgentSession is not null;

    /// <summary>The agent session if this terminal hosts an agent CLI session.</summary>
    public ChatViewModel? AgentSession => _viewModel?.AgentSession;

    /// <summary>Whether this tab's session panel is currently open.</summary>
    public bool IsSessionPanelOpen => SessionPanel.Visibility == Visibility.Visible;

    /// <summary>CLI update was requested from the session panel.</summary>
    public event EventHandler<string>? UpdateCliRequested;

    /// <summary>The parent workspace.</summary>
    public WorkspaceViewModel? Workspace
    {
        get => SessionPanel.Workspace;
        set => SessionPanel.Workspace = value;
    }

    /// <summary>A file change was clicked in the session panel, requesting diff view.</summary>
    public event EventHandler<string>? FileDiffRequested;

    /// <summary>An artifact was clicked in the session panel.</summary>
    public event EventHandler<SessionArtifact>? ArtifactRequested;

    /// <summary>A running task was clicked to peek at it.</summary>
    public event EventHandler<RunningTaskItem>? TaskPeekRequested;

    /// <summary>Toggles the session panel visibility for this terminal tab.</summary>
    public void ToggleSessionPanel()
    {
        if (IsAgentCli)
        {
            SetSessionPanelOpen(SessionPanel.Visibility != Visibility.Visible);
        }
    }

    public void SetSessionPanelOpen(bool open)
    {
        if (!IsAgentCli)
        {
            SessionPanel.Visibility = Visibility.Collapsed;
            OpenPanelButton.Visibility = Visibility.Collapsed;
            return;
        }

        SessionPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        OpenPanelButton.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOpenPanelClick(object sender, RoutedEventArgs e) => SetSessionPanelOpen(true);

    public TerminalViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            UnsubscribeFromViewModel();
            _viewModel = value;
            if (_viewModel?.AgentSession is { } session)
            {
                SessionPanel.Session = session;
                SessionPanel.Visibility = Visibility.Visible;
                OpenPanelButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                SessionPanel.Session = null;
                SessionPanel.Visibility = Visibility.Collapsed;
                OpenPanelButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Tears the display down. Called only when the tab is actually closing - a
    /// TabViewItem's content leaves the visual tree whenever another tab is
    /// selected, so Unloaded is tab-hide, not tab-close, and must not tear down
    /// the view or the terminal would come back blank.
    /// </summary>
    public void CloseHost() => UnsubscribeFromViewModel();

    /// <summary>Live-applies Settings changes to the terminal display (font size, scrollback).</summary>
    public void ApplyAppSettings() => Terminal.ApplyAppSettings();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        // Loaded fires again every time the tab is selected (its content leaves the
        // tree when hidden), so the shell takes the keyboard on each switch. Queued
        // behind the tab strip's own focus handling, which would otherwise win.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => Terminal.Focus(FocusState.Programmatic));

        if (_initialized)
        {
            return;
        }

        _initialized = true;
        CrashLog.Trace("terminal: tab loaded");

        try
        {
            _viewModel.OutputReceived += OnOutputReceived;
            _viewModel.Exited += OnExited;
            _subscribed = true;

            // Output can arrive before the tab is first laid out; the emulator buffers
            // everything, so nothing is lost - the first draw shows it all.
            if (_viewModel.StartupError is { Length: > 0 } error)
            {
                Terminal.Write($"\x1b[31m{error}\x1b[0m\r\n\x1b[90m[shell exited]\x1b[0m\r\n");
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Terminal", "terminal tab load failed", ex);
            throw;
        }
    }

    private void OnOutputReceived(object? sender, string chunk) => Terminal.Write(chunk);

    private void OnExited(object? sender, EventArgs e) =>
        Terminal.Write("\r\n\x1b[90m[shell exited]\x1b[0m\r\n");

    /// <summary>
    /// The terminal's own right-click menu: Copy and Paste against the shell, plus
    /// Select All and Clear.
    /// </summary>
    private void OnTerminalContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;

        var menu = new MenuFlyout
        {
            // Rounded to match every other popup in the app.
            MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter))
            {
                Setters = { new Setter(MenuFlyoutPresenter.CornerRadiusProperty, 8) },
            },
        };

        var copy = new MenuFlyoutItem { Text = "Copy", IsEnabled = Terminal.HasSelection };
        copy.Click += (_, _) => Terminal.CopySelection();

        var paste = new MenuFlyoutItem { Text = "Paste" };
        paste.Click += (_, _) => _ = Terminal.PasteAsync();

        var selectAll = new MenuFlyoutItem { Text = "Select All" };
        selectAll.Click += (_, _) => Terminal.SelectAll();

        var clear = new MenuFlyoutItem { Text = "Clear" };
        clear.Click += (_, _) => Terminal.Clear();

        menu.Items.Add(copy);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(paste);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(selectAll);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(clear);

        menu.ShowAt(Terminal, new FlyoutShowOptions { Position = args.TryGetPosition(Terminal, out var position) ? position : default });
    }

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
