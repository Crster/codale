using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    private GitCommit? _commitMenuItem;

    /// <summary>Every open terminal tab; each one has its own shell.</summary>
    private readonly List<TerminalTabEntry> _terminalTabs = [];

    /// <summary>Every open file tab, preview and pinned alike.</summary>
    private readonly List<EditorTabEntry> _editorTabs = [];

    /// <summary>The left panel's column width, kept while the panel is hidden so it can come back as it was.</summary>
    private GridLength _leftPanelWidth = new(260);

    /// <summary>The one tab a file click may replace; null once its file is edited.</summary>
    private EditorTabEntry? _previewTab;

    private readonly PointerEventHandler _outsidePointerPressedHandler;

    public WorkspacePage()
    {
        InitializeComponent();
        _outsidePointerPressedHandler = new PointerEventHandler(OnPointerPressedOutsideDrawer);
        AddHandler(PointerPressedEvent, _outsidePointerPressedHandler, handledEventsToo: true);
        Loaded += (_, _) => MarkdownView.FileLinkClicked += OnMarkdownFileLink;
        Unloaded += (_, _) =>
        {
            MarkdownView.FileLinkClicked -= OnMarkdownFileLink;
            RemoveHandler(PointerPressedEvent, _outsidePointerPressedHandler);
        };
    }

    public WorkspaceViewModel ViewModel { get; private set; } = null!;

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
        ViewModel.HelperLoginRequired += OnChatLoginRequired;
        Bindings.Update();

        SetTerminalCwd(ViewModel.WorkingDirectory);

        UpdateEmptyState();

        if (App.Current.MainWindow is { } window)
        {
            window.CommandsRequested += OnTitleBarCommandsRequested;
            window.McpToolsRequested += OnTitleBarMcpToolsRequested;
            window.LeftPanelToggled += OnTitleBarLeftPanelToggled;
            window.SearchSubmitted += OnTitleBarSearchSubmitted;
            window.SearchTyping += OnTitleBarSearchTyping;
            window.ShortcutInvoked += OnShortcutInvoked;

            if (window.Content is UIElement root)
            {
                root.AddHandler(PointerPressedEvent, _outsidePointerPressedHandler, handledEventsToo: true);
            }
        }

        // The Settings window writes app-level preferences; this page applies them
        // live to the tabs it owns. All windows share one UI thread.
        AppSettings.Changed += OnAppSettingsChanged;

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
            window.LeftPanelToggled -= OnTitleBarLeftPanelToggled;
            window.SearchSubmitted -= OnTitleBarSearchSubmitted;
            window.SearchTyping -= OnTitleBarSearchTyping;
            window.ShortcutInvoked -= OnShortcutInvoked;
            window.Activated -= OnWindowActivated;

            if (window.Content is UIElement root)
            {
                root.RemoveHandler(PointerPressedEvent, _outsidePointerPressedHandler);
            }
        }

        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnWorkspacePropertyChanged;
            ViewModel.ChatActivationRequested -= OnChatActivationRequested;
            ViewModel.HelperLoginRequired -= OnChatLoginRequired;
            if (_planWatchedChat is not null)
            {
                _planWatchedChat.PlanReplaced -= OnPlanReplaced;
                _planWatchedChat = null;
            }

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

        // The status bar follows the chat in front; the session panel follows the agent session in front
        // (a chat tab's own, or the terminal CLI's), so it is chosen there.
        if (ChatTabInFront() is { } chatEntry)
        {
            ViewModel.Chat = chatEntry.Chat;
        }

        UpdateSessionPanelForActiveTab();

        if (active is not null)
        {
            _ = active.CheckAndUpdateIfModifiedAsync();
        }
    }

    private EditorTab? ActiveEditorTab() =>
        _editorTabs.FirstOrDefault(t => ReferenceEquals(t.Item, CentreTabs.SelectedItem))?.Tab;

    /// <summary>
    /// Shows the language colouring the file in front (or "No File", disabled, when the tab in front is not
    /// a saved file). A language picked by hand is marked so it is not mistaken for detection.
    /// </summary>
    private void UpdateLanguageButton()
    {
        if (ActiveEditorTab() is not { } tab || !tab.ViewModel.HasFile)
        {
            LanguageText.Text = "No File";
            LanguageButton.IsEnabled = false;
            return;
        }

        LanguageButton.IsEnabled = true;

        var name = SyntaxService.Catalog.ById(tab.LanguageId)?.Name ?? "Plain Text";
        LanguageText.Text = tab.HasManualLanguage ? $"{name} (manual)" : name;
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
                await OpenChatOrOfferInstallAsync("claude");
                break;
            case "codex":
                await OpenChatOrOfferInstallAsync("codex");
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
        ClaudeMenuItem.Text = CliLabel("claude");
        CodexMenuItem.Text = CliLabel("codex");
        ClaudeEmptyLabel.Text = CliLabel("claude");
        CodexEmptyLabel.Text = CliLabel("codex");
    }

    /// <summary>"Claude chat" when the CLI is on the PATH, "Install Claude…" when it is not.</summary>
    private static string CliLabel(string cliId) =>
        CliLocator.Find(cliId) is not null
            ? $"{CliLocator.KnownClis.First(cli => cli.Id == cliId).DisplayName} chat"
            : $"Install {CliLocator.KnownClis.First(cli => cli.Id == cliId).DisplayName}…";

    /// <summary>
    /// Opens the chat when the CLI exists; otherwise offers to install it, running the
    /// installer in a new terminal tab so the user sees its output.
    /// </summary>
    private async Task OpenChatOrOfferInstallAsync(string cliId)
    {
        var cli = CliLocator.KnownClis.First(known => known.Id == cliId);
        if (CliLocator.Find(cliId) is not null)
        {
            await OpenChatTabAsync(cliId);
            return;
        }

        var command = cli.InstallCommand;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = Codale.App.Controls.DialogTheme.Of(XamlRoot),
            Title = $"{cli.DisplayName} not found",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = command is { Length: > 0 }
                    ? $"The '{cli.Executable}' command isn't on this system's PATH. " +
                      $"Install it now? This runs the following in a new terminal:\n\n{command}"
                    : $"The '{cli.Executable}' command isn't on this system's PATH. Install the {cli.DisplayName} CLI, then try again.",
            },
            PrimaryButtonText = command is { Length: > 0 } ? "Install" : "Close",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || command is not { Length: > 0 })
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
    private void OnChatLoginRequired(object? sender, string? cliName) =>
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_loginPromptOpen)
            {
                return;
            }

            var isCodex = string.Equals(cliName, "Codex", StringComparison.OrdinalIgnoreCase) ||
                          (sender is ChatViewModel { SessionCli.Id: "codex" });
            var targetCli = isCodex ? "Codex" : "Claude";

            // Codale keeps its own login, apart from the CLI's default one: the login runs
            // against Codale's folder, so a terminal's claude/codex stays on its own account.
            var command = CodaleAccountCli.LoginCommand(isCodex ? "codex" : "claude");
            var description = isCodex
                ? "Open a terminal to log in? It runs 'codex login' for Codale's own Codex account, separate from the codex you run in a terminal."
                : "Open a terminal to log in? It runs 'claude auth login' for Codale's own Claude account, separate from the claude you run in a terminal.";

            _loginPromptOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = Codale.App.Controls.DialogTheme.Of(XamlRoot),
                    Title = $"Codale isn't signed in to {targetCli}",
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = description,
                    },
                    PrimaryButtonText = "Open terminal",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await RunInNewTerminalAsync(command);
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
    private async Task OpenChatTabAsync(string cliId = "claude")
    {
        CrashLog.Trace("Chat tab open requested");

        var chat = ViewModel.FreshChat(cliId);

        // The picked CLI names the session: it lands before the first connect, and a
        // blank chat reused from another CLI's pick is (re)connected under this one.
        chat.CliId = cliId;

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
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        if (ActiveEditorTab() is { } activeTab)
        {
            _ = activeTab.CheckAndUpdateIfModifiedAsync();
        }

        if (_attentionChat is not { } chat)
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
            var tab = new ChatTab { ViewModel = chat, Workspace = ViewModel };
            tab.ToolFileRequested += OnChatToolFileRequested;
            tab.PlanOpenRequested += OnChatPlanOpenRequested;
            tab.TaskPeekRequested += OnChatTaskPeekRequested;
            tab.ArtifactRequested += (_, artifact) => ShowArtifact(artifact);
            tab.UpdateCliRequested += (_, cmd) => _ = RunInNewTerminalAsync(cmd);
            tab.RunningTaskPeekRequested += (_, task) => SetPeek(ReferenceEquals(PeekPane.Task, task) && PeekPane.Visibility == Visibility.Visible ? null : task);
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
                : ChatPaletteTheme.Brush("TextFillColorPrimaryBrush");
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
        var warn = ((SolidColorBrush)ChatPaletteTheme.Brush("StatusWarnBrush")).Color;
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
            Fill = ChatPaletteTheme.Brush("StatusRunningBrush"),
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
            Fill = ChatPaletteTheme.Brush("StatusWarnBrush"),
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

    private TerminalTabEntry? TerminalTabInFront() =>
        _terminalTabs.FirstOrDefault(e => ReferenceEquals(e.Item, CentreTabs.SelectedItem));

    private bool IsCliInSession()
    {
        if (ViewModel is null)
        {
            return false;
        }

        // 1. Is there an active agent chat session connected or working?
        if (ViewModel.Chat.IsConnected || _chatTabs.Any(t => t.Chat.IsConnected))
        {
            return true;
        }

        // 2. Is an agent CLI running in a terminal (it has a session of its own), or a terminal started for the agent?
        return ViewModel.TerminalSessions.Count > 0 || _terminalTabs.Any(t => t.Terminal.IsAgentTerminal);
    }

    /// <summary>The terminal CLI session of the tab in front, while that CLI is still running; null for any other tab.</summary>
    private ChatViewModel? CliSessionInFront() =>
        TerminalTabInFront()?.Terminal.AgentSession is { } session && ViewModel.TerminalSessions.Contains(session) ? session : null;

    private void OpenTerminalTab() => AddTerminalTab(ViewModel.CreateTerminal());

    /// <summary>
    /// Shows a terminal - shell or command - as a centre tab. Every invocation is
    /// a new session: terminals stack as tabs like in VS Code.
    /// </summary>
    private void AddTerminalTab(TerminalViewModel terminal, int insertIndex = -1)
    {
        CrashLog.Trace("terminal: AddTerminalTab begin");
        try
        {
            // An agent CLI typed at this terminal's prompt is launched in its own tab with its own
            // session's MCP servers (see TryOpenCliTab), so nothing is written for it here.
            terminal.LaunchRewriter ??= OnTerminalLaunchTyped;

            ITerminalHost tab;
            if (terminal.AgentSession is not null)
            {
                // An agent CLI: its TUI in a rounded card beside the session panel.
                var cli = new CliTerminalTab { ViewModel = terminal, Workspace = ViewModel };
                cli.FileDiffRequested += (_, path) => OnChatToolFileRequested(cli, new ToolFileRequestEventArgs(path, isEdit: true));
                cli.ArtifactRequested += (_, artifact) => ShowArtifact(artifact);
                cli.UpdateCliRequested += (_, cmd) => _ = RunInNewTerminalAsync(cmd);
                cli.TaskPeekRequested += (_, task) => SetPeek(ReferenceEquals(PeekPane.Task, task) && PeekPane.Visibility == Visibility.Visible ? null : task);
                cli.ResumeRequested += (_, resume) =>
                {
                    // A past conversation resumes natively: the CLI is launched again with its own resume
                    // arguments, through the same path as a launch typed at a shell prompt.
                    if (ViewModel is { } workspace)
                    {
                        TryOpenCliTab($"PS {workspace.ProjectPath}> {resume.Cli.Executable} {resume.Arguments}", TerminalTabInFront());
                    }
                };
                tab = cli;
            }
            else
            {
                tab = new TerminalTab { ViewModel = terminal };
            }

            tab.LinkActivated += OnTerminalLinkActivated;
            var item = new TabViewItem { IsClosable = true, Header = BuildTerminalHeader(terminal) };
            item.Content = (UIElement)tab;

            var entry = new TerminalTabEntry(item, tab, terminal);
            _terminalTabs.Add(entry);
            if (insertIndex >= 0 && insertIndex <= CentreTabs.TabItems.Count)
            {
                CentreTabs.TabItems.Insert(insertIndex, item);
            }
            else
            {
                CentreTabs.TabItems.Add(item);
            }

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
    /// Closes a terminal tab: unhooks events, tears down the display host, terminates the shell,
    /// and removes it from the tab strip.
    /// </summary>
    private void CloseTerminalTab(TerminalTabEntry entry)
    {
        _terminalTabs.Remove(entry);
        EndCliSession(entry.Terminal);

        if (CentreTabs.TabItems.Contains(entry.Item))
        {
            CentreTabs.TabItems.Remove(entry.Item);
        }

        UpdateEmptyState();
        UpdateSessionPanelForActiveTab();
        entry.Tab.LinkActivated -= OnTerminalLinkActivated;
        entry.Tab.CloseHost();
        ViewModel.RemoveTerminal(entry.Terminal);
    }

    /// <summary>
    /// Called when Enter is pressed at a terminal's shell prompt. A known agent CLI typed there
    /// opens the session panel and gets Codale's MCP servers before it starts: attached as a launch
    /// argument where the CLI takes one (the returned command), otherwise through the config files
    /// it reads at startup. Returns null to run the line as typed.
    /// </summary>
    private string? OnTerminalLaunchTyped(string promptLine)
    {
        // Called for every line submitted at a shell prompt. A CLI launch is taken out of this shell
        // and run in a dedicated tab (the returned empty line clears what was typed); its exit is
        // tracked there, so any other line is simply run as typed.
        if (!TryOpenCliTab(promptLine, TerminalTabInFront()))
        {
            return null;
        }

        return "";
    }

    /// <summary>
    /// Runs an agent CLI launch in its own terminal tab whose shell exits with the CLI: the session
    /// panel mirrors it while it lives and the tab closes when it finishes. False when the line is not
    /// a CLI session launch.
    /// </summary>
    private bool TryOpenCliTab(string promptLine, TerminalTabEntry? origin)
    {
        if (ViewModel is null ||
            CliLocator.DetectLaunch(promptLine) is not ({ } cli, var args) ||
            !McpServerSettings.IsSessionLaunch(promptLine))
        {
            return false;
        }

        ChatViewModel? session = null;
        try
        {
            // Every CLI launch is its own agent session: its own task host for what the CLI reports, its
            // own changes, its own simple panel. The tab selecting itself is what brings that panel up.
            session = ViewModel.CreateTerminalSession(cli);
            ViewModel.InjectMcpForTerminalCli(session);

            var command = ViewModel.RewriteCliLaunch(promptLine, session) ?? cli.Executable + args;
            CrashLog.Trace($"cli tab: launching {cli.Id} as '{command}'");
            var terminal = ViewModel.RunCliTerminal(command, cli, PromptDirectory(promptLine) ?? ViewModel.ProjectPath, session);
            _ = ViewModel.BeginTerminalCliSessionAsync(session, terminal);
            terminal.Exited += (_, _) => DispatcherQueue.TryEnqueue(() => OnCliTerminalExited(terminal));
            WatchCliForTaskCompletion(cli, session, terminal);
            AddTerminalTab(terminal);

            // A plain shell that only existed to launch the CLI is not left behind; closed after the
            // key handler that got us here has returned.
            if (origin is { Terminal.IsCommandTerminal: false })
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_terminalTabs.Contains(origin))
                    {
                        CloseTerminalTab(origin);
                    }
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Terminal", "CLI tab launch failed", ex);
            if (session is not null)
            {
                _ = RemoveCliSessionAsync(session);
            }

            return false;
        }
    }

    /// <summary>
    /// Notifies the user when the CLI in a terminal tab reports that its tasks are complete via todos_set.
    /// Nothing is said while Codale is the window in front.
    /// </summary>
    private void WatchCliForTaskCompletion(CliDescriptor cli, ChatViewModel session, TerminalViewModel terminal)
    {
        var wasComplete = false;
        void OnTodosChanged(object? sender, IReadOnlyList<TodoItem> todos)
        {
            if (todos.Count == 0)
            {
                return;
            }

            var isComplete = todos.All(t => t.IsDone);
            if (isComplete && !wasComplete)
            {
                wasComplete = true;
                var summary = todos.Count == 1 ? todos[0].Content : $"{todos.Count} tasks completed";
                NotifyAttention($"{cli.DisplayName} finished", summary);
            }
            else if (!isComplete)
            {
                wasComplete = false;
            }
        }

        session.TodosChanged += OnTodosChanged;
        terminal.Exited += (_, _) => session.TodosChanged -= OnTodosChanged;
    }

    /// <summary>Flashes the taskbar and raises a Windows notification, unless Codale is already in front.</summary>
    private void NotifyAttention(string title, string detail)
    {
        if (ViewModel is null || App.Current.MainWindowHandle is not { } hwnd || Services.Attention.IsForeground(hwnd))
        {
            return;
        }

        Services.Attention.FlashTaskbar(hwnd);
        Services.Attention.Notify(title, $"{detail}\n{ViewModel.DisplayName}", ActivationRequest.ToUri(ViewModel.ProjectPath));
    }

    /// <summary>The CLI ended: the panel stops mirroring it, and the tab goes with it unless it failed (its output is the error).</summary>
    private void OnCliTerminalExited(TerminalViewModel terminal)
    {
        if (_terminalTabs.FirstOrDefault(t => ReferenceEquals(t.Terminal, terminal)) is not { } entry)
        {
            return;
        }

        CrashLog.Trace($"cli tab: {terminal.CliId} exited with {terminal.ExitCode}");
        var name = terminal.CommandName ?? terminal.CliId;
        NotifyAttention(
            terminal.ExitCode == 0 ? $"{name} session ended" : $"{name} stopped with an error",
            terminal.ExitCode == 0 ? "The CLI exited" : $"Exit code {terminal.ExitCode}");
        EndCliSession(terminal);
        UpdateSessionPanelForActiveTab();
        if (terminal.ExitCode != 0)
        {
            return;
        }

        CloseTerminalTab(entry);
    }

    /// <summary>
    /// A CLI's tab ended: its session goes with it (its pipe, task host and panel), and only its.
    /// A panel that was showing it falls back to the chat; the caller refreshes the panel.
    /// </summary>
    private void EndCliSession(TerminalViewModel closing)
    {
        if (closing.AgentSession is not { } session || !ViewModel.TerminalSessions.Contains(session))
        {
            return;
        }

        CrashLog.Trace($"cli tab: ending CLI session after {closing.CliId} tab ended");
        if (ReferenceEquals(ViewModel.Panel, session))
        {
            ViewModel.Panel = ViewModel.Chat;
        }

        _ = RemoveCliSessionAsync(session);
    }

    private async Task RemoveCliSessionAsync(ChatViewModel session)
    {
        try
        {
            await ViewModel.RemoveTerminalSessionAsync(session);
        }
        catch (Exception ex)
        {
            CrashLog.Error("workspace", "ending a CLI session failed", ex);
        }
    }

    /// <summary>The directory a default shell prompt line shows, when it exists.</summary>
    private static string? PromptDirectory(string promptLine)
    {
        var match = System.Text.RegularExpressions.Regex.Match(promptLine, @"^(?:PS )?(?<dir>[A-Za-z]:\\[^>]*)>");
        return match.Success && Directory.Exists(match.Groups["dir"].Value) ? match.Groups["dir"].Value : null;
    }

    /// <summary>

    /// <summary>
    /// Tab chrome for a terminal: the terminal glyph, tinted by state (working, idle,
    /// exited, failed) so it still reads as a terminal tab, plus a "Terminal" title. The
    /// tint binds to the terminal's own view model. A command tab titles itself with the
    /// command's name and wears the play glyph instead.
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
            name.Text = "Terminal";
        }

        // The "(exited)" marker updates through its own binding; a pathless binding
        // would read the view model once and never hear the state's updates.
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
            // The Staged list has no selection of its own; clicking there still targets the toolbar.
            ViewModel.Git.SelectedChange = change;
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
            await OpenFileAtAsync(e.Path, e.Line, e.Column, e.EndLine, e.EndColumn);
        }
        else
        {
            ShowFile(ResolveWorkspacePath(e.Path));
        }
    }

    /// <summary>A file link in markdown (chat history, plan view, or markdown preview) was clicked.</summary>
    private async void OnMarkdownFileLink(MarkdownView source, LinkMatch link)
    {
        if (link.Path is null || !IsAncestorOf(source))
        {
            return;
        }

        await OpenFileAtAsync(link.Path, link.Line, link.Column, link.EndLine, link.EndColumn, source.BasePath);
    }

    private bool IsAncestorOf(DependencyObject node)
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ctrl+click on a URL or path in a terminal.</summary>
    private async void OnTerminalLinkActivated(object? sender, LinkMatch link)
    {
        if (link.Url is { } url)
        {
            await OpenUrlAsync(url);
        }
        else if (link.Path is { } path)
        {
            await OpenFileAtAsync(path, link.Line, link.Column, link.EndLine, link.EndColumn);
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
    private async Task OpenFileAtAsync(
        string path,
        int line,
        int column = 0,
        int endLine = 0,
        int endColumn = 0,
        string? basePath = null)
    {
        var normalized = path.Replace('/', '\\');
        string resolved;
        if (!string.IsNullOrEmpty(basePath) && !Path.IsPathRooted(normalized))
        {
            var combined = Path.GetFullPath(Path.Combine(basePath, normalized));
            resolved = File.Exists(combined) ? combined : ResolveWorkspacePath(normalized);
        }
        else
        {
            resolved = ResolveWorkspacePath(normalized);
        }

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
            var end = endLine >= line ? endLine : line;
            var label = end > line
                ? $"lines {line}–{end}"
                : column > 0
                    ? (endColumn > column ? $"line {line}:{column}–{endColumn}" : $"line {line}:{column}")
                    : $"line {line}";

            entry.Tab.ShowHighlights(
                [new Codale.Search.LineSpan(line, end)],
                label,
                reveal: true,
                busy: false,
                current: 0,
                moveCaret: true,
                column: column,
                endColumn: endColumn,
                bands: true);
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

    /// <summary>Clicking an explore, subagent, or start_task entry in the chat opens its peek, as selecting it in the task list does.</summary>
    private void OnChatTaskPeekRequested(object? sender, ToolCallItem call)
    {
        if (sender is ChatTab { ViewModel: { } chat })
        {
            var task = chat.RunningTasks.FirstOrDefault(t => t.ToolUseId == call.ToolUseId)
                ?? (call.ToolName.Contains("start_task", StringComparison.OrdinalIgnoreCase)
                    ? chat.RunningTasks.LastOrDefault(t => t.Hosted is not null)
                    : null);

            if (task is not null)
            {
                SetPeek(task);
            }
        }
    }

    private void OnChatPlanOpenRequested(object? sender, ToolCallItem call)
    {
        var artifact = ViewModel.Chat.Artifacts.FirstOrDefault(a => a.ToolUseId == call.ToolUseId)
            ?? new SessionArtifact { Kind = SessionArtifactKind.Plan, ToolUseId = call.ToolUseId, Markdown = call.PlanText ?? "" };
        ShowArtifact(artifact);
    }

    private void ShowArtifact(SessionArtifact artifact)
    {
        if (artifact.Kind == SessionArtifactKind.File)
        {
            // The path is chosen by the agent, so it is revealed rather than launched: opening it
            // would run an .exe, .bat or .ps1 on a click.
            if (artifact.SourcePath is { } output && File.Exists(output))
            {
                // Images peek in place, the same preview as pasted images in the chat.
                if (Path.GetExtension(output).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
                {
                    ImageLightbox.Show(XamlRoot, output);
                    return;
                }

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
                _ = ViewModel.Chat.SendPlanRevisionAsync(message, plan);
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

        // The tab follows plan takeovers, so it must listen to the chat the plan came
        // from; switching to another chat's plan re-aims the subscription.
        if (!ReferenceEquals(_planWatchedChat, ViewModel.Chat))
        {
            if (_planWatchedChat is not null)
            {
                _planWatchedChat.PlanReplaced -= OnPlanReplaced;
            }

            _planWatchedChat = ViewModel.Chat;
            _planWatchedChat.PlanReplaced += OnPlanReplaced;
        }
    }

    private ChatViewModel? _planWatchedChat;

    /// <summary>A proposal replaced a listed plan: a tab still showing the old one follows it.</summary>
    private void OnPlanReplaced(object? sender, (SessionArtifact OldPlan, SessionArtifact NewPlan) e)
    {
        if (_artifactTab is not null && ReferenceEquals(_artifactTab.Artifact, e.OldPlan))
        {
            ShowArtifact(e.NewPlan);
        }
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

    /// <summary>The history menu is one shared flyout; remember which commit row opened it.</summary>
    private void OnCommitMenuOpening(object? sender, object e) =>
        _commitMenuItem = ((sender as MenuFlyout)?.Target as FrameworkElement)?.DataContext as GitCommit;

    private async void OnCommitMenuViewChanges(object sender, RoutedEventArgs e)
    {
        if (_commitMenuItem is not { } commit)
        {
            return;
        }

        EnsureDiffTab();
        CentreTabs.SelectedItem = _diffTabItem;
        await ViewModel.DiffViewer.LoadCommitAsync(commit);
    }

    private static void CopyToClipboard(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void OnCommitMenuCopySha(object sender, RoutedEventArgs e)
    {
        if (_commitMenuItem is { } commit)
        {
            CopyToClipboard(commit.Sha);
        }
    }

    private void OnCommitMenuCopyMessage(object sender, RoutedEventArgs e)
    {
        if (_commitMenuItem is { } commit)
        {
            CopyToClipboard(commit.Subject);
        }
    }

    private async void OnCommitMenuNewBranch(object sender, RoutedEventArgs e)
    {
        if (_commitMenuItem is { } commit &&
            await PromptTextAsync($"New branch at {commit.ShortSha}", "Branch name") is { } name)
        {
            await ViewModel.Git.CreateBranchAtAsync(name, commit);
        }
    }

    private async void OnCommitMenuCheckout(object sender, RoutedEventArgs e)
    {
        if (_commitMenuItem is { } commit)
        {
            await ViewModel.Git.CheckoutCommitAsync(commit);
        }
    }

    private async void OnCommitMenuCherryPick(object sender, RoutedEventArgs e)
    {
        if (_commitMenuItem is { } commit)
        {
            await ViewModel.Git.CherryPickAsync(commit);
        }
    }

    private async void OnCommitMenuRevert(object sender, RoutedEventArgs e)
    {
        if (_commitMenuItem is { } commit)
        {
            await ViewModel.Git.RevertCommitAsync(commit);
        }
    }

    /// <summary>Reset rewrites the branch (and, hard, the working tree), so it always asks.</summary>
    private async void OnCommitMenuReset(object sender, RoutedEventArgs e)
    {
        if (_commitMenuItem is not { } commit || (sender as FrameworkElement)?.Tag is not string mode)
        {
            return;
        }

        var consequence = mode switch
        {
            "soft" => "Commits after it are undone; their changes stay staged.",
            "mixed" => "Commits after it are undone; their changes stay in your working tree, unstaged.",
            _ => "Commits after it are undone and every uncommitted change is thrown away for good.",
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = Codale.App.Controls.DialogTheme.Of(XamlRoot),
            Title = $"Reset to {commit.ShortSha}?",
            Content = $"Move the current branch back to \"{commit.Subject}\". {consequence}",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.Git.ResetToAsync(commit, mode);
        }
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
            _ = open.Tab.CheckAndUpdateIfModifiedAsync();
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
            moveCaret: true,
            bands: true,
            query: request.Terms);
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
        item.PointerPressed += (_, _) => _ = tab.CheckAndUpdateIfModifiedAsync();
        item.GotFocus += (_, _) => _ = tab.CheckAndUpdateIfModifiedAsync();

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
            if (_planWatchedChat is not null)
            {
                _planWatchedChat.PlanReplaced -= OnPlanReplaced;
                _planWatchedChat = null;
            }
        }
        else if (_terminalTabs.FirstOrDefault(t => ReferenceEquals(t.Item, args.Item)) is { } terminalEntry)
        {
            CloseTerminalTab(terminalEntry);
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

    private void OnServeProjectSplitClick(SplitButton sender, SplitButtonClickEventArgs e) => ServeProjectFolder();

    private void OnServeProjectFolderClick(object sender, RoutedEventArgs e) => ServeProjectFolder();

    private void ServeProjectFolder()
    {
        if (ViewModel is { } vm)
        {
            vm.McpTools.ServeFolder(vm.McpTools.ProjectPath);
        }
    }

    private async void OnServeOtherFolderClick(object sender, RoutedEventArgs e)
    {
        // The picker would light-dismiss the flyout anyway; close it so it does not flicker back.
        FlyoutBase.GetAttachedFlyout(McpToolsHost)?.Hide();
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");

            if (App.Current.MainWindowHandle is { } hwnd)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                ViewModel?.McpTools.ServeFolder(folder.Path);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("ServeFolder", ex.Message, ex);
        }
    }

    private void OnOpenServedClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ServedFolderItem item)
        {
            McpToolsViewModel.OpenUrl(item.Url);
        }
    }

    private void OnCopyServedClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ServedFolderItem item)
        {
            McpToolsViewModel.CopyUrl(item.Url);
        }
    }

    private void OnStopServedClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ServedFolderItem item)
        {
            ViewModel?.McpTools.StopServing(item.Id);
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
        else if (e.PropertyName == nameof(WorkspaceViewModel.Panel))
        {
            // The peek belongs to a session's task list: reset when the active session changes.
            SetPeek(null);
        }
    }

    /// <summary>Git panel's History: a collapsed list must give its star row back, not leave a blank band.</summary>
    private void OnGitHistorySectionClick(object sender, RoutedEventArgs e) =>
        GitHistoryRow.Height = GitHistorySection.IsChecked == true ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;

    /// <summary>
    /// The session panel belongs to an agent session, and follows the tab in front: a Claude chat tab shows
    /// that chat's full panel, a terminal CLI's tab shows that CLI's own simple panel.
    /// </summary>
    private void UpdateSessionPanelForActiveTab()
    {
        if (ViewModel is null)
        {
            return;
        }

        var chatInFront = ChatTabInFront();
        var cliInFront = CliSessionInFront();
        if (chatInFront is not null)
        {
            ViewModel.Panel = chatInFront.Chat;
        }
        else if (cliInFront is not null)
        {
            ViewModel.Panel = cliInFront;
        }
        else if (IsCliInSession())
        {
            // The session on the panel may have ended while another tab was in front.
            if (ViewModel.Panel is { IsTerminalSession: true } shown && !ViewModel.TerminalSessions.Contains(shown))
            {
                ViewModel.Panel = ViewModel.Chat;
            }
        }

        // The peek belongs to a session's task list: it goes when that session's tab does.
        if (chatInFront is null && cliInFront is null)
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

    private void OnPeekCloseRequested(object? sender, EventArgs e) => SetPeek(null);

    private async void OnPeekStopRequested(object? sender, RunningTaskItem task)
    {
        if (ViewModel is not null)
        {
            await ViewModel.Panel.StopTaskAsync(task);
        }
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

        await OpenChatOrOfferInstallAsync(Codale.Agents.CliLocator.ActiveCli.Id);
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
            case KeyboardShortcuts.FocusShellCommand:
                FocusStatusBarCommandBox();
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

    /// <summary>Focuses the status bar shell command input box and selects its text if any.</summary>
    private void FocusStatusBarCommandBox()
    {
        StatusBarCommandBox.Focus(FocusState.Programmatic);
        if (FindTemplateChild(StatusBarCommandBox, "TextBox") is TextBox inner && !string.IsNullOrEmpty(StatusBarCommandBox.Text))
        {
            inner.SelectAll();
        }
    }

    /// <summary>The mode chip's order; the flyout lists them the same way.</summary>
    private static readonly string[] ModeCycle =
        [ChatViewModel.AutomaticMode, "manual", "plan", "auto"];

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

        var next = ModeCycle[(Array.IndexOf(ModeCycle, current) + 1) % ModeCycle.Length];
        chat.RememberModeAsDefault(next);
        await chat.SetPermissionModeAsync(next);
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
                var found = await editor.Tab.FocusOnQueryAsync(query, ViewModel.Search.ConnectModelAsync);

                // Several results are stepped through with the arrows, so the box is free for the next search.
                if (found > 1 && sender is MainWindow window)
                {
                    window.ClearSearchText();
                }
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

            // The query lives in the Search tab now; the box is free for the next search.
            if (sender is MainWindow window && !string.IsNullOrWhiteSpace(query))
            {
                window.ClearSearchText();
            }
        }
    }

    /// <summary>Escape clears the filter and brings the tree back.</summary>
    private void OnFileSearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        ViewModel.Files.SearchText = "";
    }

    private void OnFileSearchHitClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileSearchHit hit)
        {
            ShowFile(hit.FullPath);
        }
    }

    /// <summary>Rescans the disk, re-reads git, and re-runs the filter if one is up.</summary>
    private async void OnFilesRefreshClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Files.Reload();

        try
        {
            await ViewModel.Git.RefreshAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Files refresh failed: {ex.Message}");
        }
    }

    /// <summary>Folds every open folder; the project and added folder roots stay open so the tree is never blank.</summary>
    private void OnFilesCollapseClick(object sender, RoutedEventArgs e)
    {
        var open = new List<TreeViewItem>();
        CollectTreeItems(FileTree, open);

        // Deepest first, so a parent folding does not hide children still to be reached.
        for (var i = open.Count - 1; i >= 0; i--)
        {
            if (open[i].DataContext is FileNode { IsRootEntry: false })
            {
                open[i].IsExpanded = false;
            }
        }

        FileTree.UpdateLayout();
    }

    private static void CollectTreeItems(DependencyObject parent, List<TreeViewItem> into)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TreeViewItem item)
            {
                into.Add(item);
            }

            CollectTreeItems(child, into);
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
        var context = (e.OriginalSource as FrameworkElement)?.DataContext;
        var hit = context as FileNode ?? (context as FileSearchHit)?.Node;
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

            menu.Items.Add(MenuAction("Open with default app", () => _ = OpenWithDefaultAppAsync(node), ""));

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

        menu.ShowAt(
            (FrameworkElement)sender,
            new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = e.GetPosition((FrameworkElement)sender),
            });
        e.Handled = true;
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

    private void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
        };
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }


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
            RequestedTheme = Codale.App.Controls.DialogTheme.Of(XamlRoot),
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
            RequestedTheme = Codale.App.Controls.DialogTheme.Of(XamlRoot),
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
            RequestedTheme = Codale.App.Controls.DialogTheme.Of(XamlRoot),
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
    private void OnBranchFlyoutOpening(object? sender, object e)
    {
        ViewModel.Git.ActionError = null;
        BranchFilterBox.Text = "";
        BranchList.ItemsSource = ViewModel.Git.Branches;
    }

    private async void OnStashFlyoutOpening(object? sender, object e)
    {
        ViewModel.Git.ActionError = null;
        await ViewModel.Git.LoadStashesAsync();
    }

    private async void OnStashChangesClick(object sender, RoutedEventArgs e) =>
        await ViewModel.Git.StashChangesAsync();

    private async void OnPopStashClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is GitStash stash)
        {
            await ViewModel.Git.PopStashAsync(stash);
        }
    }

    private async void OnDropStashClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is GitStash stash)
        {
            await ViewModel.Git.DropStashAsync(stash);
        }
    }

    private void OnBranchFilterChanged(object sender, TextChangedEventArgs e)
    {
        var filter = BranchFilterBox.Text.Trim();
        BranchList.ItemsSource = filter.Length == 0
            ? ViewModel.Git.Branches
            : ViewModel.Git.Branches
                .Where(b => b.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private async void OnBranchFilterKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter
            && BranchList.ItemsSource is IEnumerable<GitBranch> shown
            && shown.Take(2).ToList() is [var only]
            && await ViewModel.Git.SwitchBranchAsync(only)
            && BranchButton.Flyout is { } flyout)
        {
            flyout.Hide();
        }
    }

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
    private void OnGenerateCommitAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ViewModel.Git.GenerateCommitMessageAsync();
    }

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
        var target = (sender as MenuFlyout)?.Target as FrameworkElement;
        _changeMenuItem = target is { DataContext: GitFileStatus change }
            ? change
            : ViewModel.Git.SelectedChange;

        // Rows in the Staged list carry Tag="staged": they can only be unstaged.
        var staged = target?.Tag is "staged";
        StageItem.Visibility = staged ? Visibility.Collapsed : Visibility.Visible;
        UnstageItem.Visibility = staged ? Visibility.Visible : Visibility.Collapsed;

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
            RequestedTheme = Codale.App.Controls.DialogTheme.Of(XamlRoot),
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

    #region Status Bar Terminal Input and Slide-up Flyout

    private string _terminalCwd = "";
    private readonly List<string> _commandHistory = [];
    private bool _isTerminalDrawerOpen;
    private Storyboard? _terminalDrawerAnimation;
    private readonly double _drawerHeight = 280;
    private CancellationTokenSource? _commandRunCts;
    private Process? _activeCommandProcess;

    private void SetTerminalCwd(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _terminalCwd = Path.GetFullPath(path);
        }
        catch
        {
            _terminalCwd = path;
        }

        UpdateCwdDisplay();
    }

    private void UpdateCwdDisplay()
    {
        var display = _terminalCwd.Replace('\\', '/');
        if (StatusBarCwdText is not null)
        {
            StatusBarCwdText.Text = display;
        }

        if (StatusBarCwdButton is not null)
        {
            ToolTipService.SetToolTip(StatusBarCwdButton, $"Working directory: {_terminalCwd}\nClick to change working directory");
        }

    }

    private void OnCwdMenuFlyoutOpening(object? sender, object e)
    {
        CwdMenuFlyout.Items.Clear();

        var browseItem = new MenuFlyoutItem
        {
            Text = "Choose folder…",
            Icon = new FontIcon { Glyph = "\uED25" },
        };
        browseItem.Click += async (_, _) => await PickCustomFolderAsync();
        CwdMenuFlyout.Items.Add(browseItem);

        CwdMenuFlyout.Items.Add(new MenuFlyoutSeparator());

        if (ViewModel is not null)
        {
            var projectRoot = ViewModel.ProjectPath;
            var projectItem = new MenuFlyoutItem
            {
                Text = $"Project: {Path.GetFileName(projectRoot)}",
                Icon = new FontIcon { Glyph = "\uE80F" },
            };
            ToolTipService.SetToolTip(projectItem, projectRoot);
            projectItem.Click += (_, _) => SetTerminalCwd(projectRoot);
            CwdMenuFlyout.Items.Add(projectItem);

            if (ViewModel.ActiveEditor?.FilePath is { Length: > 0 } activeFile)
            {
                var dir = Path.GetDirectoryName(activeFile);
                if (!string.IsNullOrEmpty(dir) && !string.Equals(dir, projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var fileDirItem = new MenuFlyoutItem
                    {
                        Text = $"Active file: {Path.GetFileName(dir)}",
                        Icon = new FontIcon { Glyph = "\uE8A5" },
                    };
                    ToolTipService.SetToolTip(fileDirItem, dir);
                    fileDirItem.Click += (_, _) => SetTerminalCwd(dir);
                    CwdMenuFlyout.Items.Add(fileDirItem);
                }
            }

            var roots = new List<string> { projectRoot };
            foreach (var extra in ViewModel.ExtraFolders.Folders)
            {
                var extraPath = extra;
                roots.Add(extraPath);
                CwdMenuFlyout.Items.Add(CwdItem($"Workspace: {Path.GetFileName(extraPath)}", extraPath, "\uE8B7"));
            }

            // Directories the project's commands declare, other than the roots themselves.
            var commandDirs = new List<string>();
            foreach (var command in ViewModel.DiscoverCommands())
            {
                if (string.IsNullOrWhiteSpace(command.WorkingDirectory))
                {
                    continue;
                }

                string dir;
                try
                {
                    dir = Path.GetFullPath(Path.Combine(projectRoot, command.WorkingDirectory));
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (Directory.Exists(dir)
                    && !roots.Contains(dir, StringComparer.OrdinalIgnoreCase)
                    && !commandDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                {
                    commandDirs.Add(dir);
                    CwdMenuFlyout.Items.Add(CwdItem($"Command: {Path.GetRelativePath(projectRoot, dir).Replace('\\', '/')}", dir, ""));
                }
            }

            var recent = ViewModel.ExtraFolders.RecentCwds
                .Where(r => !roots.Contains(r, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (recent.Count > 0)
            {
                CwdMenuFlyout.Items.Add(new MenuFlyoutSeparator());
                foreach (var path in recent)
                {
                    CwdMenuFlyout.Items.Add(CwdItem(Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path, path, "\uE81C"));
                }
            }
        }
    }

    private MenuFlyoutItem CwdItem(string text, string path, string glyph)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        ToolTipService.SetToolTip(item, path);
        item.Click += (_, _) => SetTerminalCwd(path);
        return item;
    }


    private async Task PickCustomFolderAsync()
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");

            if (App.Current.MainWindowHandle is { } hwnd)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                SetTerminalCwd(folder.Path);
                ViewModel?.ExtraFolders.AddRecentCwd(folder.Path);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("PickCustomFolder", ex.Message, ex);
        }
    }

    private int _historyCursor = -1;

    private void OnStatusBarCommandBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space
                when Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down):
                e.Handled = true;
                _ = CycleShellSuggestionAsync();
                return;
            case Windows.System.VirtualKey.Up:
            case Windows.System.VirtualKey.Down:
                if (_commandHistory.Count > 0)
                {
                    e.Handled = true;
                    var step = e.Key == Windows.System.VirtualKey.Up ? 1 : -1;
                    var next = _historyCursor + step;
                    if (next < 0)
                    {
                        _historyCursor = -1;
                        SetStatusBarCommandText("");
                    }
                    else if (next < _commandHistory.Count)
                    {
                        _historyCursor = next;
                        SetStatusBarCommandText(_commandHistory[_commandHistory.Count - 1 - next]);
                    }
                }

                return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (_isTerminalDrawerOpen)
            {
                CloseTerminalDrawer();
            }
            else
            {
                StatusBarCommandBox.Text = "";
            }
        }
    }

    private void OnStatusBarCommandBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _historyCursor = -1;
        }
    }

    private CancellationTokenSource? _shellSuggestCts;
    private string _shellTyped = "";
    private string _shellCurrent = "";
    private List<string> _shellCandidates = [];
    private int _shellIndex;
    private readonly List<string> _shellSeen = [];

    /// <summary>
    /// Ctrl+Space in the command box: completes what is typed from the newest matching history entry, then
    /// older ones, then the helper model. Each press shows the next one as a selected remainder, so typing
    /// on replaces it. A box that changed since the last suggestion starts over from what is there.
    /// </summary>
    private async Task CycleShellSuggestionAsync()
    {
        var box = StatusBarCommandBox;
        var text = box.Text;
        if (text.Length == 0)
        {
            return;
        }

        if (text != _shellCurrent)
        {
            _shellTyped = text;
            _shellIndex = -1;
            _shellSeen.Clear();
            _shellCandidates = _commandHistory
                .AsEnumerable().Reverse()
                .Where(h => h.Length > text.Length && h.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();
        }

        _shellIndex++;
        if (_shellIndex < _shellCandidates.Count)
        {
            ShowShellSuggestion(box, _shellTyped, _shellCandidates[_shellIndex]);
            return;
        }

        _shellIndex = _shellCandidates.Count - 1;
        if (await SuggestShellCompletionAsync(box, _shellTyped) is { Length: > 0 } rest)
        {
            _shellSeen.Add(rest);
            _shellIndex++;
            _shellCandidates.Add(_shellTyped + rest);
            ShowShellSuggestion(box, _shellTyped, _shellTyped + rest);
        }
    }

    private void ShowShellSuggestion(AutoSuggestBox box, string typed, string full)
    {
        _shellCurrent = full;
        SetStatusBarCommandText(full);
        if (FindTemplateChild(box, "TextBox") is TextBox inner)
        {
            inner.Select(typed.Length, full.Length - typed.Length);
        }
    }

    /// <summary>
    /// Asks the helper model to finish the command, steering clear of earlier answers. A newer request
    /// cancels an older one. Empty when there is nothing to add or the box no longer holds <paramref name="typed"/>.
    /// </summary>
    private async Task<string> SuggestShellCompletionAsync(AutoSuggestBox box, string typed)
    {
        _shellSuggestCts?.Cancel();
        if (BackgroundHelper.Completions is not { } model)
        {
            return "";
        }

        var cts = _shellSuggestCts = new CancellationTokenSource();
        try
        {
            var cwd = _terminalCwd;
            var shell = Path.GetFileNameWithoutExtension(Codale.Terminal.TerminalSession.ResolveShell(AppSettings.TerminalShell));
            var recent = _commandHistory.TakeLast(5).ToList();
            var files = await Task.Run(() => ListShellFiles(cwd), cts.Token);
            var rest = await model.CompleteShellAsync(shell, cwd, recent, files, typed, _shellSeen.ToList(), cts.Token);
            return cts.IsCancellationRequested || (box.Text != typed && box.Text != _shellCurrent) ? "" : rest;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Shell completion failed: {ex.Message}");
        }

        return "";
    }
    /// <summary>Up to 40 names in the folder, for the model to complete paths against.</summary>
    private static List<string> ListShellFiles(string cwd)
    {
        try
        {
            return Directory.Exists(cwd)
                ? Directory.EnumerateFileSystemEntries(cwd).Select(Path.GetFileName).OfType<string>().Take(40).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void SetStatusBarCommandText(string text)
    {
        StatusBarCommandBox.Text = text;
        if (FindTemplateChild(StatusBarCommandBox, "TextBox") is TextBox inner)
        {
            inner.Select(text.Length, 0);
        }
    }

    private async void OnStatusBarCommandBoxQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _historyCursor = -1;
        await ExecuteStatusBarCommandAsync(args.QueryText);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Windows.System.VirtualKey.Escape && _isTerminalDrawerOpen)
        {
            e.Handled = true;
            CloseTerminalDrawer();
        }
    }

    private async void OnStatusBarRunButtonClick(object sender, RoutedEventArgs e) =>
        await ExecuteStatusBarCommandAsync(StatusBarCommandBox.Text);

    private void OnStatusBarToggleOutputClick(object sender, RoutedEventArgs e)
    {
        if (_isTerminalDrawerOpen)
        {
            CloseTerminalDrawer();
        }
        else
        {
            OpenTerminalDrawer();
        }
    }

    /// <summary>
    /// Sends a status bar command to a terminal tab when it is an agent CLI launch.
    /// False to run it in the drawer as usual.
    /// </summary>
    private bool TryRunInTab(string command)
    {
        if (ViewModel is null)
        {
            return false;
        }

        var shell = Codale.Terminal.TerminalSession.ResolveShell(AppSettings.TerminalShell);
        var powershell = Path.GetFileName(shell).Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(shell).Contains("powershell", StringComparison.OrdinalIgnoreCase);

        // The CLI detector reads a default shell prompt line, so give it one for this command.
        return TryOpenCliTab($"{(powershell ? "PS " : "")}{_terminalCwd}> {command}", null);
    }

    private async Task<bool> ConfirmRiskyCommandAsync(string command, IReadOnlyList<Codale.Core.Shell.ShellFinding> findings)
    {
        var assessment = Codale.Core.Shell.ShellCommandAnalyzer.Assess(command, _terminalCwd);
        var descriptionBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = assessment.Description,
        };
        var safetyBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = $"Safety: {assessment.FormatAssessment()}",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[assessment.Risk switch
            {
                Codale.Core.Shell.ShellRisk.Danger => "SystemFillColorCriticalBrush",
                Codale.Core.Shell.ShellRisk.Caution => "SystemFillColorCautionBrush",
                _ => "SystemFillColorSuccessBrush",
            }],
        };
        var commandBlock = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Text = command,
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(descriptionBlock);
        panel.Children.Add(safetyBlock);
        panel.Children.Add(commandBlock);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = Codale.App.Controls.DialogTheme.Of(XamlRoot),
            Title = assessment.Risk == Codale.Core.Shell.ShellRisk.Danger ? "This command is dangerous" : "Run this command?",
            Content = panel,
            PrimaryButtonText = "Run anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (ViewModel?.Helper is { IsAvailable: true } model)
        {
            _ = RefineRiskyCommandDialogAsync(model, command, dialog, descriptionBlock, safetyBlock);
        }

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task RefineRiskyCommandDialogAsync(
        Codale.Core.Helper.IHelperModel model,
        string command,
        ContentDialog dialog,
        TextBlock descriptionBlock,
        TextBlock safetyBlock)
    {
        try
        {
            var assessment = await Codale.Core.Shell.ShellCommandAnalyzer.AssessAsync(model, command, _terminalCwd).ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (assessment.Description.Length > 0)
                {
                    descriptionBlock.Text = assessment.Description;
                }

                dialog.Title = assessment.Risk == Codale.Core.Shell.ShellRisk.Danger ? "This command is dangerous" : "Run this command?";
                safetyBlock.Text = $"Safety: {assessment.FormatAssessment()}";
                safetyBlock.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[assessment.Risk switch
                {
                    Codale.Core.Shell.ShellRisk.Danger => "SystemFillColorCriticalBrush",
                    Codale.Core.Shell.ShellRisk.Caution => "SystemFillColorCautionBrush",
                    _ => "SystemFillColorSuccessBrush",
                }];
            });
        }
        catch (Exception ex)
        {
            CrashLog.Debug("shell", $"risky command dialog assessment failed: {ex.Message}");
        }
    }

    private async Task ExecuteStatusBarCommandAsync(string rawCommand)
    {
        var command = rawCommand.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        // A command the risk rules flag waits for a yes; the text stays in the box if the answer is no.
        var findings = Codale.Core.Shell.ShellCommandAnalyzer.Analyze(command, _terminalCwd);
        if (findings.Count > 0 && !await ConfirmRiskyCommandAsync(command, findings))
        {
            return;
        }

        _commandHistory.Remove(command);
        _commandHistory.Add(command);

        StatusBarCommandBox.Text = "";

        // An agent CLI belongs in a tab of its own, where it stays visible and
        // its exit is known, rather than in the drawer's one-shot output box.
        if (TryRunInTab(command))
        {
            return;
        }

        OpenTerminalDrawer();

        TerminalDrawerCommandText.Text = $"$ {command}";
        TerminalDrawerSpinner.Visibility = Visibility.Visible;
        TerminalDrawerSpinner.IsActive = true;
        TerminalDrawerStatusIcon.Visibility = Visibility.Collapsed;
        TerminalDrawerStatusText.Text = "Running…";
        TerminalDrawerOutputBox.Text = "";

        if (command.Equals("cls", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            TerminalDrawerSpinner.IsActive = false;
            TerminalDrawerSpinner.Visibility = Visibility.Collapsed;
            TerminalDrawerStatusIcon.Glyph = "\uE73E";
            if (ChatPaletteTheme.TryGetBrush("SystemFillColorSuccessBrush", out var b))
            {
                TerminalDrawerStatusIcon.Foreground = b;
            }
            TerminalDrawerStatusIcon.Visibility = Visibility.Visible;
            TerminalDrawerStatusText.Text = "Cleared";
            TerminalDrawerOutputBox.Text = "";
            return;
        }

        if (command.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("chdir ", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("cd", StringComparison.OrdinalIgnoreCase))
        {
            HandleCdCommand(command);
            return;
        }

        _commandRunCts?.Cancel();
        _commandRunCts?.Dispose();
        _commandRunCts = new CancellationTokenSource();
        var ct = _commandRunCts.Token;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var outputBuilder = new StringBuilder();
            var shell = Codale.Terminal.TerminalSession.ResolveDefaultShell();

            var startInfo = new ProcessStartInfo
            {
                FileName = shell,
                WorkingDirectory = _terminalCwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var shellName = Path.GetFileName(shell).ToLowerInvariant();
            if (shellName.Contains("powershell") || shellName.Contains("pwsh"))
            {
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + command);
            }
            else
            {
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(command);
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _activeCommandProcess = process;

            void AppendOutput(string? line)
            {
                if (line is null)
                {
                    return;
                }

                var clean = Codale.Core.Agents.BackgroundTaskDetector.StripAnsi(line);
                string text;
                // stdout and stderr callbacks arrive on separate threads; StringBuilder isn't thread-safe.
                lock (outputBuilder)
                {
                    outputBuilder.AppendLine(clean);
                    text = outputBuilder.ToString();
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    TerminalDrawerOutputBox.Text = text;
                    TerminalDrawerScrollViewer.ChangeView(null, TerminalDrawerScrollViewer.ScrollableHeight, null);
                });
            }

            process.OutputDataReceived += (_, args) => AppendOutput(args.Data);
            process.ErrorDataReceived += (_, args) => AppendOutput(args.Data);

            // Off the UI thread: see ProcessUtil.StartAsync.
            await Task.Run(process.Start);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            SetCommandRunning(true);

            await process.WaitForExitAsync(ct);

            stopwatch.Stop();
            var elapsed = stopwatch.ElapsedMilliseconds < 1000
                ? $"{stopwatch.ElapsedMilliseconds}ms"
                : $"{stopwatch.Elapsed.TotalSeconds:F2}s";

            var exitCode = process.ExitCode;
            DispatcherQueue.TryEnqueue(() =>
            {
                TerminalDrawerSpinner.IsActive = false;
                TerminalDrawerSpinner.Visibility = Visibility.Collapsed;
                TerminalDrawerStatusIcon.Visibility = Visibility.Visible;

                if (exitCode == 0)
                {
                    TerminalDrawerStatusIcon.Glyph = "\uE73E";
                    if (ChatPaletteTheme.TryGetBrush("SystemFillColorSuccessBrush", out var sb))
                    {
                        TerminalDrawerStatusIcon.Foreground = sb;
                    }
                    TerminalDrawerStatusText.Text = $"Exit 0 · {elapsed}";
                }
                else
                {
                    TerminalDrawerStatusIcon.Glyph = "\uE783";
                    if (ChatPaletteTheme.TryGetBrush("SystemFillColorCriticalBrush", out var cb))
                    {
                        TerminalDrawerStatusIcon.Foreground = cb;
                    }
                    TerminalDrawerStatusText.Text = $"Exit {exitCode} · {elapsed}";
                }

                if (outputBuilder.Length == 0)
                {
                    TerminalDrawerOutputBox.Text = "(Command executed with no output)";
                }
            });
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            TerminalDrawerSpinner.IsActive = false;
            TerminalDrawerSpinner.Visibility = Visibility.Collapsed;
            TerminalDrawerStatusIcon.Glyph = "\uE711";
            if (ChatPaletteTheme.TryGetBrush("SystemFillColorCautionBrush", out var caub))
            {
                TerminalDrawerStatusIcon.Foreground = caub;
            }
            TerminalDrawerStatusIcon.Visibility = Visibility.Visible;
            TerminalDrawerStatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TerminalDrawerSpinner.IsActive = false;
            TerminalDrawerSpinner.Visibility = Visibility.Collapsed;
            TerminalDrawerStatusIcon.Glyph = "\uE783";
            if (ChatPaletteTheme.TryGetBrush("SystemFillColorCriticalBrush", out var critb))
            {
                TerminalDrawerStatusIcon.Foreground = critb;
            }
            TerminalDrawerStatusIcon.Visibility = Visibility.Visible;
            TerminalDrawerStatusText.Text = "Failed";
            TerminalDrawerOutputBox.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _activeCommandProcess?.Dispose();
            _activeCommandProcess = null;
            SetCommandRunning(false);
        }
    }

    private void HandleCdCommand(string command)
    {
        TerminalDrawerSpinner.IsActive = false;
        TerminalDrawerSpinner.Visibility = Visibility.Collapsed;
        TerminalDrawerStatusIcon.Visibility = Visibility.Visible;

        var parts = command.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var target = parts.Length > 1 ? parts[1].Trim('\"', '\'') : "";

        string targetPath;
        if (string.IsNullOrEmpty(target) || target == "~")
        {
            targetPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (Path.IsPathRooted(target))
        {
            targetPath = target;
        }
        else
        {
            targetPath = Path.GetFullPath(Path.Combine(_terminalCwd, target));
        }

        if (Directory.Exists(targetPath))
        {
            SetTerminalCwd(targetPath);
            TerminalDrawerStatusIcon.Glyph = "\uE73E";
            if (ChatPaletteTheme.TryGetBrush("SystemFillColorSuccessBrush", out var sb))
            {
                TerminalDrawerStatusIcon.Foreground = sb;
            }
            TerminalDrawerStatusText.Text = "Directory changed";
            TerminalDrawerOutputBox.Text = $"Current working directory is now:\n{targetPath.Replace('\\', '/')}";
        }
        else
        {
            TerminalDrawerStatusIcon.Glyph = "\uE783";
            if (ChatPaletteTheme.TryGetBrush("SystemFillColorCriticalBrush", out var cb))
            {
                TerminalDrawerStatusIcon.Foreground = cb;
            }
            TerminalDrawerStatusText.Text = "Directory not found";
            TerminalDrawerOutputBox.Text = $"Cannot find path '{target}' because it does not exist.";
        }
    }

    private void OpenTerminalDrawer()
    {
        if (_isTerminalDrawerOpen)
        {
            return;
        }

        _isTerminalDrawerOpen = true;

        TerminalOutputDrawer.Visibility = Visibility.Visible;
        TerminalOutputDrawer.Height = _drawerHeight;

        var slide = new DoubleAnimation
        {
            From = _drawerHeight,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, TerminalDrawerSlide);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
        };
        Storyboard.SetTarget(fade, TerminalOutputDrawer);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);

        _terminalDrawerAnimation?.Stop();
        _terminalDrawerAnimation = storyboard;
        storyboard.Begin();
    }

    private void CloseTerminalDrawer()
    {
        if (!_isTerminalDrawerOpen)
        {
            return;
        }

        _isTerminalDrawerOpen = false;

        var slide = new DoubleAnimation
        {
            From = 0,
            To = _drawerHeight,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(slide, TerminalDrawerSlide);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(160),
        };
        Storyboard.SetTarget(fade, TerminalOutputDrawer);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);

        storyboard.Completed += (_, _) =>
        {
            if (!_isTerminalDrawerOpen)
            {
                TerminalOutputDrawer.Visibility = Visibility.Collapsed;
            }
        };

        _terminalDrawerAnimation?.Stop();
        _terminalDrawerAnimation = storyboard;
        storyboard.Begin();
    }

    private void OnPointerPressedOutsideDrawer(object sender, PointerRoutedEventArgs e)
    {
        if (!_isTerminalDrawerOpen)
        {
            return;
        }

        if (IsPointerInsideTerminalDrawer(e))
        {
            return;
        }

        CloseTerminalDrawer();
    }

    private bool IsPointerInsideTerminalDrawer(PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            if (IsDescendantOf(source, TerminalOutputDrawer))
            {
                return true;
            }

            if (IsDescendantOf(source, StatusBarToggleOutputButton))
            {
                return true;
            }
        }

        try
        {
            var drawerPoint = e.GetCurrentPoint(TerminalOutputDrawer).Position;
            if (drawerPoint.X >= 0 && drawerPoint.X <= TerminalOutputDrawer.ActualWidth &&
                drawerPoint.Y >= 0 && drawerPoint.Y <= TerminalOutputDrawer.ActualHeight)
            {
                return true;
            }

            var togglePoint = e.GetCurrentPoint(StatusBarToggleOutputButton).Position;
            if (togglePoint.X >= 0 && togglePoint.X <= StatusBarToggleOutputButton.ActualWidth &&
                togglePoint.Y >= 0 && togglePoint.Y <= StatusBarToggleOutputButton.ActualHeight)
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject root)
    {
        for (DependencyObject? current = node; current is not null;)
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                break;
            }
        }

        return false;
    }

    private void OnTerminalDrawerCloseClick(object sender, RoutedEventArgs e) => CloseTerminalDrawer();

    private void OnTerminalDrawerCopyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TerminalDrawerOutputBox.Text))
        {
            CopyText(TerminalDrawerOutputBox.Text);
        }
    }

    private void OnTerminalDrawerClearClick(object sender, RoutedEventArgs e)
    {
        TerminalDrawerOutputBox.Text = "";
        TerminalDrawerStatusText.Text = "Cleared";
    }

    private async void OnTerminalDrawerRerunClick(object sender, RoutedEventArgs e)
    {
        var cmd = TerminalDrawerCommandText.Text.TrimStart('$', ' ').Trim();
        if (!string.IsNullOrEmpty(cmd))
        {
            await ExecuteStatusBarCommandAsync(cmd);
        }
    }

    private void OnTerminalDrawerStopClick(object sender, RoutedEventArgs e)
    {
        _commandRunCts?.Cancel();
        try
        {
            _activeCommandProcess?.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process already exited or was disposed.
        }
    }

    /// <summary>Shows the toggle-button progress bar and the drawer Stop button once a command has run for a moment.</summary>
    private async void SetCommandRunning(bool running)
    {
        if (!running)
        {
            StatusBarRunProgress.Visibility = Visibility.Collapsed;
            TerminalDrawerStopButton.Visibility = Visibility.Collapsed;
            TerminalDrawerRerunButton.IsEnabled = true;
            return;
        }

        TerminalDrawerStopButton.Visibility = Visibility.Visible;
        TerminalDrawerRerunButton.IsEnabled = false;
        var process = _activeCommandProcess;
        await Task.Delay(600);
        if (ReferenceEquals(process, _activeCommandProcess) && process is not null)
        {
            StatusBarRunProgress.Visibility = Visibility.Visible;
        }
    }

    private void OnTerminalDrawerOpenTabClick(object sender, RoutedEventArgs e)
    {
        var cmd = TerminalDrawerCommandText.Text.TrimStart('$', ' ').Trim();
        CloseTerminalDrawer();
        if (!string.IsNullOrEmpty(cmd))
        {
            _ = RunInNewTerminalAsync(cmd);
        }
        else
        {
            AddTerminalTab(new TerminalViewModel(_terminalCwd));
        }
    }

    private void OnTerminalDrawerOutputBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CloseTerminalDrawer();
        }
    }

    #endregion

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
    private sealed class TerminalTabEntry(TabViewItem item, ITerminalHost tab, TerminalViewModel terminal)
    {
        public TabViewItem Item { get; } = item;

        public ITerminalHost Tab { get; } = tab;

        public TerminalViewModel Terminal { get; } = terminal;
    }
}