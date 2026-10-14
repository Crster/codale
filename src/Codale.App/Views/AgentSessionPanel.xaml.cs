using System.Collections.Specialized;
using System.ComponentModel;

using Codale.Agents;
using Codale.Agents.Claude;
using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.Core.Agents;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.ApplicationModel.DataTransfer;

namespace Codale.App.Views;

/// <summary>
/// The session panel for a specific chat or agent CLI terminal session.
/// Shows session status, controls, active todos, running tasks, session file changes,
/// artifacts, and past history.
/// </summary>
public sealed partial class AgentSessionPanel : UserControl, INotifyPropertyChanged
{
    private ChatViewModel? _session;
    private WorkspaceViewModel? _workspace;
    private bool _historyShowAll;
    private bool _hadConversation;
    private bool _historyLayoutQueued;
    private int _todoCountSeen;

    public AgentSessionPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The session (chat or agent terminal session) this panel displays.</summary>
    public ChatViewModel? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value))
            {
                return;
            }

            if (_session is not null)
            {
                _session.PropertyChanged -= OnSessionPropertyChanged;
                _session.Todos.CollectionChanged -= OnTodosCollectionChanged;
            }

            _session = value;
            _todoCountSeen = 0;

            if (_session is not null)
            {
                _session.PropertyChanged += OnSessionPropertyChanged;
                _session.Todos.CollectionChanged += OnTodosCollectionChanged;
                OpenTodosOnFirstGoals();
            }

            NotifySessionProperties();
            _ = CliUpdate.RefreshAsync(PanelCli);
            UpdateHistoryLayout();
        }
    }

    /// <summary>The parent workspace view model.</summary>
    public WorkspaceViewModel? Workspace
    {
        get => _workspace;
        set
        {
            if (ReferenceEquals(_workspace, value))
            {
                return;
            }

            if (_workspace is not null)
            {
                _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
            }

            _workspace = value;

            if (_workspace is not null)
            {
                _workspace.PropertyChanged += OnWorkspacePropertyChanged;
            }

            NotifySessionProperties();
            UpdateHistoryLayout();
        }
    }

    public CliUpdateViewModel CliUpdate { get; } = new();

    private CliDescriptor PanelCli => Session?.TerminalCli ?? CliLocator.ActiveCli;

    public string EndpointLabel =>
        Session?.TerminalCli is { } cli
            ? cli.DisplayName
            : Workspace?.CliEndpoint.IsActive == true && Workspace?.CliEndpoint.ActiveName is { Length: > 0 } name ? name : "Default";

    public bool ShowSessionPickers => Session is not null && !Session.IsTerminalSession;

    public bool ShowNewSessionButtons => ShowSessionPickers && !IsIsolated;

    public string WorkingDirectory => Session?.WorktreePath ?? Workspace?.ProjectPath ?? "";

    public bool IsIsolated => Session?.IsIsolated ?? false;

    // Events raised to parent container
    public event EventHandler? CollapseRequested;
    public event EventHandler<string>? FileDiffRequested;
    public event EventHandler<SessionArtifact>? ArtifactRequested;
    public event EventHandler<RunningTaskItem>? TaskPeekRequested;
    public event EventHandler<string>? UpdateCliRequested;
    public event EventHandler? NewSessionRequested;
    public event EventHandler? IsolatedSessionRequested;
    public event EventHandler? MergeIsolatedRequested;
    public event EventHandler? DiscardIsolatedRequested;

    private void NotifySessionProperties()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndpointLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSessionPickers)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowNewSessionButtons)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkingDirectory)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIsolated)));
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.IsIsolated) or
            nameof(ChatViewModel.WorktreePath) or
            nameof(ChatViewModel.IsTerminalSession) or
            nameof(ChatViewModel.TerminalCli))
        {
            NotifySessionProperties();
        }

        if (e.PropertyName == nameof(ChatViewModel.HasConversation))
        {
            QueueHistoryLayout();
        }
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ProjectPath) or
            nameof(WorkspaceViewModel.IsStartingWorktree) or
            nameof(WorkspaceViewModel.IsMergingWithAgent) or
            nameof(WorkspaceViewModel.WorktreeError))
        {
            NotifySessionProperties();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = CliUpdate.RefreshAsync(PanelCli);
        UpdateHistoryLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
            _session.Todos.CollectionChanged -= OnTodosCollectionChanged;
        }

        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        }
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e) =>
        CollapseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCliEndpointFlyoutOpening(object sender, object e) =>
        Workspace?.CliEndpoint.Reload();

    private void OnOpenProviderSettingsClick(object sender, RoutedEventArgs e) =>
        App.Current.MainWindow?.SetSettingsOpen(true);

    private async void OnUpdateCliClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var cli = CliUpdate.Cli;
            if (CliVersionCheck.UpdateCommand(cli) is not { } command || Workspace is null)
            {
                return;
            }

            UpdateCliRequested?.Invoke(this, command);

            for (var i = 0; i < 36 && CliUpdate.UpdateAvailable && CliUpdate.Cli == cli; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await CliUpdate.RefreshAsync(cli);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("AgentSessionPanel.OnUpdateCliClick", ex.Message, ex);
        }
    }

    private void OnInterruptedInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args) =>
        sender.Visibility = Visibility.Collapsed;

    private void OnReturnToLiveClick(object sender, RoutedEventArgs e) =>
        Workspace?.ReturnToLiveSession();

    #region Model, Effort and Mode Pickers

    private void OnModelChipClick(object sender, RoutedEventArgs e)
    {
        if (Session is null)
        {
            return;
        }

        var chat = Session;
        var panel = new StackPanel { Spacing = 2, MinWidth = 320 };
        var flyout = PickerFlyout(panel);

        panel.Children.Add(PickerTitle("Model"));

        // "CLI" is always selectable; the tiers appear once the catalogue and the tier
        // models behind them are known. Without them the menu points at Settings
        // instead of listing half-broken tiers.
        var tiersKnown = false;
        foreach (var tier in ChatViewModel.ModelTiers)
        {
            var (model, _) = chat.TierConfig(tier);
            if (tier != "cli" && model is not { Length: > 0 })
            {
                continue;
            }

            if (tier != "cli")
            {
                tiersKnown = true;
            }

            string detail = tier switch
            {
                "lite" => "Fast model, low effort - quickest useful answers",
                "flash" => "Fast model, medium effort - balanced everyday work",
                "pro" => "Capable model, medium effort - most coding tasks",
                "plus" => "Capable model, high effort - hard problems",
                _ => $"Whatever {chat.ProviderName} starts with",
            };
            if (model is { Length: > 0 } id)
            {
                detail += $" \u00B7 {chat.ModelName(id)}";
            }

            var picked = tier;
            panel.Children.Add(PickOption(
                OptionContent("\uE99A", ChatViewModel.TierTitle(picked), detail),
                chat.CurrentTier == picked,
                "model",
                () => _ = chat.ApplyTierAsync(picked),
                flyout));
        }

        if (!tiersKnown)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Set up the Flash and Pro models in Settings \u2192 Model tiers.",
                Style = (Style)Resources["PickerHintText"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(PickerHint());
        flyout.ShowAt((FrameworkElement)sender);
    }

    private void OnModeChipClick(object sender, RoutedEventArgs e)
    {
        if (Session is null || !Session.CanChangeMode)
        {
            return;
        }

        var chat = Session;
        var panel = new StackPanel { Spacing = 2, MinWidth = 320 };
        var flyout = PickerFlyout(panel);
        var current = chat.PermissionMode;

        void Add(string mode, string glyph, string title, string detail, bool isCurrent) =>
            panel.Children.Add(PickOption(OptionContent(glyph, title, detail), isCurrent, "mode",
                () =>
                {
                    chat.RememberModeAsDefault(mode);
                    _ = chat.SetPermissionModeAsync(mode);
                }, flyout));

        panel.Children.Add(PickerTitle("Mode"));
        Add(ChatViewModel.AutomaticMode, "\uE82F", "Auto",
            chat.Router?.IsAvailable == true
                ? "Picks the right depth per message: answer, clarify, plan, or work"
                : "Needs the Claude CLI to pick modes",
            current == ChatViewModel.AutomaticMode);
        Add("manual", "\uE7C9", "Interactive",
            "Reads freely; shell, edits, creates and deletes ask first. Asks when the goal is unclear",
            current is "manual" or "default");
        Add("plan", "\uE707", "Plan",
            "Plans and questions you first, researches and tests, then implements with full permission",
            current == "plan");
        Add("auto", "\uE945", "Full",
            "Fully automated, no asking; only risky shell commands and deletes confirm",
            current is "auto" or "dontAsk" or "bypassPermissions");
        panel.Children.Add(PickerHint());

        flyout.ShowAt((FrameworkElement)sender);
    }

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

    private Flyout PickerFlyout(UIElement content) => new()
    {
        Content = content,
        FlyoutPresenterStyle = (Style)Resources["PickerFlyout"],
    };

    private TextBlock PickerTitle(string text) =>
        new() { Text = text, Style = (Style)Resources["PickerTitleText"] };

    private TextBlock PickerHint() =>
        new() { Text = "Applies without restarting the conversation.", Style = (Style)Resources["PickerHintText"] };

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

    #endregion

    #region Isolated and New Session

    private void OnNewSessionClick(object sender, RoutedEventArgs e)
    {
        if (NewSessionRequested is not null)
        {
            NewSessionRequested.Invoke(this, EventArgs.Empty);
        }
        else if (Workspace is not null)
        {
            _ = Workspace.StartNewChatSessionAsync();
        }
    }

    private async void OnIsolatedSessionClick(object sender, RoutedEventArgs e)
    {
        if (IsolatedSessionRequested is not null)
        {
            IsolatedSessionRequested.Invoke(this, EventArgs.Empty);
        }
        else if (Workspace is not null)
        {
            await Workspace.StartIsolatedSessionAsync();
        }
    }

    private async void OnMergeIsolatedClick(object sender, RoutedEventArgs e)
    {
        if (Session?.IsBusy == true)
        {
            if (Workspace is not null)
            {
                Workspace.WorktreeError = "Wait for the current turn to finish before merging.";
            }
            return;
        }

        if (MergeIsolatedRequested is not null)
        {
            MergeIsolatedRequested.Invoke(this, EventArgs.Empty);
        }
        else if (Workspace is not null)
        {
            await Workspace.LeaveIsolationAsync(merge: true);
        }
    }

    private async void OnDiscardIsolatedClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Discard the isolated session?",
            Content = "The work done in the worktree is thrown away and this chat starts a fresh session in the project.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (DiscardIsolatedRequested is not null)
            {
                DiscardIsolatedRequested.Invoke(this, EventArgs.Empty);
            }
            else if (Workspace is not null)
            {
                await Workspace.LeaveIsolationAsync(merge: false);
            }
        }
    }

    #endregion

    #region Tasks, Changes and Artifacts

    private void OnRunningTaskClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RunningTaskItem task)
        {
            TaskPeekRequested?.Invoke(this, task);
        }
    }

    private async void OnStopTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RunningTaskItem task } && Session is not null)
        {
            await Session.StopTaskAsync(task);
        }
    }

    private void OnSessionChangeClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionFileChange change)
        {
            FileDiffRequested?.Invoke(this, change.Path);
        }
    }

    private void OnArtifactClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionArtifact artifact)
        {
            ArtifactRequested?.Invoke(this, artifact);
        }
    }

    private void OnTodosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, OpenTodosOnFirstGoals);

    private void OpenTodosOnFirstGoals()
    {
        var count = Session?.Todos.Count ?? 0;
        if (_todoCountSeen == 0 && count > 0)
        {
            TodosSection.IsChecked = true;
        }
        _todoCountSeen = count;
    }

    #endregion

    #region History

    private void QueueHistoryLayout()
    {
        if (_historyLayoutQueued)
        {
            return;
        }

        _historyLayoutQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateHistoryLayout);
    }

    private void UpdateHistoryLayout()
    {
        _historyLayoutQueued = false;

        if (Workspace is null || Session is null)
        {
            return;
        }

        var sessions = Workspace.Sessions;
        var conversation = Session.HasConversation;
        var live = conversation || Session.IsTerminalSession;

        if (conversation != _hadConversation)
        {
            _hadConversation = conversation;
            _historyShowAll = false;
        }

        var folded = conversation && !_historyShowAll;
        var expanded = HistorySection.IsChecked == true;

        IEnumerable<SessionListItem> source = folded ? sessions.OpenSessions : sessions.Sessions;
        if (!ReferenceEquals(HistoryList.ItemsSource, source))
        {
            HistoryList.ItemsSource = source;
        }

        var star = new GridLength(1, GridUnitType.Star);
        LiveRow.Height = live ? star : GridLength.Auto;
        LiveHost.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        HistoryRow.Height = expanded && !folded ? star : GridLength.Auto;

        HistoryShowAllButton.Visibility = conversation ? Visibility.Visible : Visibility.Collapsed;
        HistoryShowAllButton.Content = _historyShowAll ? "Show less" : $"Show all ({sessions.Sessions.Count})";
        HistoryNotListedText.Visibility = folded && expanded && sessions.OpenSessions.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnHistorySectionClick(object sender, RoutedEventArgs e) => UpdateHistoryLayout();

    private void OnHistoryShowAllClick(object sender, RoutedEventArgs e)
    {
        _historyShowAll = !_historyShowAll;
        HistorySection.IsChecked = true;
        UpdateHistoryLayout();
    }

    private void OnSessionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SessionListItem item &&
            Workspace?.Sessions.OpenCommand.CanExecute(item) == true)
        {
            Workspace.Sessions.OpenCommand.Execute(item);
        }
    }

    private async void OnCopySessionHandoutClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session || Workspace is null)
        {
            return;
        }

        try
        {
            CopyText(await Workspace.Sessions.BuildHandoutAsync(session));
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not create handout", ex.Message);
        }
    }

    private void OnCopySessionIdClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionListItem session)
        {
            CopyText(session.SessionId);
        }
    }

    private async void OnForkSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session || Workspace is null)
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
                var summary = await Workspace.SummarizeSessionAsync(session, cts.Token);
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
            await Workspace.ForkSessionAsync(session, summaryBox.Text.Trim());
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not fork session", ex.Message);
        }
    }

    private async void OnCompactSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session || Workspace is null)
        {
            return;
        }

        try
        {
            await Workspace.CompactSessionAsync(session.Summary);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not compact session", ex.Message);
        }
    }

    private async void OnRetitleSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session || Workspace is null)
        {
            return;
        }

        var input = new TextBox
        {
            Text = session.Title,
            PlaceholderText = "Give this session a short name",
            SelectionStart = 0,
            SelectionLength = session.Title.Length,
        };

        var suggestButton = new Button
        {
            Content = "Suggest with AI",
            Margin = new Thickness(0, 8, 0, 0),
        };

        var hint = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel
        {
            Width = 380,
            Spacing = 4,
            Children = { input, suggestButton, hint },
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename session",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        using var cts = new CancellationTokenSource();
        dialog.Closing += (_, _) => cts.Cancel();

        suggestButton.Click += async (_, _) =>
        {
            suggestButton.IsEnabled = false;
            hint.Text = "Thinking of a title…";
            try
            {
                var suggestion = await Workspace.SuggestTitleAsync(session, cts.Token);
                if (suggestion is { Length: > 0 })
                {
                    input.Text = suggestion;
                    input.SelectAll();
                    hint.Text = "";
                }
                else
                {
                    hint.Text = "This session has no prompt to name it from.";
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                hint.Text = $"Could not suggest title: {ex.Message}";
            }
            finally
            {
                suggestButton.IsEnabled = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
        {
            return;
        }

        try
        {
            await Workspace.Sessions.RetitleAsync(session, input.Text);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not retitle session", ex.Message);
        }
    }

    private async void OnDeleteSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session || Workspace is null)
        {
            return;
        }

        if (session.IsOpen)
        {
            await ShowErrorAsync("Session is open", "Close its tab before deleting it.");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete \"{session.Title}\"?",
            Content = "This removes the session's transcript from disk. The work it did in your workspace is kept.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await Workspace.Sessions.DeleteAsync(session);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Could not delete session", ex.Message);
            }
        }
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        }.ShowAsync();
    }

    #endregion
}
