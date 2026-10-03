using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.App.Views;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Codale.App;

/// <summary>A title bar search: the query, and whether it is scoped to the open file.</summary>
public sealed record SearchSubmission(string Query, bool InFile);

/// <summary>A keyboard shortcut fired; the workspace sets <see cref="Handled"/> when it acted on it.</summary>
public sealed class ShortcutEventArgs(string actionId) : EventArgs
{
    public string ActionId { get; } = actionId;

    public bool Handled { get; set; }
}

/// <summary>
/// The project window. One of these exists per project; the launcher variant shows the
/// picker until a project is chosen.
/// </summary>
public sealed partial class MainWindow : Window
{
    private WorkspaceViewModel? _workspace;

    public MainWindow(ActivationRequest request)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        ApplyTheme();
        RootGrid.ActualThemeChanged += (_, _) => ChatPaletteTheme.Apply(RootGrid.ActualTheme == ElementTheme.Light);
        AppSettings.Changed += OnAppSettingsChanged;

        // Closing (not Closed) is where unsaved edits are asked about and the sessions are
        // stopped: it can still be cancelled, and the process outlives it.
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        SettingsHost.CloseRequested += OnSettingsCloseRequested;
        RootGrid.KeyDown += OnRootKeyDown;

        // Tunnelling, so a shortcut wins over the focused control (the terminal and
        // editor would otherwise consume Ctrl+letter chords themselves).
        RootGrid.PreviewKeyDown += OnRootPreviewKeyDown;

        // handledEventsToo: the box's inner TextBox handles Esc itself before it bubbles.
        TitleSearchBox.AddHandler(
            UIElement.KeyDownEvent, new KeyEventHandler(OnSearchKeyDown), handledEventsToo: true);

        if (request.ProjectPath is { } path)
        {
            // Navigating here, mid-constructor, would run the workspace's
            // OnNavigatedTo before App.MainWindow exists: the page would start
            // without its title-bar wiring and panel sync. Wait for the queue.
            DispatcherQueue.TryEnqueue(() => OpenProject(path));
        }
        else
        {
            Title = "Codale";
            AppTitleBar.Title = "Codale";
            RootFrame.Navigate(typeof(ProjectPickerPage));
        }
    }

    public void OpenProject(string projectPath)
    {
        // Opening over a project that is still open would leak its sessions and terminals.
        if (_workspace is { } previous)
        {
            _workspace = null;
            _ = DisposeWorkspaceAsync(previous);
        }

        RecentProjects.Record(projectPath);
        _workspace = new WorkspaceViewModel(projectPath);

        // Project chrome (panel toggles, folder switch, search) only exists once
        // there is something to act on.
        LeftPanelButton.Visibility = Visibility.Visible;
        TitleBarExtras.Visibility = Visibility.Visible;

        // Window.Title alone is not enough when the TitleBar control owns the caption:
        // the taskbar / alt-tab caption follows AppWindow.Title, which Window.Title does
        // not reliably update once ExtendsContentIntoTitleBar is on.
        var title = $"{_workspace.DisplayName} — Codale";
        Title = title;
        AppWindow.Title = title;
        AppTitleBar.Title = _workspace.DisplayName;
        AppTitleBar.Subtitle = _workspace.ProjectPath;

        RootFrame.Navigate(typeof(WorkspacePage), _workspace);
    }

    /// <summary>Called when a second launch for this project was redirected here.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    /// <summary>The title-bar Commands button was clicked; the workspace page owns the flyout.</summary>
    public event EventHandler? CommandsRequested;

    /// <summary>The commands flyout anchors here, at the button the click came from.</summary>
    public FrameworkElement CommandsButtonAnchor => CommandsButton;

    /// <summary>The title-bar session button was clicked; the workspace page owns the panel.</summary>
    public event EventHandler? SessionPanelToggled;

    /// <summary>The title-bar left-panel button was clicked; the workspace page owns the panel.</summary>
    public event EventHandler? LeftPanelToggled;

    /// <summary>A search was submitted from the title bar; the workspace page runs it.</summary>
    public event EventHandler<SearchSubmission>? SearchSubmitted;

    /// <summary>The user is typing a search - true when it is scoped to the open file - so the model can get ready.</summary>
    public event EventHandler<bool>? SearchTyping;

    /// <summary>The open file's name while the box is scoped to it (Ctrl+F in an editor); null for the project.</summary>
    private string? _searchFileScope;

    private const string ProjectSearchPlaceholder = "Ask or search the project…";

    private void OnTitleBarCommandsClick(object sender, RoutedEventArgs e) =>
        CommandsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows the running-command count on the button; zero hides the badge.</summary>
    public void SetCommandsBadge(int count)
    {
        CommandsBadgeText.Text = count.ToString();
        CommandsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The title-bar Browser button was clicked; the workspace page owns the flyout.</summary>
    public event EventHandler? McpToolsRequested;

    /// <summary>The tools flyout anchors here, at the button the click came from.</summary>
    public FrameworkElement McpToolsButtonAnchor => BrowserButton;

    private void OnTitleBarBrowserClick(object sender, RoutedEventArgs e) =>
        McpToolsRequested?.Invoke(this, EventArgs.Empty);

    private void OnTitleBarSessionClick(object sender, RoutedEventArgs e) =>
        SessionPanelToggled?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows the panel's open state on the title-bar toggle (filled = open).</summary>
    public void SetSessionPanelOpen(bool open)
    {
        // A white wash only reads on dark; the light theme gets the same wash in black.
        var light = RootGrid.ActualTheme == ElementTheme.Light;
        SessionButton.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(
                open ? (byte)0x18 : (byte)0x00,
                light ? (byte)0x00 : (byte)0xFF,
                light ? (byte)0x00 : (byte)0xFF,
                light ? (byte)0x00 : (byte)0xFF));
    }

    private void OnTitleBarLeftPanelClick(object sender, RoutedEventArgs e) =>
        LeftPanelToggled?.Invoke(this, EventArgs.Empty);

    private const double SettingsWidth = 560;

    private bool _settingsOpen;

    private Storyboard? _settingsAnimation;

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SetSettingsOpen(!_settingsOpen);

    /// <summary>Slides the Settings pane in from (or out to) the right edge.</summary>
    public void SetSettingsOpen(bool open)
    {
        if (open == _settingsOpen)
        {
            return;
        }

        _settingsOpen = open;
        _settingsAnimation?.Stop();

        if (open)
        {
            SettingsHost.Reload();
            SettingsHost.Visibility = Visibility.Visible;
        }

        var slide = new DoubleAnimation
        {
            From = SettingsSlide.X,
            To = open ? 0 : SettingsWidth,
            Duration = TimeSpan.FromMilliseconds(open ? 240 : 180),
            EasingFunction = new CubicEase
            {
                EasingMode = open
                    ? EasingMode.EaseOut
                    : EasingMode.EaseIn,
            },
        };
        Storyboard.SetTarget(slide, SettingsSlide);
        Storyboard.SetTargetProperty(slide, "X");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Completed += (_, _) =>
        {
            // Hidden once off-screen so it stops taking focus and hit-tests.
            if (!_settingsOpen)
            {
                SettingsHost.Visibility = Visibility.Collapsed;
            }
        };
        _settingsAnimation = storyboard;
        storyboard.Begin();

        if (open)
        {
            SettingsHost.Focus(FocusState.Programmatic);
        }
    }

    private void OnSettingsCloseRequested(object? sender, EventArgs e) => SetSettingsOpen(false);

    /// <summary>A configured keyboard shortcut was pressed; the open workspace runs it.</summary>
    public event EventHandler<ShortcutEventArgs>? ShortcutInvoked;

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (KeyboardShortcuts.IsRecording || ShortcutInvoked is null ||
            KeyChord.FromKeyPress(e.Key, usableOnly: true) is not { IsUsable: true } chord ||
            KeyboardShortcuts.Find(chord) is not { } actionId)
        {
            return;
        }

        // Holding the chord must not run the action again and again.
        if (e.KeyStatus.WasKeyDown)
        {
            e.Handled = true;
            return;
        }

        var args = new ShortcutEventArgs(actionId);
        ShortcutInvoked.Invoke(this, args);
        e.Handled = args.Handled;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && _settingsOpen)
        {
            e.Handled = true;
            SetSettingsOpen(false);
        }
    }

    /// <summary>
    /// Applies the theme choice to this window and re-colours the shared palette
    /// brushes (converters read Application.Resources, so the palette follows the
    /// resolved theme rather than any one window's).
    /// </summary>
    private void OnAppSettingsChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = AppSettings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ChatPaletteTheme.Apply(RootGrid.ActualTheme == ElementTheme.Light);
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        SearchSubmitted?.Invoke(this, new SearchSubmission(args.QueryText, InFile: _searchFileScope is not null));

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && sender.Text.Length > 0)
        {
            SearchTyping?.Invoke(this, _searchFileScope is not null);
        }
    }

    /// <summary>Puts the caret in the title bar search box; the one place a search starts.</summary>
    public void FocusSearch() => FocusSearch(fileName: null);

    /// <summary>
    /// Focuses the box, scoped to one open file when <paramref name="fileName"/> is given.
    /// The scope shows in the placeholder and icon, and lasts until the box loses focus
    /// or Esc returns it to the project - so a plain click on the box always means the
    /// whole project.
    /// </summary>
    public void FocusSearch(string? fileName)
    {
        SetSearchScope(fileName);

        if (fileName is not null)
        {
            // Cleared so the scoped placeholder is visible, not hidden behind the last query.
            TitleSearchBox.Text = "";
        }

        TitleSearchBox.Focus(FocusState.Programmatic);
    }

    private void SetSearchScope(string? fileName)
    {
        _searchFileScope = fileName;
        TitleSearchBox.PlaceholderText = fileName is null
            ? ProjectSearchPlaceholder
            : $"Find in {fileName} · Esc for the project";
        TitleSearchBox.QueryIcon = new SymbolIcon(fileName is null ? Symbol.Find : Symbol.Document);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && _searchFileScope is not null)
        {
            e.Handled = true;
            SetSearchScope(null);
        }
    }

    /// <summary>
    /// Focus moving inside the box (text to the search icon) also raises LostFocus, so
    /// the check waits until focus has settled and only resets once it has left.
    /// </summary>
    private void OnSearchLostFocus(object sender, RoutedEventArgs e)
    {
        if (_searchFileScope is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            var focused = FocusManager.GetFocusedElement(TitleSearchBox.XamlRoot) as DependencyObject;
            for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, TitleSearchBox))
                {
                    return;
                }
            }

            SetSearchScope(null);
        });
    }

    /// <summary>
    /// Back to the project picker: the open workspace (and its agent processes) is torn
    /// down first so nothing outlives the folder it belonged to. Unsaved edits are asked about first.
    /// </summary>
    private async void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ConfirmUnsavedEditsAsync())
            {
                return;
            }

            if (_workspace is { } workspace)
            {
                _workspace = null;
                await DisposeWorkspaceAsync(workspace);
            }

            Title = "Codale";
            AppTitleBar.Title = "Codale";
            AppTitleBar.Subtitle = null;
            LeftPanelButton.Visibility = Visibility.Collapsed;
            TitleBarExtras.Visibility = Visibility.Collapsed;
            RootFrame.Navigate(typeof(ProjectPickerPage));
        }
        catch (Exception ex)
        {
            CrashLog.Error("MainWindow", "changing folder failed", ex);
        }
    }

    /// <summary>True once the close was confirmed and the workspace torn down; the next Closing passes through.</summary>
    private bool _closeConfirmed;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || _workspace is null)
        {
            return;
        }

        args.Cancel = true;
        _ = CloseAfterCleanupAsync();
    }

    /// <summary>
    /// Asks about unsaved edits, then stops every agent process and terminal before the
    /// window really closes: doing it in Closed raced the process exit, so a CLI could outlive the app.
    /// </summary>
    private async Task CloseAfterCleanupAsync()
    {
        try
        {
            if (!await ConfirmUnsavedEditsAsync())
            {
                return;
            }

            // The decision is made: the window goes away at once, the cleanup finishes behind it.
            AppWindow.Hide();

            if (_workspace is { } workspace)
            {
                _workspace = null;
                await DisposeWorkspaceAsync(workspace);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Error("MainWindow", "close cleanup failed", ex);
        }

        _closeConfirmed = true;
        Close();
    }

    private static async Task DisposeWorkspaceAsync(WorkspaceViewModel workspace)
    {
        try
        {
            await workspace.DisposeAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Error("MainWindow", "workspace shutdown failed", ex);
        }
    }

    /// <summary>
    /// Save / Don't save / Cancel for edits that are not on disk. True to go ahead (saved or
    /// discarded), false to stay.
    /// </summary>
    private async Task<bool> ConfirmUnsavedEditsAsync()
    {
        if (_workspace is not { HasDirtyEditors: true } workspace)
        {
            return true;
        }

        try
        {
            var names = workspace.DirtyEditorNames;
            var list = string.Join("\n", names.Take(8).Select(name => "•  " + name))
                + (names.Count > 8 ? $"\n…and {names.Count - 8} more" : "");

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Save your changes?",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"These files have unsaved changes:\n\n{list}" },
                PrimaryButtonText = "Save all",
                SecondaryButtonText = "Don't save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.None)
            {
                return false;
            }

            if (choice == ContentDialogResult.Secondary)
            {
                return true;
            }

            var unsaved = await workspace.SaveAllEditorsAsync();
            if (unsaved.Count == 0)
            {
                return true;
            }

            await new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Some files were not saved",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "These could not be saved (untitled, changed on disk, or write failed). " +
                           $"Save them from their tabs, or close again to discard them:\n\n{string.Join("\n", unsaved.Select(name => "•  " + name))}",
                },
                CloseButtonText = "OK",
            }.ShowAsync();
            return false;
        }
        catch (Exception ex)
        {
            // Another dialog is already up, or the window is going away: stay open rather than lose edits.
            CrashLog.Error("MainWindow", "unsaved-changes prompt failed", ex);
            return false;
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        AppSettings.Changed -= OnAppSettingsChanged;

        // Normally already torn down by CloseAfterCleanupAsync; this catches any other way out.
        if (_workspace is { } workspace)
        {
            _workspace = null;
            await DisposeWorkspaceAsync(workspace);
        }
    }
}
