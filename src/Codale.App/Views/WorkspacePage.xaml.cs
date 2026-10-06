using System.ComponentModel;
using System.Diagnostics;

using Codale.Agents;
using Codale.Agents.Claude;
using Codale.App.Controls;
using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.Commands;
using Codale.Core.Agents;
using Codale.Core.Syntax;
using Codale.Git;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

using Windows.Storage.Pickers;

namespace Codale.App.Views;

public sealed partial class WorkspacePage : Page
{
    // The status bar's divider after the timer only earns its place when something follows it.
    public static Visibility AnyVisible(int count, bool flag) =>
        count > 0 || flag ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Every open chat tab, one conversation each; they all keep running in the background.</summary>
    private readonly List<ChatTabEntry> _chatTabs = [];
    private SearchTab? _searchTab;
    private TabViewItem? _searchTabItem;

    /// <summary>The one centre diff tab; every git diff request replaces its contents.</summary>
    private DiffTab? _diffTab;
    private TabViewItem? _diffTabItem;

    /// <summary>The one centre plan tab; opening another plan replaces its contents.</summary>
    private ArtifactTab? _artifactTab;
    private TabViewItem? _artifactTabItem;

    /// <summary>The change the right-click menu is on, captured when the menu opens.</summary>
    private GitFileStatus? _changeMenuItem;

    /// <summary>Every open terminal tab; each one has its own shell.</summary>
    private readonly List<TerminalTabEntry> _terminalTabs = [];

    /// <summary>Every open file tab, preview and pinned alike.</summary>
    private readonly List<EditorTabEntry> _editorTabs = [];

    /// <summary>The left panel's column width, kept while the panel is hidden so it can come back as it was.</summary>
    private GridLength _leftPanelWidth = new(260);

    /// <summary>The one tab a file click may replace; null once its file is edited.</summary>
    private EditorTabEntry? _previewTab;

    public WorkspacePage() => InitializeComponent();

    public WorkspaceViewModel ViewModel { get; private set; } = null!;

    public CliUpdateViewModel CliUpdate { get; } = CliUpdateViewModel.Instance;

    /// <summary>
    /// Updates the CLI in a terminal tab, so its output is visible and nothing changes
    /// without a click. The version is then re-read every few seconds until the new one
    /// shows, so the icon goes away by itself once the update has landed.
    /// </summary>
    private async void OnUpdateCliClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunInNewTerminalAsync(CliVersionCheck.UpdateCommand);

            for (var i = 0; i < 36 && CliUpdate.UpdateAvailable; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await CliUpdate.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("WorkspacePage.OnUpdateCliClick", ex.Message, ex);
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not WorkspaceViewModel workspace)
        {
            return;
        }

        ViewModel = workspace;
        ViewModel.PropertyChanged += OnWorkspacePropertyChanged;
        ViewModel.ChatActivationRequested += OnChatActivationRequested;
        WatchTodos(ViewModel.Chat);
        Bindings.Update();

        UpdateEmptyState();

        if (App.Current.MainWindow is { } window)
        {
            window.CommandsRequested += OnTitleBarCommandsRequested;
            window.McpToolsRequested += OnTitleBarMcpToolsRequested;
            window.SessionPanelToggled += OnTitleBarSessionToggled;
            window.LeftPanelToggled += OnTitleBarLeftPanelToggled;
            window.SearchSubmitted += OnTitleBarSearchSubmitted;
            window.SearchTyping += OnTitleBarSearchTyping;
            window.ShortcutInvoked += OnShortcutInvoked;

            // Sync the toggle and reserved width with the panel's default state.
            SetSessionPanel(_sessionPanelOpen);
        }

        // The Settings window writes app-level preferences; this page applies them
        // live to the tabs it owns. All windows share one UI thread.
        AppSettings.Changed += OnAppSettingsChanged;

        _ = CliUpdate.RefreshAsync();

        // Everything except the CLI: no agent session starts until the user opens a
        // chat tab and asks for one. An exception escaping this async void would kill
        // the process with nothing but a stowed XAML exception in its wake, so it is
        // logged and the panels that did load stay usable.
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write("WorkspacePage.OnNavigatedTo", ex.Message, ex);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        AppSettings.Changed -= OnAppSettingsChanged;

        if (App.Current.MainWindow is { } window)
        {
            window.CommandsRequested -= OnTitleBarCommandsRequested;
            window.McpToolsRequested -= OnTitleBarMcpToolsRequested;
            window.SessionPanelToggled -= OnTitleBarSessionToggled;
            window.LeftPanelToggled -= OnTitleBarLeftPanelToggled;
            window.SearchSubmitted -= OnTitleBarSearchSubmitted;
            window.SearchTyping -= OnTitleBarSearchTyping;
            window.ShortcutInvoked -= OnShortcutInvoked;
            window.Activated -= OnWindowActivated;
        }

        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnWorkspacePropertyChanged;
            ViewModel.ChatActivationRequested -= OnChatActivationRequested;

            foreach (var entry in _chatTabs)
            {
                StopTabAttention(entry);
                entry.Chat.LoginRequired -= OnChatLoginRequired;
                entry.Chat.AttentionNeeded -= OnChatAttentionNeeded;
                entry.Chat.PropertyChanged -= OnChatPropertyChanged;
            }
        }
    }

    /// <summary>
    /// A setting was written in the Settings window: apply it to the tabs that are
    /// already open. New tabs read the settings themselves when they are created.
    /// </summary>
    private void OnAppSettingsChanged(object? sender, EventArgs e)
    {
        foreach (var entry in _chatTabs)
        {
            entry.Tab.ApplyAppSettings();
        }

        foreach (var entry in _editorTabs)
        {
            entry.Tab.ApplyAppSettings();
        }

        foreach (var entry in _terminalTabs)
        {
            entry.Tab.ApplyAppSettings();
        }

        ViewModel?.Git.ApplyPollInterval();
    }

    /// <summary>
    /// The placeholder and the tab strip trade places: with no tabs the whole strip
    /// (including its + button) is gone and the placeholder's buttons are the entry
    /// point; with tabs the placeholder disappears and + comes back.
    /// </summary>
    private void UpdateEmptyState()
    {
        var hasTabs = CentreTabs.TabItems.Count > 0;
        EmptyState.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
        CentreTabs.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        CentreTabs.IsAddTabButtonVisible = hasTabs;

        if (!hasTabs)
        {
            RefreshCliLabels();
        }
    }

    /// <summary>
    /// The status bar shows cursor and word counts only while an editor tab is in
    /// front, reading that tab's own view model through <see cref="WorkspaceViewModel.ActiveEditor"/>.
    /// </summary>
    private void OnCentreTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        foreach (var chatTab in _chatTabs)
        {
            RefreshTabAttention(chatTab);
        }

        var activeItem = CentreTabs.SelectedItem as TabViewItem;
        var active = _editorTabs.FirstOrDefault(t => ReferenceEquals(t.Item, activeItem))?.Tab;

        foreach (var entry in _editorTabs)
        {
            entry.Tab.ViewModel.IsTabActive = ReferenceEquals(entry.Tab, active);
        }

        ViewModel.ActiveEditor = active?.ViewModel;
        UpdateLanguageButton();

        // The session panel and status bar follow the chat in front.
        if (ChatTabInFront() is { } chatEntry)
        {
            ViewModel.Chat = chatEntry.Chat;
        }

        UpdateSessionPanelForActiveTab();
    }

    private EditorTab? ActiveEditorTab() =>
        _editorTabs.FirstOrDefault(t => ReferenceEquals(t.Item, CentreTabs.SelectedItem))?.Tab;

    /// <summary>
    /// Shows the language colouring the file in front (or hides the control when the tab in front is not
    /// a saved file). A language picked by hand is marked so it is not mistaken for detection.
    /// </summary>
    private void UpdateLanguageButton()
    {
        if (ActiveEditorTab() is not { } tab || !tab.ViewModel.HasFile)
        {
            LanguageHost.Visibility = Visibility.Collapsed;
            return;
        }

        var name = SyntaxService.Catalog.ById(tab.LanguageId)?.Name ?? "Plain Text";
        LanguageText.Text = tab.HasManualLanguage ? $"{name} (manual)" : name;
        LanguageHost.Visibility = Visibility.Visible;
    }

    private void OnLanguageFlyoutOpening(object? sender, object e)
    {
        if (ActiveEditorTab() is not { } tab || !tab.ViewModel.HasFile)
        {
            return;
        }

        LanguagePickerControl.Load(tab.LanguageId, tab.HasManualLanguage, tab.ViewModel.FileName ?? "Untitled");
        LanguagePickerControl.LanguageChosen -= OnLanguageChosen;
        LanguagePickerControl.LanguageChosen += OnLanguageChosen;

        if (!_languageActionsAdded)
        {
            _languageActionsAdded = true;
            LanguagePickerControl.AddAction("Download grammars…", () =>
            {
                LanguageFlyout.Hide();
                _ = SyntaxDialogs.ShowCatalogAsync(XamlRoot);
            });
            LanguagePickerControl.AddAction("Generate with AI…", () =>
            {
                LanguageFlyout.Hide();
                _ = GenerateGrammarForActiveFileAsync();
            });
        }
    }

    private bool _languageActionsAdded;

    /// <summary>Opens the AI generator pre-filled from the file in front: its extension, and its text as the sample.</summary>
    private async Task GenerateGrammarForActiveFileAsync()
    {
        if (ActiveEditorTab() is not { } tab || !tab.ViewModel.HasFile)
        {
            return;
        }

        var ext = Path.GetExtension(tab.ViewModel.FileName ?? string.Empty).TrimStart('.');
        await SyntaxDialogs.ShowGenerateAsync(XamlRoot, ViewModel.Helper, ext.ToUpperInvariant(), ext, tab.GetEditorText());
    }

    private void OnLanguageChosen(string? languageId)
    {
        LanguageFlyout.Hide();
        ActiveEditorTab()?.SetLanguage(languageId);
    }

    private void OnCentreAddTabClick(TabView sender, object args)
    {
        // Anchor to the + button itself: showing at the TabView drops the menu at
        // the bottom of the whole centre area. The button lives in the control's
        // template, so fall back to the TabView if the template ever renames it.
        if (FindTemplateChild(sender, "AddButton") is FrameworkElement addButton)
        {
            NewTabMenuFlyout.ShowAt(addButton);
            return;
        }

        NewTabMenuFlyout.ShowAt(sender);
    }

    private static DependencyObject? FindTemplateChild(DependencyObject parent, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is FrameworkElement { Name: var childName } && childName == name)
            {
                return child;
            }

            var found = FindTemplateChild(child, name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private async void OnNewTabMenuItemClick(object sender, RoutedEventArgs e)
    {
        NewTabMenuFlyout.Hide();

        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "claude":
                await OpenChatOrOfferInstallAsync();
                break;
            case "terminal":
                OpenTerminalTab();
                break;
            case "openfile":
                await OpenFileFromPickerAsync();
                break;
            case "newfile":
                OpenNewFileTab();
                break;
            case "search":
                OpenSearchTab();
                App.Current.MainWindow?.FocusSearch();
                break;
        }
    }

    /// <summary>
    /// The chat entries name the CLI's state: "Claude chat" when it is on the PATH,
    /// "Install Claude CLI…" when it is not. Checked on every opening, so a CLI
    /// installed from the offered terminal shows up without a restart.
    /// </summary>
    private void OnNewTabMenuOpening(object? sender, object e) => RefreshCliLabels();

    private void RefreshCliLabels()
    {
        var claude = CliLocator.IsInstalled() ? "Claude chat" : "Install Claude CLI…";
        ClaudeMenuItem.Text = claude;
        ClaudeEmptyLabel.Text = claude;
    }

    /// <summary>
    /// Opens the chat when the Claude CLI exists; otherwise offers to install it,
    /// running the installer in a new terminal tab so the user sees its output.
    /// </summary>
    private async Task OpenChatOrOfferInstallAsync()
    {
        if (CliLocator.IsInstalled())
        {
            await OpenChatTabAsync();
            return;
        }

        var command = CliLocator.InstallCommand;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Claude CLI not found",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"The '{CliLocator.Executable}' command isn't on this system's PATH. " +
                       $"Install it now? This runs the following in a new terminal:\n\n{command}",
            },
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunInNewTerminalAsync(command);
    }

    /// <summary>Time a fresh shell gets to print its prompt before a command is typed into it.</summary>
    private static readonly TimeSpan ShellPromptDelay = TimeSpan.FromMilliseconds(1500);

    private async Task RunInNewTerminalAsync(string command)
    {
        var terminal = ViewModel.CreateTerminal();
        AddTerminalTab(terminal);

        await Task.Delay(ShellPromptDelay);
        await terminal.WriteAsync(command + "\r");
    }

    private bool _loginPromptOpen;

    /// <summary>
    /// The CLI has no credentials. Its login is interactive (a browser OAuth flow),
    /// so it runs in a terminal tab; the chat works on the next message afterwards.
    /// </summary>
    private void OnChatLoginRequired(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_loginPromptOpen)
            {
                return;
            }

            _loginPromptOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Claude CLI isn't logged in",
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Open a terminal to log in? It starts 'claude'; type /login there, finish " +
                               "sign-in in your browser, then exit with /exit and resend your message here.",
                    },
                    PrimaryButtonText = "Open terminal",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await RunInNewTerminalAsync("claude");
                }
            }
            finally
            {
                _loginPromptOpen = false;
            }
        });

    /// <summary>
    /// The + menu's chat entries: a new conversation in its own tab, so a running one
    /// is never replaced. The chat in front is reused only while it holds nothing yet.
    /// </summary>
    private async Task OpenChatTabAsync()
    {
        CrashLog.Trace("Chat tab open requested");

        var chat = ViewModel.FreshChat();
        await ViewModel.ApplyDefaultsToEmptyChatAsync(chat);
        ShowChatTab(chat);

        // The CLI connects the first time a chat tab exists, not on window load.
        if (!chat.IsConnected)
        {
            await chat.ConnectAsync();
        }
    }

    /// <summary>The chat that last asked for the reader while the window was elsewhere; shown when they come back.</summary>
    private ChatViewModel? _attentionChat;

    private bool _watchingActivation;

    /// <summary>
    /// A chat needs the reader. When Codale is the window in front, the transcript
    /// already shows it (the question card, the amber status and tab dot), so nothing
    /// more is said. Otherwise the taskbar button flashes until the window is brought
    /// forward, a notification says what is waiting - clicking it opens this project's
    /// window - and on return the chat that asked is brought to the front.
    /// </summary>
    private void OnChatAttentionNeeded(object? sender, AttentionRequest request)
    {
        if (sender is not ChatViewModel chat ||
            App.Current.MainWindowHandle is not { } hwnd ||
            Services.Attention.IsForeground(hwnd))
        {
            return;
        }

        Services.Attention.FlashTaskbar(hwnd);

        var who = chat.ProviderName;
        var title = request.Kind switch
        {
            AttentionKind.Question => $"{who} has a question for you",
            AttentionKind.Permission => $"{who} needs your permission",
            AttentionKind.Plan => $"{who} has a plan for you to review",
            AttentionKind.TurnFinished => $"{who} finished",
            _ => $"{who} stopped with an error",
        };

        Services.Attention.Notify(
            title,
            $"{request.Message}\n{chat.TabTitle} · {ViewModel.DisplayName}",
            ActivationRequest.ToUri(ViewModel.ProjectPath));

        // A turn that merely finished is not worth yanking the reader's tab around.
        if (request.NeedsAnswer)
        {
            _attentionChat = chat;
        }

        if (!_watchingActivation && App.Current.MainWindow is { } window)
        {
            _watchingActivation = true;
            window.Activated += OnWindowActivated;
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || _attentionChat is not { } chat)
        {
            return;
        }

        _attentionChat = null;

        // Still waiting? Then that is what the reader came back for.
        if (chat.IsWaitingForYou && _chatTabs.Any(e => ReferenceEquals(e.Chat, chat)))
        {
            ShowChatTab(chat);
        }
    }

    /// <summary>The workspace brought a chat forward (history, resume, new session): show its tab.</summary>
    private void OnChatActivationRequested(object? sender, ChatViewModel chat) => ShowChatTab(chat);

    /// <summary>
    /// Brings a chat's tab to the front, creating it the first time. The tab header
    /// carries the conversation's title and a pulsing dot while it works, so a turn
    /// running in a background tab stays visible.
    /// </summary>
    private void ShowChatTab(ChatViewModel chat)
    {
        var entry = _chatTabs.FirstOrDefault(e => ReferenceEquals(e.Chat, chat));

        if (entry is null)
        {
            var tab = new ChatTab { ViewModel = chat };
            tab.ToolFileRequested += OnChatToolFileRequested;
            tab.PlanOpenRequested += OnChatPlanOpenRequested;
            chat.LoginRequired += OnChatLoginRequired;
            chat.AttentionNeeded += OnChatAttentionNeeded;
            chat.PropertyChanged += OnChatPropertyChanged;

            var item = new TabViewItem
            {
                IsClosable = true,
                IconSource = new FontIconSource { Glyph = "\uE8BD" },
                Header = ChatTabHeader(chat),
                Content = tab,
            };
            ApplyIsolationTint(item, chat);

            entry = new ChatTabEntry(item, tab, chat);
            _chatTabs.Add(entry);
            CentreTabs.TabItems.Add(item);
            UpdateEmptyState();
        }

        ViewModel.Chat = chat;
        CentreTabs.SelectedItem = entry.Item;
    }

    /// <summary>The tab's chat icon is blue while its chat works in an isolated worktree.</summary>
    private static void ApplyIsolationTint(TabViewItem item, ChatViewModel chat)
    {
        if (item.IconSource is FontIconSource icon)
        {
            icon.Foreground = chat.IsIsolated
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6))
                : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        }
    }

    private void OnChatPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsIsolated) &&
            _chatTabs.FirstOrDefault(t => ReferenceEquals(t.Chat, sender)) is { } isolated)
        {
            ApplyIsolationTint(isolated.Item, isolated.Chat);
        }

        if (e.PropertyName == nameof(ChatViewModel.IsWaitingForYou) &&
            _chatTabs.FirstOrDefault(t => ReferenceEquals(t.Chat, sender)) is { } entry)
        {
            RefreshTabAttention(entry);
        }
    }

    /// <summary>
    /// Tints the whole tab amber and breathes it while its chat is waiting on the reader (a question,
    /// a permission, a plan) and the tab is not the one in front - with several chats
    /// open, the small amber dot alone is easy to miss. Looking at the tab stops it.
    /// Only the brush pulses, so the icon, title and close button stay steady.
    /// </summary>
    private void RefreshTabAttention(ChatTabEntry entry)
    {
        var active = entry.Chat.IsWaitingForYou && !ReferenceEquals(CentreTabs.SelectedItem, entry.Item);
        if (active == entry.AttentionPulse is not null)
        {
            return;
        }

        if (!active)
        {
            StopTabAttention(entry);
            return;
        }

        // TabViewItem's template paints its whole container (icon, title, close button)
        // with Background, so a local value covers the tab edge to edge.
        var warn = ((SolidColorBrush)Application.Current.Resources["StatusWarnBrush"]).Color;
        var tint = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, warn.R, warn.G, warn.B)) { Opacity = 0.45 };
        entry.Item.Background = tint;

        var breathe = new DoubleAnimation
        {
            From = 0.45,
            To = 0.15,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(breathe, tint);
        Storyboard.SetTargetProperty(breathe, "Opacity");

        var storyboard = new Storyboard { Children = { breathe } };
        entry.AttentionPulse = storyboard;
        storyboard.Begin();
    }

    /// <summary>Ends a tab's attention pulse and hands its background back to the theme.</summary>
    private static void StopTabAttention(ChatTabEntry entry)
    {
        entry.AttentionPulse?.Stop();
        entry.AttentionPulse = null;
        entry.Item.ClearValue(Control.BackgroundProperty);
    }

    private static StackPanel ChatTabHeader(ChatViewModel chat)
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)Application.Current.Resources["StatusRunningBrush"],
        };
        dot.SetBinding(VisibilityProperty, new Binding
        {
            Path = new PropertyPath(nameof(ChatViewModel.IsBusyWorking)),
            Source = chat,
            Converter = new BoolToVisibilityConverter(),
        });
        dot.SetBinding(Pulse.IsActiveProperty, new Binding { Path = new PropertyPath(nameof(ChatViewModel.IsBusyWorking)), Source = chat });

        // Amber while the chat waits on the reader, so a question in a background tab is findable.
        var waiting = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)Application.Current.Resources["StatusWarnBrush"],
        };
        waiting.SetBinding(VisibilityProperty, new Binding
        {
            Path = new PropertyPath(nameof(ChatViewModel.IsWaitingForYou)),
            Source = chat,
            Converter = new BoolToVisibilityConverter(),
        });
        waiting.SetBinding(Pulse.IsActiveProperty, new Binding { Path = new PropertyPath(nameof(ChatViewModel.IsWaitingForYou)), Source = chat });

        var title = new TextBlock
        {
            MaxWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetBinding(TextBlock.TextProperty, new Binding { Path = new PropertyPath(nameof(ChatViewModel.TabTitle)), Source = chat });
        title.SetBinding(ToolTipService.ToolTipProperty, new Binding { Path = new PropertyPath(nameof(ChatViewModel.TabTitle)), Source = chat });

        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, waiting, title } };
    }

    private ChatTabEntry? ChatTabInFront() =>
        _chatTabs.FirstOrDefault(e => ReferenceEquals(e.Item, CentreTabs.SelectedItem));

    private void OpenTerminalTab() => AddTerminalTab(ViewModel.CreateTerminal());

    /// <summary>
    /// Shows a terminal - shell or command - as a new centre tab. Every invocation is
    /// a new session: terminals stack as tabs like in VS Code.
    /// </summary>
    private void AddTerminalTab(TerminalViewModel terminal)
    {
        CrashLog.Trace("terminal: AddTerminalTab begin");
        try
        {
            var tab = new TerminalTab { ViewModel = terminal };
            tab.LinkActivated += OnTerminalLinkActivated;
            var item = new TabViewItem { IsClosable = true, Header = BuildTerminalHeader(terminal) };
            item.Content = tab;

            var entry = new TerminalTabEntry(item, tab, terminal);
            _terminalTabs.Add(entry);
            CentreTabs.TabItems.Add(item);
            UpdateEmptyState();
            CentreTabs.SelectedItem = item;

            // The shell lives exactly as long as its tab: started when the tab first
            // appears, terminated when the tab is closed.
            if (!terminal.IsOpen)
            {
                terminal.Toggle();
            }

            CrashLog.Trace("terminal: AddTerminalTab end");
        }
        catch (Exception ex)
        {
            CrashLog.Write("Terminal", "AddTerminalTab failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Tab chrome for a terminal: the terminal glyph, tinted by state (working, idle,
    /// exited, failed) so it still reads as a terminal tab, plus a title that names
    /// the CLI running inside. Both bind to the terminal's own view model, so the
    /// background process-tree poll updates them on their own. A command tab titles
    /// itself with the command's name and wears the play glyph instead.
    /// </summary>
    private object BuildTerminalHeader(TerminalViewModel terminal)
    {
        var icon = new FontIcon
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Glyph = terminal.IsCommandTerminal ? "\uE768" : "\uE756",
        };
        icon.SetBinding(FontIcon.ForegroundProperty, new Binding
        {
            Path = new PropertyPath("Activity"),
            Source = terminal,
            Converter = (IValueConverter)Resources["TerminalActivityBrush"],
        });

        var name = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (terminal.IsCommandTerminal)
        {
            // The command's name is the stable title; what is executing underneath
            // ("node") is less meaningful than what the user clicked.
            name.SetBinding(TextBlock.TextProperty, new Binding
            {
                Path = new PropertyPath("CommandName"),
                Source = terminal,
            });
        }
        else
        {
            name.SetBinding(TextBlock.TextProperty, new Binding
            {
                Path = new PropertyPath("RunningCommand"),
                Source = terminal,
                Converter = (IValueConverter)Resources["TerminalTitle"],
            });
        }

        // The "(exited)" marker updates through its own binding; a pathless binding
        // would read the view model once and never hear the poll's updates.
        var suffix = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        suffix.SetBinding(TextBlock.TextProperty, new Binding
        {
            Path = new PropertyPath("Activity"),
            Source = terminal,
            Converter = (IValueConverter)Resources["TerminalExitSuffix"],
        });

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { icon, name, suffix },
        };
    }

    private async Task OpenFileFromPickerAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");

        // A packaged picker needs to be told which window owns it.
        if (App.Current.MainWindowHandle is { } hwnd)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        if (await picker.PickSingleFileAsync() is { } file)
        {
            ShowFile(file.Path);
        }
    }

    private void OpenNewFileTab()
    {
        // Untitled buffers are born pinned: there is nothing on disk to preview, and
        // every click of "New file" means one more buffer.
        var entry = CreateEditorTab(isPreview: false);
        CentreTabs.SelectedItem = entry.Item;
        entry.Tab.StartNewFile();
    }

    private void OpenSearchTab()
    {
        if (_searchTab is null)
        {
            _searchTab = new SearchTab { ViewModel = ViewModel.Search };
            _searchTab.FileOpenRequested += async (_, request) =>
            {
                try
                {
                    await ShowSearchResultAsync(request);
                }
                catch (Exception ex)
                {
                    CrashLog.Write("WorkspacePage.ShowSearchResult", ex.Message, ex);
                }
            };
            _searchTabItem = new TabViewItem { Header = "Search", IsClosable = true };
            _searchTabItem.IconSource = new FontIconSource { Glyph = "\uE721" };
            _searchTabItem.Content = _searchTab;
            CentreTabs.TabItems.Add(_searchTabItem);
            UpdateEmptyState();
        }

        CentreTabs.SelectedItem = _searchTabItem;
    }

    /// <summary>
    /// The centre diff tab: one instance for every diff the git panel asks for, the way
    /// a single preview tab serves every file click. Its header title follows the diff
    /// currently loaded.
    /// </summary>
    private void EnsureDiffTab()
    {
        if (_diffTab is not null)
        {
            return;
        }

        _diffTab = new DiffTab().WithViewModel(ViewModel.DiffViewer);
        _diffTab.FileOpenRequested += (_, relativePath) =>
        {
            if (ResolveGitPath(relativePath) is { } absolute)
            {
                ShowFile(absolute);
            }
        };

        var icon = new FontIcon { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Glyph = "\uE8A5" };
        var name = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        name.SetBinding(TextBlock.TextProperty, new Binding
        {
            Path = new PropertyPath("Title"),
            Source = ViewModel.DiffViewer,
            FallbackValue = "Diff",
        });

        _diffTabItem = new TabViewItem { IsClosable = true, Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon, name } } };
        _diffTabItem.Content = _diffTab;
        CentreTabs.TabItems.Add(_diffTabItem);
        UpdateEmptyState();
    }

    /// <summary>Click on a change in the git panel: show its diff in the centre.</summary>
    private async void OnGitChangeClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GitFileStatus change)
        {
            await ShowChangeDiffAsync(change);
        }
    }

    /// <summary>
    /// A file chip in the transcript: edits open the diff, reads open the file. The
    /// chip carries the path as the agent reported it - absolute, or relative to the
    /// repository - so both directions get resolved before the view loads.
    /// </summary>
    private async void OnChatToolFileRequested(object? sender, ToolFileRequestEventArgs e)
    {
        // The call's own change, when the transcript has it: what this one edit did,
        // not everything uncommitted in the file.
        if (e.IsEdit && e.Diff is { Hunks.Count: > 0 } diff)
        {
            ShowAgentDiff(e.Path, diff);
            return;
        }

        if (e.IsEdit && ToRepoRelative(e.Path) is { } relative)
        {
            EnsureDiffTab();
            CentreTabs.SelectedItem = _diffTabItem;
            var absolute = ResolveGitPath(relative);
            await ViewModel.DiffViewer.LoadFileAsync(relative, absolute is not null && File.Exists(absolute));
        }
        else if (e.Line > 0 || !File.Exists(ResolveWorkspacePath(e.Path)))
        {
            // Also for a bare name like test.cs that only a search of the repo can place.
            await OpenFileAtAsync(e.Path, e.Line);
        }
        else
        {
            ShowFile(ResolveWorkspacePath(e.Path));
        }
    }

    /// <summary>Ctrl+click on a URL or path in a terminal.</summary>
    private async void OnTerminalLinkActivated(object? sender, Codale.App.Services.LinkMatch link)
    {
        if (link.Url is { } url)
        {
            await OpenUrlAsync(url);
        }
        else if (link.Path is { } path)
        {
            await OpenFileAtAsync(path, link.Line);
        }
    }

    private static async Task OpenUrlAsync(string url)
    {
        // Only web links launch from text: a path-shaped "URL" could be any registered protocol.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    /// <summary>
    /// Opens a file a person clicked in text and, when the text named a line, scrolls to
    /// it and marks it. The path is as printed - absolute, relative to the project, or a
    /// bare name - so it is resolved before giving up.
    /// </summary>
    private async Task OpenFileAtAsync(string path, int line)
    {
        var resolved = ResolveWorkspacePath(path.Replace('/', '\\'));
        if (!File.Exists(resolved))
        {
            var root = ViewModel.Git.RepositoryRoot ?? ViewModel.ProjectPath;
            resolved = await Task.Run(() => FindByName(root, path)) ?? resolved;
        }

        if (!File.Exists(resolved))
        {
            return;
        }

        var entry = await ShowFileTabAsync(resolved);
        if (line > 0)
        {
            entry.Tab.ShowHighlights([new Codale.Search.LineSpan(line, line)], $"line {line}", reveal: true, moveCaret: true);
        }
    }

    private static readonly HashSet<string> SkippedFolders =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs" };

    /// <summary>
    /// A relative path whose folders differ from the project's - or a bare file name -
    /// found by its trailing segments. First match wins; build output is skipped.
    /// </summary>
    private static string? FindByName(string root, string path)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return null;
        }

        var tail = path.Replace('/', '\\').TrimStart('.', '\\');
        var name = Path.GetFileName(tail);
        if (name.Length == 0)
        {
            return null;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, name))
                {
                    if (file.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!SkippedFolders.Contains(Path.GetFileName(sub)))
                    {
                        pending.Push(sub);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
            }
        }

        return null;
    }

    /// <summary>An agent-made diff - one edit, or a file's whole session - in the diff tab.</summary>
    private void ShowAgentDiff(string path, Codale.Git.FileDiff diff)
    {
        var absolute = ResolveWorkspacePath(path);
        EnsureDiffTab();
        CentreTabs.SelectedItem = _diffTabItem;
        ViewModel.DiffViewer.LoadDiffs(
            Path.GetFileName(absolute),
            ToRepoRelative(absolute) ?? absolute,
            [diff with { Path = ToRepoRelative(absolute) ?? absolute }],
            File.Exists(absolute));
    }

    /// <summary>A row in the panel's session changes: everything the agent did to that file.</summary>
    private void OnSessionChangeClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionFileChange change)
        {
            ShowAgentDiff(change.Path, change.CumulativeDiff());
        }
    }

    private void OnChatPlanOpenRequested(object? sender, ToolCallItem call)
    {
        var artifact = ViewModel.Chat.Artifacts.FirstOrDefault(a => a.ToolUseId == call.ToolUseId)
            ?? new SessionArtifact { Kind = SessionArtifactKind.Plan, ToolUseId = call.ToolUseId, Markdown = call.PlanText ?? "" };
        ShowArtifact(artifact);
    }

    /// <summary>A plan in the panel: a plan document opens in the editor, a chat plan in the plan tab.</summary>
    private void OnArtifactClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionArtifact artifact)
        {
            ShowArtifact(artifact);
        }
    }

    private void ShowArtifact(SessionArtifact artifact)
    {
        if (artifact.Kind == SessionArtifactKind.File)
        {
            // The path is chosen by the agent, so it is revealed rather than launched: opening it
            // would run an .exe, .bat or .ps1 on a click.
            if (artifact.SourcePath is { } output && File.Exists(output))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{output}\"") { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Explorer unavailable: nothing to show.
                }
            }

            return;
        }

        if (_artifactTab is null)
        {
            _artifactTab = new ArtifactTab();
            _artifactTab.RevisionRequested += (plan, message) =>
                _ = ViewModel.Chat.SendRoutedAsync(message, [], Codale.Core.Helper.RouteIntent.Plan);
            _artifactTabItem = new TabViewItem
            {
                IsClosable = true,
                IconSource = new FontIconSource { Glyph = "\uE8FD" },
                Content = _artifactTab,
            };
            CentreTabs.TabItems.Add(_artifactTabItem);
            UpdateEmptyState();
        }

        _artifactTab.Show(artifact);
        _artifactTabItem!.Header = artifact.Title.Length > 28 ? artifact.Title[..28] + "…" : artifact.Title;
        CentreTabs.SelectedItem = _artifactTabItem;
    }

    /// <summary>Tool paths may be bare relative names; they live under the project.</summary>
    private string ResolveWorkspacePath(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        var root = ViewModel.Git.RepositoryRoot ?? ViewModel.ProjectPath;
        var candidate = Path.Combine(root, path.Replace('/', '\\'));
        return File.Exists(candidate) ? candidate : path;
    }

    /// <summary>Absolute tool path back to repository-relative, or null when the file sits outside the repo.</summary>
    private string? ToRepoRelative(string path)
    {
        var root = ViewModel.Git.RepositoryRoot ?? ViewModel.ProjectPath;
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var rootFull = Path.GetFullPath(root);
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(rootFull, full).Replace('\\', '/')
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task ShowChangeDiffAsync(GitFileStatus change)
    {
        var absolute = ResolveGitPath(change.Path);
        EnsureDiffTab();
        CentreTabs.SelectedItem = _diffTabItem;
        await ViewModel.DiffViewer.LoadFileAsync(change.Path, absolute is not null && File.Exists(absolute));
    }

    /// <summary>View diff on a file-tree node: same diff tab, path rewritten to repository-relative.</summary>
    private async Task ShowNodeDiffAsync(FileNode node)
    {
        var root = ViewModel.Git.RepositoryRoot ?? ViewModel.ProjectPath;
        var relative = Path.GetRelativePath(root, node.FullPath).Replace('\\', '/');
        EnsureDiffTab();
        CentreTabs.SelectedItem = _diffTabItem;
        await ViewModel.DiffViewer.LoadFileAsync(relative, File.Exists(node.FullPath));
    }

    /// <summary>Click on a commit in the history graph: show everything it changed.</summary>
    private async void OnGitCommitClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GitCommit commit)
        {
            return;
        }

        EnsureDiffTab();
        CentreTabs.SelectedItem = _diffTabItem;
        await ViewModel.DiffViewer.LoadCommitAsync(commit);
    }

    /// <summary>Change paths are relative to the repository root, which may sit above the project folder.</summary>
    private string? ResolveGitPath(string relativePath) =>
        ViewModel.Git.RepositoryRoot is { } root
            ? Path.Combine(root, relativePath.Replace('/', '\\'))
            : null;


    /// <summary>
    /// Opens a file the way a preview tab works: the click shows it in the single
    /// preview tab (italic header), and each further click replaces that tab's
    /// contents. Editing the file pins its tab, so the next click opens a fresh
    /// preview and the edited file stays open.
    /// </summary>
    private void ShowFile(string path) => _ = ShowFileTabAsync(path);

    private async Task<EditorTabEntry> ShowFileTabAsync(string path)
    {
        // A pinned tab already showing this file wins over the preview tab.
        var open = _editorTabs.FirstOrDefault(t =>
            !t.Tab.ViewModel.IsPreview && PathsEqual(t.Tab.ViewModel.FilePath, path));
        if (open is not null)
        {
            CentreTabs.SelectedItem = open.Item;
            return open;
        }

        if (_previewTab is { } preview)
        {
            // The preview is always read from disk: it cannot hold unsaved edits,
            // because the first keystroke pins the tab. Re-clicking picks up outside
            // changes instead of showing stale text.
            await preview.Tab.OpenFileAsync(path);
            CentreTabs.SelectedItem = preview.Item;
            return preview;
        }

        var entry = CreateEditorTab(isPreview: true);
        await entry.Tab.OpenFileAsync(path);
        CentreTabs.SelectedItem = entry.Item;
        return entry;
    }

    /// <summary>A search result: open the file and mark what the search found in it.</summary>
    private async Task ShowSearchResultAsync(SearchOpenRequest request)
    {
        var entry = await ShowFileTabAsync(request.FilePath);
        var count = request.Spans.Count;

        entry.Tab.ShowHighlights(
            request.Spans,
            count == 1 ? $"1 match · {ViewModel.Search.Query}" : $"{count} matches · {ViewModel.Search.Query}",
            reveal: true,
            current: request.Current,
            moveCaret: true);
    }

    /// <summary>Creates an editor tab with its own view model and registers it.</summary>
    private EditorTabEntry CreateEditorTab(bool isPreview)
    {
        var editor = new EditorViewModel { IsPreview = isPreview };

        // A save changes the working tree, so git re-colours the file tree now rather than on the next poll.
        editor.Saved += (_, _) => _ = ViewModel.Git.RefreshAsync();
        var tab = new EditorTab { Helper = ViewModel.Helper, ProjectRoot = ViewModel.ProjectPath }.WithViewModel(editor);
        var item = new TabViewItem { IsClosable = true };
        item.Content = tab;
        item.DoubleTapped += OnEditorTabDoubleTapped;

        var entry = new EditorTabEntry(item, tab);
        tab.Edited += (_, _) => PromoteEditorTab(entry);
        tab.FindReferencesRequested += (_, text) =>
        {
            OpenSearchTab();
            ViewModel.Search.FindReferences(text);
        };
        tab.LanguageChanged += (_, _) =>
        {
            if (ReferenceEquals(ActiveEditorTab(), tab))
            {
                UpdateLanguageButton();
            }
        };

        _editorTabs.Add(entry);
        if (isPreview)
        {
            _previewTab = entry;
        }

        ApplyHeader(entry);
        CentreTabs.TabItems.Add(item);
        UpdateEmptyState();
        return entry;
    }

    /// <summary>
    /// The first edit (or save, or a double-click on the header) claims the tab: it
    /// stops being the preview, so replacing the preview on the next file click can
    /// no longer touch it. The header's bindings pick the change up on their own.
    /// </summary>
    private void PromoteEditorTab(EditorTabEntry entry)
    {
        if (_previewTab != entry)
        {
            return;
        }

        _previewTab = null;
        entry.Tab.ViewModel.IsPreview = false;
    }

    private void OnEditorTabDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is TabViewItem item &&
            _editorTabs.FirstOrDefault(t => ReferenceEquals(t.Item, item)) is { } entry)
        {
            PromoteEditorTab(entry);
        }
    }

    /// <summary>
    /// Tab chrome: a state icon (preview, new file, edited, saved) and the file name,
    /// italic while it is still a preview. All of it binds against the tab's own view
    /// model, so promotions, edits and save-as renames update the tab on their own.
    /// </summary>
    private void ApplyHeader(EditorTabEntry entry)
    {
        var vm = entry.Tab.ViewModel;

        var icon = new FontIcon { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        icon.SetBinding(FontIcon.GlyphProperty, new Binding
        {
            Path = new PropertyPath("TabState"),
            Source = vm,
            Converter = (IValueConverter)Resources["TabStateToGlyph"],
        });

        icon.SetBinding(FontIcon.ForegroundProperty, new Binding
        {
            Path = new PropertyPath("TabState"),
            Source = vm,
            Converter = (IValueConverter)Resources["TabStateToBrush"],
        });

        var name = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetBinding(TextBlock.TextProperty, new Binding
        {
            Path = new PropertyPath("FileName"),
            Source = vm,
            TargetNullValue = "Untitled",
        });
        name.SetBinding(TextBlock.FontStyleProperty, new Binding
        {
            Path = new PropertyPath("IsPreview"),
            Source = vm,
            Converter = (IValueConverter)Resources["PreviewToItalic"],
        });

        entry.Item.Header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { icon, name },
        };
    }

    /// <summary>Windows paths: case-insensitive comparison.</summary>
    private static bool PathsEqual(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void OnCentreTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        sender.TabItems.Remove(args.Item);
        UpdateEmptyState();
        UpdateSessionPanelForActiveTab();

        if (_editorTabs.FirstOrDefault(t => ReferenceEquals(t.Item, args.Item)) is { } entry)
        {
            _editorTabs.Remove(entry);

            if (_previewTab == entry)
            {
                _previewTab = null;
            }

            entry.Tab.ViewModel.Close();

            if (ReferenceEquals(ViewModel.ActiveEditor, entry.Tab.ViewModel))
            {
                ViewModel.ActiveEditor = null;
            }

            return;
        }

        if (_chatTabs.FirstOrDefault(t => ReferenceEquals(t.Item, args.Item)) is { } chatEntry)
        {
            // Closing a chat ends that conversation only - the others keep running -
            // and it stays reachable through the session list. Fire and forget: a tab
            // close cannot await.
            CrashLog.Trace("Chat tab closed (session ended)");
            _chatTabs.Remove(chatEntry);
            StopTabAttention(chatEntry);
            chatEntry.Chat.LoginRequired -= OnChatLoginRequired;
            chatEntry.Chat.AttentionNeeded -= OnChatAttentionNeeded;
            chatEntry.Chat.PropertyChanged -= OnChatPropertyChanged;

            if (ReferenceEquals(_attentionChat, chatEntry.Chat))
            {
                _attentionChat = null;
            }
            _ = ViewModel.CloseChatAsync(chatEntry.Chat);
        }
        else if (ReferenceEquals(args.Item, _diffTabItem))
        {
            _diffTab = null;
            _diffTabItem = null;
        }
        else if (ReferenceEquals(args.Item, _artifactTabItem))
        {
            _artifactTab = null;
            _artifactTabItem = null;
        }
        else if (_terminalTabs.FirstOrDefault(t => ReferenceEquals(t.Item, args.Item)) is { } terminalEntry)
        {
            _terminalTabs.Remove(terminalEntry);

            // Closing the tab ends that shell - a command's as well; the other
            // terminals keep running.
            terminalEntry.Tab.CloseHost();
            ViewModel.RemoveTerminal(terminalEntry.Terminal);
        }
        else if (ReferenceEquals(args.Item, _searchTabItem))
        {
            _searchTab = null;
            _searchTabItem = null;
        }
    }

    private void OnTitleBarMcpToolsRequested(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            if (FlyoutBase.GetAttachedFlyout(McpToolsHost) is { } flyout)
            {
                flyout.ShowAt(window.McpToolsButtonAnchor);
            }
        }
    }

    private void OnTitleBarCommandsRequested(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            ShowCommandsFlyout(window.CommandsButtonAnchor);
        }
    }

    /// <summary>The commands menu while it is on screen, so its shortcut can close it again.</summary>
    private MenuFlyout? _commandsFlyout;

    private void ToggleCommandsFlyout()
    {
        if (_commandsFlyout is { IsOpen: true } open)
        {
            open.Hide();
        }
        else if (App.Current.MainWindow is { } window)
        {
            ShowCommandsFlyout(window.CommandsButtonAnchor);
        }
    }

    /// <summary>
    /// The commands menu: what is running, what can run, and the files both came
    /// from. Built fresh on every open, so a config edited anywhere is current.
    /// </summary>
    private void ShowCommandsFlyout(FrameworkElement anchor)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

        // Same rounded presenter as the + button's menu, which lives in XAML.
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        menu.MenuFlyoutPresenterStyle = presenterStyle;

        var running = ViewModel.RunningCommands;
        if (running.Count > 0)
        {
            AddFlyoutHeader(menu, "Running");
            foreach (var run in running)
            {
                var item = new MenuFlyoutItem
                {
                    Text = run.CommandName,
                    Tag = run,
                    Icon = new FontIcon { Glyph = "\uE76C" },
                };
                ToolTipService.SetToolTip(item, run.CommandText);
                item.Click += OnRunningCommandClick;
                menu.Items.Add(item);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var commands = ViewModel.DiscoverCommands();
        if (commands.Count == 0)
        {
            AddFlyoutHeader(menu, "No commands found");
        }
        else
        {
            // Discover orders by source; grouping on it keeps that order in the menu.
            foreach (var group in commands.GroupBy(command => command.Source))
            {
                AddFlyoutHeader(menu, group.First().SourceLabel);
                foreach (var command in group)
                {
                    var item = new MenuFlyoutItem
                    {
                        Text = command.Name,
                        Tag = command,
                        Icon = new FontIcon { Glyph = "\uE768" },
                    };
                    ToolTipService.SetToolTip(item, command.Command);
                    item.Click += OnCommandItemClick;
                    menu.Items.Add(item);
                }
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var edit = new MenuFlyoutItem
        {
            Text = "Edit commands…",
            Icon = new FontIcon { Glyph = "\uE70F" },
        };
        edit.Click += OnEditCommandsClick;
        menu.Items.Add(edit);

        // The open menu holds the keyboard focus in its own popup, out of reach of the
        // window's key handler; a menu item's accelerator is how the chord reaches it.
        if (KeyChord.Parse(KeyboardShortcuts.Get(KeyboardShortcuts.ToggleCommands)) is { } toggle)
        {
            var accelerator = new KeyboardAccelerator
            {
                Key = toggle.Key,
                Modifiers = (toggle.Ctrl ? Windows.System.VirtualKeyModifiers.Control : 0)
                    | (toggle.Shift ? Windows.System.VirtualKeyModifiers.Shift : 0)
                    | (toggle.Alt ? Windows.System.VirtualKeyModifiers.Menu : 0),
            };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                menu.Hide();
            };
            edit.KeyboardAccelerators.Add(accelerator);
            edit.KeyboardAcceleratorTextOverride = " ";
        }

        // The discovered files are editable too; the Codale file is the item above.
        var codaleFile = Path.Combine(ViewModel.ProjectPath, ".codale", "commands.json");
        foreach (var path in commands.Select(command => command.SourcePath).OfType<string>().Distinct())
        {
            if (PathsEqual(path, codaleFile))
            {
                continue;
            }

            var sourceItem = new MenuFlyoutItem
            {
                Text = $"Edit {Path.GetRelativePath(ViewModel.ProjectPath, path)}",
                Tag = path,
                Icon = new FontIcon { Glyph = "\uE70F" },
            };
            sourceItem.Click += OnEditSourceFileClick;
            menu.Items.Add(sourceItem);
        }

        _commandsFlyout = menu;
        menu.ShowAt(anchor);
    }

    /// <summary>A quiet label row; menu flyouts have no section header of their own.</summary>
    private static void AddFlyoutHeader(MenuFlyout menu, string text) =>
        menu.Items.Add(new MenuFlyoutItem { Text = text, IsEnabled = false });

    private void OnRunningCommandClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TerminalViewModel terminal)
        {
            RevealTerminal(terminal);
        }
    }

    private void OnCommandItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectCommand command)
        {
            OpenCommandTab(command);
        }
    }

    private void OnEditCommandsClick(object sender, RoutedEventArgs e) =>
        ShowFile(ViewModel.EnsureCommandsFile());

    private void OnEditSourceFileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path)
        {
            ShowFile(path);
        }
    }

    /// <summary>
    /// Opens the command in a terminal tab - or brings the live terminal for it to
    /// the front: one command, one tab, so a second click never starts a second copy.
    /// </summary>
    private void OpenCommandTab(ProjectCommand command)
    {
        var terminal = ViewModel.RunProjectCommand(command);

        if (_terminalTabs.FirstOrDefault(t => ReferenceEquals(t.Terminal, terminal)) is { } existing)
        {
            CentreTabs.SelectedItem = existing.Item;
            return;
        }

        AddTerminalTab(terminal);
    }

    /// <summary>Brings a terminal's tab to the front; its output has been growing off-screen.</summary>
    private void RevealTerminal(TerminalViewModel terminal)
    {
        if (_terminalTabs.FirstOrDefault(t => ReferenceEquals(t.Terminal, terminal)) is { } existing)
        {
            CentreTabs.SelectedItem = existing.Item;
        }
    }

    /// <summary>The running-command count rides from the workspace onto the title-bar badge.</summary>
    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.RunningCommandCount))
        {
            App.Current.MainWindow?.SetCommandsBadge(ViewModel.RunningCommandCount);
        }
        else if (e.PropertyName == nameof(WorkspaceViewModel.Chat))
        {
            WatchTodos(ViewModel.Chat);
        }
    }

    /// <summary>The chat whose task list the panel is watching.</summary>
    private ChatViewModel? _todoWatched;

    /// <summary>
    /// When the agent publishes a task list, the Todos section opens by itself - a
    /// fresh set of goals is exactly what the reader wants to see, even if they had
    /// folded the section while it was empty.
    /// </summary>
    private void WatchTodos(ChatViewModel chat)
    {
        if (_todoWatched is not null)
        {
            _todoWatched.Todos.CollectionChanged -= OnTodosCollectionChanged;
        }

        _todoWatched = chat;
        _todoCountSeen = 0;
        chat.Todos.CollectionChanged += OnTodosCollectionChanged;
        OpenTodosOnFirstGoals();
    }

    /// <summary>The list size last settled on; only an empty-to-something change opens the section.</summary>
    private int _todoCountSeen;

    /// <summary>
    /// Whether the session panel is showing. The panel tree is deferred (x:Load), so
    /// this - not the element's Visibility - is the state of record until first open.
    /// </summary>
    private bool _sessionPanelOpen;

    // Every update rebuilds the list (clear, then add), so the check waits for the
    // rebuild to settle: a reader who folded a live list is not overruled on each tick.
    private void OnTodosCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, OpenTodosOnFirstGoals);

    private void OpenTodosOnFirstGoals()
    {
        var count = _todoWatched?.Todos.Count ?? 0;

        if (_todoCountSeen == 0 && count > 0)
        {
            if (RightPanel is null)
            {
                FindName("RightPanel");
            }

            TodosSection.IsChecked = true;
        }

        _todoCountSeen = count;
    }

    /// <summary>
    /// One place for the session panel's state: the panel floats over the centre
    /// card, but the workspace reserves its width so nothing runs underneath it,
    /// and the title-bar toggle reflects the state.
    /// </summary>
    private void SetSessionPanel(bool open)
    {
        var wasOpen = _sessionPanelOpen;

        // The panel is over half the page's visual tree; it stays deferred until the
        // chat needs it, so a workspace that never opens the chat never builds it.
        if (open && RightPanel is null)
        {
            FindName("RightPanel");
        }

        _sessionPanelOpen = open;
        if (RightPanel is not null)
        {
            RightPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }

        // Panel width (450) + its right inset (6) + a small gap.
        CentreContent.Margin = open ? new Thickness(0, 0, 464, 0) : default;

        if (ViewModel is not null)
        {
            ViewModel.IsSessionPanelOpen = open;
        }

        // Appearing is also what populates it: the transcript scan runs now rather
        // than at window load, so a project that never opens the chat never pays.
        if (open && !wasOpen && ViewModel is not null)
        {
            _ = ViewModel.Sessions.RefreshCommand.ExecuteAsync(null);
        }

        App.Current.MainWindow?.SetSessionPanelOpen(open);
    }

    /// <summary>
    /// The session panel belongs to the agent chat: it is shown only while the chat
    /// tab is in front of the centre area, and hides when another tab takes over or
    /// the chat tab closes. The title-bar button can still close it by hand while
    /// the chat stays in front.
    /// </summary>
    private void UpdateSessionPanelForActiveTab()
    {
        if (ViewModel is null)
        {
            return;
        }

        var chatInFront = ChatTabInFront() is not null;
        SetSessionPanel(chatInFront);

        // The peek belongs to the chat's task list: it goes when the chat does.
        if (!chatInFront)
        {
            SetPeek(null);
        }
    }

    /// <summary>
    /// Opens the peek beside the chat at half width (the splitter can move it), swaps
    /// to another task, or - with null - closes it and gives the tabs the full width.
    /// </summary>
    private void SetPeek(RunningTaskItem? task)
    {
        if (task is null)
        {
            PeekPane.Detach();
            PeekPane.Visibility = Visibility.Collapsed;
            PeekSplitter.Visibility = Visibility.Collapsed;
            PeekSplitterColumn.Width = new GridLength(0);
            PeekColumn.Width = new GridLength(0);
            CentreTabsColumn.Width = new GridLength(1, GridUnitType.Star);
            return;
        }

        if (PeekPane.Visibility != Visibility.Visible)
        {
            CentreTabsColumn.Width = new GridLength(1, GridUnitType.Star);
            PeekSplitterColumn.Width = GridLength.Auto;
            PeekColumn.Width = new GridLength(1, GridUnitType.Star);
            PeekSplitter.Visibility = Visibility.Visible;
            PeekPane.Visibility = Visibility.Visible;
        }

        PeekPane.Show(task);
    }

    private void OnRunningTaskClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RunningTaskItem task)
        {
            return;
        }

        // Clicking the task already on show closes the peek again.
        SetPeek(ReferenceEquals(PeekPane.Task, task) && PeekPane.Visibility == Visibility.Visible ? null : task);
    }

    private void OnPeekCloseRequested(object? sender, EventArgs e) => SetPeek(null);

    private async void OnStopTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RunningTaskItem task } && ViewModel is not null)
        {
            await ViewModel.Chat.StopTaskAsync(task);
        }
    }

    private async void OnPeekStopRequested(object? sender, RunningTaskItem task)
    {
        if (ViewModel is not null)
        {
            await ViewModel.Chat.StopTaskAsync(task);
        }
    }

    private async void OnTitleBarSessionToggled(object? sender, EventArgs e) => await ToggleSessionPanelAsync();

    private async Task ToggleSessionPanelAsync()
    {
        // With the chat in front the button simply toggles the panel; otherwise it
        // brings the chat back, since the panel only exists for the agent.
        if (ChatTabInFront() is not null)
        {
            SetSessionPanel(!_sessionPanelOpen);
            return;
        }

        await BringChatToFrontAsync();
    }

    /// <summary>
    /// The chat that was last in front if it still has a tab, else any chat tab, else a
    /// new conversation (offering the CLI install when it is missing).
    /// </summary>
    private async Task BringChatToFrontAsync()
    {
        if ((_chatTabs.FirstOrDefault(e => ReferenceEquals(e.Chat, ViewModel.Chat)) ?? _chatTabs.LastOrDefault()) is { } existing)
        {
            ShowChatTab(existing.Chat);
            return;
        }

        await OpenChatOrOfferInstallAsync();
    }

    private void OnTitleBarLeftPanelToggled(object? sender, EventArgs e) => ToggleLeftPanel();

    private void ToggleLeftPanel()
    {
        var visible = LeftPanel.Visibility == Visibility.Visible;
        LeftPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        LeftSplitter.Visibility = LeftPanel.Visibility;

        // The column keeps an explicit width (the splitter writes pixel widths as
        // the panel is dragged), so it stays put when the pivot is hidden; zero it
        // and restore what it was.
        if (visible)
        {
            _leftPanelWidth = LeftColumn.Width;
            // MinWidth would otherwise keep reserving space at zero width.
            LeftColumn.MinWidth = 0;
            LeftColumn.Width = new GridLength(0);
        }
        else
        {
            LeftColumn.MinWidth = 200;
            LeftColumn.Width = _leftPanelWidth;
        }

        // The centre card's left margin only exists next to the splitter; when the
        // panel is gone it needs its own gap from the window edge.
        CentreCard.Margin = visible ? new Thickness(0) : new Thickness(6, 0, 0, 0);
    }

    /// <summary>Runs the window-wide keyboard shortcut the user pressed (chords come from Settings).</summary>
    private void OnShortcutInvoked(object? sender, ShortcutEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        e.Handled = true;
        switch (e.ActionId)
        {
            case KeyboardShortcuts.OpenTerminal:
                OpenTerminalTab();
                break;
            case KeyboardShortcuts.ToggleLeftPanel:
                ToggleLeftPanel();
                break;
            case KeyboardShortcuts.NewFile:
                OpenNewFileTab();
                break;
            case KeyboardShortcuts.OpenChat:
                RunShortcut(OpenChatAndFocusAsync);
                break;
            case KeyboardShortcuts.ToggleCommands:
                ToggleCommandsFlyout();
                break;
            case KeyboardShortcuts.ToggleRightPanel:
                RunShortcut(ToggleRightPanelAndChatAsync);
                break;
            case KeyboardShortcuts.Search:
                FocusSearchFromShortcut();
                break;
            case KeyboardShortcuts.OpenBrowser:
                ViewModel.McpTools.OpenBrowserCommand.Execute(null);
                break;
            case KeyboardShortcuts.CycleMode:
                RunShortcut(CycleChatModeAsync);
                break;
            case KeyboardShortcuts.ShowGit:
                ShowGitPanel();
                break;
            case KeyboardShortcuts.StopChat:
                (ChatTabInFront()?.Chat ?? ViewModel.Chat).InterruptCommand.Execute(null);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    /// <summary>Starts an async shortcut; its failure is logged, not thrown into the key event.</summary>
    private static async void RunShortcut(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            CrashLog.Write("WorkspacePage.Shortcut", ex.Message, ex);
        }
    }

    /// <summary>Chat in front: focus its prompt. Otherwise bring a chat up first, then focus.</summary>
    private async Task OpenChatAndFocusAsync()
    {
        if (ChatTabInFront() is null)
        {
            await BringChatToFrontAsync();
        }

        ChatTabInFront()?.Tab.FocusDraft();
    }

    /// <summary>Toggles the session panel (bringing the chat up if it is not in front); an opened panel gets the prompt's focus.</summary>
    private async Task ToggleRightPanelAndChatAsync()
    {
        await ToggleSessionPanelAsync();

        if (_sessionPanelOpen)
        {
            ChatTabInFront()?.Tab.FocusDraft();
        }
    }

    /// <summary>
    /// Ctrl+F: with an editor in front the title bar box searches that file; otherwise
    /// the Search tab opens and the box takes the project query.
    /// </summary>
    private void FocusSearchFromShortcut()
    {
        var window = App.Current.MainWindow;
        if (window is null)
        {
            return;
        }

        if (_editorTabs.FirstOrDefault(t => ReferenceEquals(t.Item, CentreTabs.SelectedItem)) is { } editor)
        {
            window.FocusSearch(editor.Tab.ViewModel.FileName ?? "this file");
            return;
        }

        OpenSearchTab();

        // After the tab switch settles, or the new tab's content takes the focus back.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => window.FocusSearch());
    }

    /// <summary>The mode chip's order; the flyout lists them the same way.</summary>
    private static readonly string[] ModeCycle =
        [ChatViewModel.AutomaticMode, ChatViewModel.AskMode, "plan", "manual", "acceptEdits", "auto"];

    private async Task CycleChatModeAsync()
    {
        var chat = ViewModel.Chat;
        if (!chat.CanChangeMode)
        {
            return;
        }

        // The CLI's older names for manual and full sit at the same stops.
        var current = chat.PermissionMode switch
        {
            "default" => "manual",
            "dontAsk" or "bypassPermissions" => "auto",
            var mode => mode,
        };

        await chat.SetPermissionModeAsync(ModeCycle[(Array.IndexOf(ModeCycle, current) + 1) % ModeCycle.Length]);
    }

    private void ShowGitPanel()
    {
        if (LeftPanel.Visibility != Visibility.Visible)
        {
            ToggleLeftPanel();
        }

        if (LeftPanel.Items.OfType<PivotItem>().FirstOrDefault(item => item.Header as string == "Git") is { } git)
        {
            LeftPanel.SelectedItem = git;
        }
    }

    private void OnTitleBarSearchTyping(object? sender, bool inFile) => ViewModel.Search.Prewarm(inFile);

    private async void OnTitleBarSearchSubmitted(object? sender, SearchSubmission submission)
    {
        var query = submission.Query;

        // Scoped with Ctrl+F: the editor in front marks what in its file relates to the
        // query, and keeps focus so F3 and Esc work straight away.
        if (submission.InFile &&
            _editorTabs.FirstOrDefault(t => ReferenceEquals(t.Item, CentreTabs.SelectedItem)) is { } editor &&
            !string.IsNullOrWhiteSpace(query))
        {
            editor.Tab.FocusText();

            try
            {
                await editor.Tab.FocusOnQueryAsync(query, ViewModel.Search.ConnectModelAsync);
            }
            catch (Exception ex)
            {
                // async void: an escaping exception would take the process down.
                CrashLog.Write("WorkspacePage.FocusOnQuery", ex.Message, ex);
            }

            return;
        }

        OpenSearchTab();
        ViewModel.Search.Query = query;

        if (ViewModel.Search.RunCommand.CanExecute(null))
        {
            ViewModel.Search.RunCommand.Execute(null);
        }
    }

    private void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is FileNode node)
        {
            node.LoadChildren();

            // Freshly loaded children need the git colours the rest of the tree has.
            ViewModel.Files.RefreshGitStatus();
        }
    }

    /// <summary>
    /// The file tree's right-click menu: the actions that make a tree worth having.
    /// Building it per tap keeps the contents honest - a folder offers creation, a
    /// file offers chat, clipboard and lifecycle entries.
    /// </summary>
    private void OnTreeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Right-clicking empty space acts on the project root.
        var hit = (e.OriginalSource as FrameworkElement)?.DataContext as FileNode;
        if (hit is { IsPlaceholder: true })
        {
            return;
        }

        var isRoot = hit is null || ReferenceEquals(hit, ViewModel.Files.Root);
        var node = hit ?? ViewModel.Files.Root;
        var menu = new MenuFlyout();

        if (node.IsDirectory)
        {
            menu.Items.Add(MenuAction("New file", () => _ = CreateChildAsync(node, file: true), ""));
            menu.Items.Add(MenuAction("New folder", () => _ = CreateChildAsync(node, file: false), ""));
        }
        else
        {
            menu.Items.Add(MenuAction("Open", () => ShowFile(node.FullPath), ""));

            // A modified tracked file can go straight to the diff instead of the editor.
            if (node.GitStatus is GitChangeKind.Modified or GitChangeKind.Renamed or GitChangeKind.Copied)
            {
                menu.Items.Add(MenuAction("View diff", () => _ = ShowNodeDiffAsync(node), ""));
            }

            menu.Items.Add(MenuAction("Open with default app", () => _ = OpenWithDefaultAppAsync(node), ""));
            menu.Items.Add(MenuAction("Add to chat", () => AddFileToChat(node), ""));

            if (TurnAttachment.KindOf(node.FullPath) is not null)
            {
                menu.Items.Add(MenuAction("Attach to chat", () => ViewModel.Chat.TryAddAttachment(node.FullPath), ""));
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var isFolderRoot = node.IsWorkspaceRoot;

        if (!isRoot && !isFolderRoot)
        {
            menu.Items.Add(MenuAction("Cut", () => CopyToClipboard(node, move: true), ""));
            menu.Items.Add(MenuAction("Copy", () => CopyToClipboard(node, move: false), ""));
        }

        var paste = MenuAction("Paste", () => _ = PasteAsync(node), "");
        paste.IsEnabled = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()
            .Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);
        menu.Items.Add(paste);
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(MenuAction("Copy full path", () => CopyText(node.FullPath)));
        menu.Items.Add(MenuAction("Copy relative path", () => CopyText(isRoot ? "." : RelativeToProject(node.FullPath))));

        if (!isRoot)
        {
            menu.Items.Add(MenuAction("Copy name", () => CopyText(node.Name)));
            menu.Items.Add(new MenuFlyoutSeparator());

            if (isFolderRoot)
            {
                menu.Items.Add(MenuAction("Remove from workspace", () => ViewModel.ExtraFolders.Remove(node.FullPath), ""));
            }
            else
            {
                menu.Items.Add(MenuAction("Rename…", () => _ = RenameAsync(node), ""));
                menu.Items.Add(MenuAction("Delete", () => _ = DeleteAsync(node), ""));
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuAction("Add folder to workspace…", () => _ = AddWorkspaceFolderAsync(), ""));
        menu.Items.Add(MenuAction("Refresh", AfterTreeMutation, ""));
        menu.Items.Add(MenuAction("Reveal in File Explorer", () => RevealInExplorer(node), ""));

        menu.ShowAt(
            (FrameworkElement)sender,
            new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = e.GetPosition((FrameworkElement)sender),
            });
        e.Handled = true;
    }

    /// <summary>
    /// Adds a folder beside the project root (e.g. the backend of the frontend that is open).
    /// The agent is granted it with --add-dir, so a session started afterwards may work there.
    /// </summary>
    private async Task AddWorkspaceFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");

        if (App.Current.MainWindowHandle is { } hwnd)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            ViewModel.ExtraFolders.Add(folder.Path);
        }
    }

    private static async Task OpenWithDefaultAppAsync(FileNode node)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(node.FullPath);
        await Windows.System.Launcher.LaunchFileAsync(file);
    }

    /// <summary>Puts the node on the system clipboard as a storage item, so Explorer can paste it too.</summary>
    private async void CopyToClipboard(FileNode node, bool move)
    {
        try
        {
            Windows.Storage.IStorageItem item = node.IsDirectory
                ? await Windows.Storage.StorageFolder.GetFolderFromPathAsync(node.FullPath)
                : await Windows.Storage.StorageFile.GetFileFromPathAsync(node.FullPath);

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage
            {
                RequestedOperation = move
                    ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move
                    : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
            };
            package.SetStorageItems([item], readOnly: false);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            await ShowTreeErrorAsync($"Could not {(move ? "cut" : "copy")} {node.Name}", ex.Message);
        }
    }

    private async Task PasteAsync(FileNode target)
    {
        var directory = target.IsDirectory
            ? target.FullPath
            : Path.GetDirectoryName(target.FullPath) ?? ViewModel.ProjectPath;

        try
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            var move = view.RequestedOperation.HasFlag(Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move);
            var items = await view.GetStorageItemsAsync();

            foreach (var item in items)
            {
                var isFolder = item is Windows.Storage.StorageFolder;
                var destination = Path.Combine(directory, item.Name);

                if (move && string.Equals(destination, item.Path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (isFolder && IsInside(directory, item.Path))
                {
                    throw new IOException("A folder cannot be pasted into itself.");
                }

                destination = UniquePath(destination, isFolder);

                if (isFolder && move)
                {
                    Directory.Move(item.Path, destination);
                }
                else if (isFolder)
                {
                    CopyDirectory(item.Path, destination);
                }
                else if (move)
                {
                    File.Move(item.Path, destination);
                }
                else
                {
                    File.Copy(item.Path, destination);
                }
            }

            if (move)
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.Clear();
            }

            AfterTreeMutation();
        }
        catch (Exception ex)
        {
            await ShowTreeErrorAsync("Could not paste", ex.Message);
        }
    }

    /// <summary>True when <paramref name="path"/> is the folder itself or lies beneath it.</summary>
    private static bool IsInside(string path, string folder)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string UniquePath(string path, bool isFolder)
    {
        static bool Taken(string p) => File.Exists(p) || Directory.Exists(p);

        if (!Taken(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var stem = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isFolder ? "" : Path.GetExtension(path);

        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} - Copy{(n > 1 ? $" ({n})" : "")}{ext}");
            if (!Taken(candidate))
            {
                return candidate;
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private MenuFlyoutItem MenuAction(string text, Action action, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (glyph is not null)
        {
            item.Icon = new FontIcon { Glyph = glyph };
        }
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>The @-reference the composer's own autocomplete would have inserted.</summary>
    private string RelativeToProject(string path) =>
        Path.GetRelativePath(ViewModel.ProjectPath, path).Replace('\\', '/');

    private void AddFileToChat(FileNode node)
    {
        var reference = $"@{RelativeToProject(node.FullPath)} ";
        ViewModel.Chat.Draft = ViewModel.Chat.Draft is { Length: > 0 } draft ? draft + reference : reference;
    }

    private void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
        };
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void RevealInExplorer(FileNode node) =>
        _ = Windows.System.Launcher.LaunchFolderPathAsync(
            node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath) ?? ViewModel.ProjectPath);

    private async Task CreateChildAsync(FileNode parent, bool file)
    {
        var name = await PromptTextAsync(file ? "New file" : "New folder", $"Name for the new {(file ? "file" : "folder")} in {parent.Name}");
        if (name is null)
        {
            return;
        }

        var path = Path.Combine(parent.FullPath, name);

        try
        {
            if (file)
            {
                File.WriteAllText(path, "");
            }
            else
            {
                Directory.CreateDirectory(path);
            }

            AfterTreeMutation();
        }
        catch (Exception ex)
        {
            await ShowTreeErrorAsync($"Could not create {name}", ex.Message);
        }
    }

    private async Task RenameAsync(FileNode node)
    {
        var name = await PromptTextAsync($"Rename {node.Name}", "New name", node.Name);
        if (name is null)
        {
            return;
        }

        var target = Path.Combine(Path.GetDirectoryName(node.FullPath) ?? ViewModel.ProjectPath, name);

        try
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                await ShowTreeErrorAsync($"Could not rename {node.Name}", $"{name} already exists.");
                return;
            }

            if (node.IsDirectory)
            {
                Directory.Move(node.FullPath, target);
            }
            else
            {
                File.Move(node.FullPath, target);
            }

            AfterTreeMutation();
        }
        catch (Exception ex)
        {
            await ShowTreeErrorAsync($"Could not rename {node.Name}", ex.Message);
        }
    }

    private async Task DeleteAsync(FileNode node)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete {node.Name}?",
            Content = node.IsDirectory
                ? "The folder and everything in it will be removed."
                : "This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (node.IsDirectory)
            {
                Directory.Delete(node.FullPath, recursive: true);
            }
            else
            {
                File.Delete(node.FullPath);
            }

            AfterTreeMutation();
        }
        catch (Exception ex)
        {
            await ShowTreeErrorAsync($"Could not delete {node.Name}", ex.Message);
        }
    }

    /// <summary>The tree rescans from disk, and git re-colours what changed.</summary>
    private void AfterTreeMutation()
    {
        ViewModel.Files.Reload();
        _ = ViewModel.Git.RefreshAsync();
    }

    private async Task<string?> PromptTextAsync(string title, string placeholder, string initial = "")
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var name = box.Text.Trim();
        return name.Length == 0 ? null : name;
    }

    private async Task ShowTreeErrorAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        }.ShowAsync();
    }

    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // The template root is a TreeViewItem (needed so Children bind), so the invoked
        // item can be either the FileNode itself or that inner control. Resolve both
        // to a node plus the TreeViewItem that actually expands.
        TreeViewItem? container;
        FileNode? node;

        switch (args.InvokedItem)
        {
            case FileNode direct:
                node = direct;
                container = sender.ContainerFromItem(direct) as TreeViewItem;
                break;
            case TreeViewItem inner when inner.DataContext is FileNode fromInner:
                node = fromInner;
                container = inner;
                break;
            default:
                return;
        }

        if (node.IsPlaceholder)
        {
            return;
        }

        if (node.IsDirectory)
        {
            if (container is not null)
            {
                container.IsExpanded = !container.IsExpanded;
            }
        }
        else
        {
            ShowFile(node.FullPath);
        }
    }

    private void OnCliEndpointFlyoutOpening(object sender, object e) =>
        ViewModel.CliEndpoint.Reload();

    private void OnOpenProviderSettingsClick(object sender, RoutedEventArgs e) =>
        App.Current.MainWindow?.SetSettingsOpen(true);

    private void OnSessionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionListItem session &&
            ViewModel.Sessions.OpenCommand.CanExecute(session))
        {
            ViewModel.Sessions.OpenCommand.Execute(session);
        }
    }

    private async void OnCopySessionHandoutClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session)
        {
            return;
        }

        try
        {
            CopyText(await ViewModel.Sessions.BuildHandoutAsync(session));
        }
        catch (Exception ex)
        {
            await ShowTreeErrorAsync("Could not create handout", ex.Message);
        }
    }

    private void OnCopySessionIdClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionListItem session)
        {
            CopyText(session.SessionId);
        }
    }

    /// <summary>
    /// Fork session: summarizes the session (on its own provider, cheaply), lets the user
    /// read and edit the briefing, then starts a new session that carries it.
    /// </summary>
    private async void OnForkSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session)
        {
            return;
        }

        var ring = new ProgressRing { Width = 18, Height = 18, IsActive = true };
        var status = new TextBlock
        {
            Text = "Summarizing the session…",
            VerticalAlignment = VerticalAlignment.Center,
        };

        var progress = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { ring, status } };
        var summaryBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 220,
            MaxHeight = 360,
            Header = "Briefing the new session starts with (editable)",
            Visibility = Visibility.Collapsed,
        };

        ScrollViewer.SetVerticalScrollBarVisibility(summaryBox, ScrollBarVisibility.Auto);

        var hint = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            Text = "Written on a small model in a throwaway session, so the original is untouched. " +
                   "The briefing goes out with your first message in the new session.",
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Fork “{session.Title}”",
            Content = new StackPanel { Width = 520, Spacing = 12, Children = { progress, summaryBox, hint } },
            PrimaryButtonText = "Start forked session",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        using var cts = new CancellationTokenSource();
        dialog.Closing += (_, _) => cts.Cancel();

        async Task SummarizeAsync()
        {
            try
            {
                var summary = await ViewModel.SummarizeSessionAsync(session, cts.Token);
                summaryBox.Text = summary;
                summaryBox.Visibility = Visibility.Visible;
                progress.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ring.IsActive = false;
                ring.Visibility = Visibility.Collapsed;
                status.Text = $"Could not summarize: {ex.Message}";
            }
        }

        dialog.Opened += (_, _) => _ = SummarizeAsync();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(summaryBox.Text))
        {
            return;
        }

        try
        {
            await ViewModel.ForkSessionAsync(session, summaryBox.Text.Trim());
        }
        catch (Exception ex)
        {
            await ShowTreeErrorAsync("Could not fork session", ex.Message);
        }
    }

    private async void OnCompactSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session)
        {
            return;
        }

        try
        {
            await ViewModel.CompactSessionAsync(session.Summary);
        }
        catch (Exception ex)
        {
            await ShowTreeErrorAsync("Could not compact session", ex.Message);
        }
    }

    private async void OnRetitleSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session)
        {
            return;
        }

        var input = new TextBox
        {
            Text = session.Title,
            PlaceholderText = "Give this session a short name",
            MaxLength = 80,
            SelectionStart = 0,
            SelectionLength = session.Title.Length,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var generateIcon = new FontIcon { Glyph = "\uE945", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var generateRing = new ProgressRing { Width = 14, Height = 14, IsActive = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var generate = new Button
        {
            Content = new Grid { Children = { generateIcon, generateRing } },
            Width = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(generate, "Auto-generate title");

        var caption = new TextBlock
        {
            Text = "TITLE",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 60,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var fieldRow = new Grid { ColumnSpacing = 8 };
        fieldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fieldRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(generate, 1);
        fieldRow.Children.Add(input);
        fieldRow.Children.Add(generate);

        var hint = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            Text = "Stored on this device. Leave it empty to go back to the automatic name.",
        };

        using var cts = new CancellationTokenSource();
        generate.Click += async (_, _) =>
        {
            generate.IsEnabled = false;
            generateIcon.Visibility = Visibility.Collapsed;
            generateRing.Visibility = Visibility.Visible;

            try
            {
                var suggestion = await ViewModel.SuggestTitleAsync(session, cts.Token);
                if (suggestion is { Length: > 0 })
                {
                    input.Text = suggestion;
                    input.SelectAll();
                    input.Focus(FocusState.Programmatic);
                }
                else
                {
                    hint.Text = "This session has no prompt to name it from.";
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                generateRing.Visibility = Visibility.Collapsed;
                generateIcon.Visibility = Visibility.Visible;
                generate.IsEnabled = true;
            }
        };

        var panel = new StackPanel { Width = 380, Spacing = 10 };
        panel.Children.Add(caption);
        panel.Children.Add(fieldRow);
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        // No stock title header - the small caption carries the purpose - so the
        // dialog padding is tightened to read as a compact form, not a page.
        dialog.Resources["ContentDialogPadding"] = new Thickness(24, 18, 24, 18);

        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        dialog.Closing += (_, _) => cts.Cancel();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ViewModel.Sessions.RetitleAsync(session, input.Text);
        }
        catch (Exception ex)
        {
            await ShowTreeErrorAsync("Could not retitle session", ex.Message);
        }
    }

    private async void OnDeleteSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session)
        {
            return;
        }

        if (session.IsOpen)
        {
            await ShowTreeErrorAsync("Session is open", "Close its tab before deleting it.");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete \"{session.Title}\"?",
            Content = "The conversation's transcript will be removed. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ViewModel.Sessions.DeleteAsync(session);
        }
        catch (Exception ex)
        {
            await ShowTreeErrorAsync("Could not delete session", ex.Message);
        }
    }

    private void OnReturnToLiveClick(object sender, RoutedEventArgs e) =>
        ViewModel.ReturnToLiveSession();

    /// <summary>Picks a branch from the flyout; the flyout only closes once git accepts.</summary>
    private async void OnBranchClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GitBranch branch)
        {
            return;
        }

        if (await ViewModel.Git.SwitchBranchAsync(branch) && BranchButton.Flyout is { } flyout)
        {
            flyout.Hide();
        }
    }

    /// <summary>A fresh opening should not show a failure from an earlier attempt.</summary>
    private void OnBranchFlyoutOpening(object? sender, object e) =>
        ViewModel.Git.ActionError = null;

    private async void OnCreateBranchClick(object sender, RoutedEventArgs e) => await CreateBranchAsync();

    private async void OnNewBranchBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await CreateBranchAsync();
        }
    }

    private async Task CreateBranchAsync()
    {
        if (await ViewModel.Git.CreateBranchAsync(NewBranchBox.Text) && BranchButton.Flyout is { } flyout)
        {
            NewBranchBox.Text = "";
            flyout.Hide();
        }
    }

    /// <summary>Ctrl+Enter in the message box commits, the way a chat draft sends.</summary>
    private void OnCommitAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (ViewModel.Git.CommitCommand.CanExecute(null))
        {
            ViewModel.Git.CommitCommand.Execute(null);
        }
    }

    /// <summary>
    /// The change list's context menu is one shared flyout; remember which row opened it,
    /// since the template's bindings reach the data, not the view model.
    /// </summary>
    private void OnChangeMenuOpening(object? sender, object e)
    {
        _changeMenuItem = (sender as MenuFlyout)?.Target is FrameworkElement { DataContext: GitFileStatus change }
            ? change
            : ViewModel.Git.SelectedChange;

        // Folder ignores only make sense for a nested path: "folder" is the entry's own
        // directory, "root folder" the top-level one, hidden when that is the same thing.
        var path = _changeMenuItem?.Path;
        var firstSlash = path?.IndexOf('/') ?? -1;
        IgnoreFolderItem.Visibility = firstSlash > 0 ? Visibility.Visible : Visibility.Collapsed;
        var rootSlash = path is null ? -1 : path.IndexOf('/', firstSlash + 1);
        IgnoreRootFolderItem.Visibility = rootSlash > firstSlash ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMenuViewDiff(object sender, RoutedEventArgs e)
    {
        if (_changeMenuItem is { } change)
        {
            _ = ShowChangeDiffAsync(change);
        }
    }

    private void OnMenuStage(object sender, RoutedEventArgs e)
    {
        if (_changeMenuItem is { } change &&
            ViewModel.Git.StageChangeCommand.CanExecute(change))
        {
            ViewModel.Git.StageChangeCommand.Execute(change);
        }
    }

    private void OnMenuUnstage(object sender, RoutedEventArgs e)
    {
        if (_changeMenuItem is { } change &&
            ViewModel.Git.UnstageChangeCommand.CanExecute(change))
        {
            ViewModel.Git.UnstageChangeCommand.Execute(change);
        }
    }

    /// <summary>Discard deletes untracked files outright, so it asks first.</summary>
    private async void OnMenuDiscard(object sender, RoutedEventArgs e)
    {
        if (_changeMenuItem is not { } change)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Discard changes?",
            Content = $"Throw away every uncommitted change to {change.FileName}? Untracked files are deleted for good.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Keep",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.Git.DiscardChangeAsync(change);
        }
    }

    private async void OnMenuIgnore(object sender, RoutedEventArgs e)
    {
        if (_changeMenuItem is { } change)
        {
            await ViewModel.Git.IgnoreChangeAsync(change);
        }
    }

    private async void OnMenuIgnoreFolder(object sender, RoutedEventArgs e)
    {
        if (_changeMenuItem is { } change && change.Directory is { } folder)
        {
            await ViewModel.Git.IgnoreFolderAsync(change, $"{folder}/");
        }
    }

    private async void OnMenuIgnoreRootFolder(object sender, RoutedEventArgs e)
    {
        if (_changeMenuItem is { } change)
        {
            var slash = change.Path.IndexOf('/');
            if (slash > 0)
            {
                await ViewModel.Git.IgnoreFolderAsync(change, $"{change.Path[..(slash + 1)]}");
            }
        }
    }

    /// <summary>
    /// Off to on starts an isolated session. On to off is never silent: the worktree's
    /// work is merged back into the project, discarded, or the toggle stays on. While
    /// an isolated turn is running it is left alone and another isolated session opens.
    /// </summary>
    private async void OnIsolatedSessionClick(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleButton)sender;

        if (!ViewModel.IsIsolated || ViewModel.Chat.IsBusy)
        {
            await ViewModel.StartIsolatedSessionAsync();
            toggle.IsChecked = ViewModel.IsIsolated;
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Leave the isolated session?",
            Content = "Merge its branch back into your project, or discard the work done in the worktree. " +
                      "Either way this chat starts a fresh session in the project.",
            PrimaryButtonText = "Merge back",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Stay isolated",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await dialog.ShowAsync();
        if (choice != ContentDialogResult.None)
        {
            await ViewModel.LeaveIsolationAsync(merge: choice == ContentDialogResult.Primary);
        }

        toggle.IsChecked = ViewModel.IsIsolated;
    }

    /// <summary>
    /// The model chip: one flyout, one tap. Default first - named, so it is clear what
    /// "default" means - then the agent's catalogue (claude answers
    /// list_models or falls back to its documented family). A pick applies immediately
    /// and closes the flyout.
    /// </summary>
    private void OnModelChipClick(object sender, RoutedEventArgs e)
    {
        var chat = ViewModel.Chat;
        var requested = chat.RequestedModel;
        IReadOnlyList<AgentModelInfo> catalogue = chat.AvailableModels.Count > 0
            ? chat.AvailableModels
            : ClaudeAgentSession.DocumentedModels;

        var panel = new StackPanel { Spacing = 2, MinWidth = 300 };
        var flyout = PickerFlyout(panel);

        panel.Children.Add(PickerTitle("Model"));

        var defaultName = chat.DefaultModel is { } fallback ? chat.ModelName(fallback.Id) : null;
        panel.Children.Add(PickOption(
            OptionContent("\uE734", defaultName is null ? "Default" : $"Default · {defaultName}", $"Whatever {chat.ProviderName} starts with"),
            requested is null, "model",
            () => _ = ViewModel.ApplySessionConfigAsync(null, chat.RequestedEffort), flyout));

        foreach (var model in catalogue)
        {
            var id = model.Id;
            var name = chat.ModelName(id);
            var detail = model.Description is { Length: > 0 } d ? d : id != name ? id : null;

            panel.Children.Add(PickOption(
                OptionContent("\uE99A", name, detail),
                string.Equals(requested, id, StringComparison.OrdinalIgnoreCase),
                "model",
                () => _ = ViewModel.ApplySessionConfigAsync(id, chat.RequestedEffort),
                flyout));
        }

        panel.Children.Add(PickerHint());

        flyout.ShowAt((FrameworkElement)sender);
    }

    /// <summary>
    /// The mode chip: how much the agent may do. Auto lets the helper model pick
    /// per message, Ask explains and changes nothing, Planning researches and proposes,
    /// Manual asks on every action, Accept edits auto-approves file writes, Full
    /// runs without asking.
    /// </summary>
    private void OnModeChipClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Chat.CanChangeMode)
        {
            return;
        }

        var panel = new StackPanel { Spacing = 2, MinWidth = 300 };
        var flyout = PickerFlyout(panel);
        var current = ViewModel.Chat.PermissionMode;

        void Add(string mode, string glyph, string title, string detail, bool isCurrent) =>
            panel.Children.Add(PickOption(OptionContent(glyph, title, detail), isCurrent, "mode",
                () => _ = ViewModel.Chat.SetPermissionModeAsync(mode), flyout));

        panel.Children.Add(PickerTitle("Mode"));
        Add(ChatViewModel.AutomaticMode, "\uE82F", "Auto",
            ViewModel.Chat.Router?.IsAvailable == true
                ? "Picks the right mode per message"
                : "Needs the Claude CLI to pick modes",
            current == ChatViewModel.AutomaticMode);
        Add(ChatViewModel.AskMode, "\uE8BD", "Ask", "Explains in detail, never edits files",
            current == ChatViewModel.AskMode);
        Add("plan", "\uE707", "Planning", "Researches first, proposes a plan to approve",
            current == "plan");
        Add("manual", "\uE7C9", "Manual", "Asks before every action",
            current is "manual" or "default");
        Add("acceptEdits", "\uE70F", "Accept edits", "Edits files freely, asks for the rest",
            current == "acceptEdits");
        Add("auto", "\uE945", "Full", "Works without asking",
            current is "auto" or "dontAsk" or "bypassPermissions");
        panel.Children.Add(PickerHint());

        flyout.ShowAt((FrameworkElement)sender);
    }

    /// <summary>
    /// The effort chip: the same single-tap flyout as the model chip. A model that
    /// advertises its own efforts decides the list; otherwise Claude's documented vocabulary.
    /// </summary>
    private void OnEffortChipClick(object sender, RoutedEventArgs e)
    {
        var chat = ViewModel.Chat;
        var requested = chat.RequestedEffort;
        var efforts = chat.LiveEffortChoices
            ?? ["low", "medium", "high", "xhigh", "max"];

        var panel = new StackPanel { Spacing = 2, MinWidth = 260 };
        var flyout = PickerFlyout(panel);

        panel.Children.Add(PickerTitle("Effort"));
        panel.Children.Add(PickOption(
            OptionContent("\uE734", "CLI default", "What the CLI uses unless told otherwise"),
            requested is null, "effort",
            () => _ = ViewModel.ApplySessionConfigAsync(chat.RequestedModel, null), flyout));

        foreach (var effort in efforts)
        {
            panel.Children.Add(PickOption(
                OptionContent(EffortGlyph(effort), ChatViewModel.EffortName(effort), EffortDetail(effort)),
                requested == effort, "effort",
                () => _ = ViewModel.ApplySessionConfigAsync(chat.RequestedModel, effort), flyout));
        }

        panel.Children.Add(PickerHint());
        flyout.ShowAt((FrameworkElement)sender);
    }

    /// <summary>A bar-chart ladder: more bars, more thinking.</summary>
    private static string EffortGlyph(string effort) => effort switch
    {
        "minimal" => "\uE904",
        "low" => "\uE905",
        "medium" => "\uE906",
        "high" => "\uE907",
        _ => "\uE908",
    };

    private static string? EffortDetail(string effort) => effort switch
    {
        "minimal" => "Barely reasons, fastest replies",
        "low" => "Quick replies for simple asks",
        "medium" => "Balanced speed and depth",
        "high" => "Thinks longer on hard problems",
        "xhigh" => "Deep reasoning for tricky work",
        "max" => "Longest thinking; restarts the session",
        _ => null,
    };

    /// <summary>
    /// A picker row's content: an icon, the choice's name, and a quieter line saying
    /// what it does. Secondary text dims by opacity, which reads right in both themes.
    /// </summary>
    private static Grid OptionContent(string glyph, string title, string? detail)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon { Glyph = glyph, FontSize = 14, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (detail is { Length: > 0 })
        {
            text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>A flyout in the shared popup style: floating card, rounded, hairline stroke.</summary>
    private Flyout PickerFlyout(UIElement content) => new()
    {
        Content = content,
        FlyoutPresenterStyle = (Style)Resources["PickerFlyout"],
    };

    /// <summary>The small quiet heading every picker popup shares.</summary>
    private TextBlock PickerTitle(string text) =>
        new() { Text = text, Style = (Style)Resources["PickerTitleText"] };

    private TextBlock PickerHint() =>
        new() { Text = "Applies without restarting the conversation.", Style = (Style)Resources["PickerHintText"] };

    /// <summary>
    /// One choice in a picker flyout: a full-width row with a trailing check, the
    /// shared <c>PickerRow</c> style. Checking it applies and the flyout closes;
    /// tapping the already-checked one simply dismisses. The Checked subscription
    /// comes after IsChecked is set, so building the list never applies anything.
    /// </summary>
    private RadioButton PickOption(object content, bool isChecked, string group, Action apply, Flyout flyout)
    {
        var radio = new RadioButton
        {
            Style = (Style)Resources["PickerRow"],
            GroupName = group,
            Content = content,
            IsChecked = isChecked,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        radio.Click += (_, _) => flyout.Hide();
        radio.Checked += (_, _) => apply();
        return radio;
    }

    /// <summary>New session in the project directory - no worktree, no branch, fresh transcript.</summary>
    private void OnNewSessionClick(object sender, RoutedEventArgs e) =>
        _ = ViewModel.StartNewChatSessionAsync();

    /// <summary>One open chat tab: its <see cref="TabViewItem"/>, its transcript view and its conversation.</summary>
    private sealed class ChatTabEntry(TabViewItem item, ChatTab tab, ChatViewModel chat)
    {
        public TabViewItem Item { get; } = item;

        public ChatTab Tab { get; } = tab;

        public ChatViewModel Chat { get; } = chat;

        /// <summary>The running "waiting for you" pulse on the tab's background, or null when it is calm.</summary>
        public Storyboard? AttentionPulse { get; set; }
    }

    /// <summary>
    /// One open file tab: its <see cref="TabViewItem"/> and its editor surface with
    /// its own view model. Preview state lives on the view model, where the header
    /// bindings can see it.
    /// </summary>
    private sealed class EditorTabEntry(TabViewItem item, EditorTab tab)
    {
        public TabViewItem Item { get; } = item;

        public EditorTab Tab { get; } = tab;
    }

    /// <summary>
    /// One open terminal tab: its <see cref="TabViewItem"/>, its display surface and
    /// the shell view model whose lifetime matches the tab.
    /// </summary>
    private sealed class TerminalTabEntry(TabViewItem item, TerminalTab tab, TerminalViewModel terminal)
    {
        public TabViewItem Item { get; } = item;

        public TerminalTab Tab { get; } = tab;

        public TerminalViewModel Terminal { get; } = terminal;
    }
}




