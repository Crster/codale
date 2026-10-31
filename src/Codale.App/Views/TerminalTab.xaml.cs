using Codale.App.Controls;
using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Codale.App.Views;

/// <summary>
/// A ConPTY shell, hosted in a centre-area tab and created on demand from the
/// + button. The display is Codale's own native <see cref="TerminalView"/> - Win2D
/// rendering over the TerminalEmulator in Codale.Terminal, no WebView involved.
/// The shell lives exactly as long as the tab: closing it terminates the session.
/// An agent CLI gets a <see cref="CliTerminalTab"/> instead.
/// </summary>
public sealed partial class TerminalTab : UserControl, ITerminalHost
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

        PointerPressed += (_, _) => FocusTerminal();
        GotFocus += (_, _) => FocusTerminal();
        CrashLog.Trace("terminal: tab ctor end");
    }

    /// <summary>A URL or file path in the output was Ctrl+clicked.</summary>
    public event EventHandler<LinkMatch>? LinkActivated;

    public TerminalViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            UnsubscribeFromViewModel();
            _viewModel = value;
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

    private void OnTerminalContextRequested(UIElement sender, ContextRequestedEventArgs args) =>
        TerminalContextMenu.Show(Terminal, args,
            _viewModel is { IsCommandTerminal: true, IsCliTerminal: false } viewModel
                ? viewModel.RefreshAsync : null);

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
