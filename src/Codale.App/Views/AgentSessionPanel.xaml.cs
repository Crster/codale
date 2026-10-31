using System.Collections.Specialized;
using System.ComponentModel;

using Codale.Agents;
using Codale.Agents.Claude;
using Codale.App.Controls;
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
                Detach(_session);
            }

            _session = value;
            _todoCountSeen = 0;

            if (_session is not null)
            {
                Attach(_session);
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

    private CliDescriptor PanelCli => Session?.TerminalCli ?? Session?.SessionCli ?? CliLocator.ActiveCli;

    public string EndpointLabel =>
        Session?.TerminalCli is { } cli
            ? cli.DisplayName
            : Workspace?.CliEndpoint.IsActive == true && Workspace?.CliEndpoint.ActiveName is { Length: > 0 } name ? name : "Default";

    public bool ShowSessionPickers => Session is not null && !Session.IsTerminalSession;

    public string WorkingDirectory => Session?.WorktreePath ?? Workspace?.ProjectPath ?? "";

    public bool IsIsolated => Session?.IsIsolated ?? false;

    // Events raised to parent container
    public event EventHandler<string>? FileDiffRequested;
    public event EventHandler<SessionArtifact>? ArtifactRequested;
    public event EventHandler<string>? UpdateCliRequested;
    public event EventHandler? MergeIsolatedRequested;
    public event EventHandler? DiscardIsolatedRequested;

    private void NotifySessionProperties()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Session)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndpointLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSessionPickers)));
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

        if (e.PropertyName is nameof(ChatViewModel.SessionCli) or nameof(ChatViewModel.TerminalCli))
        {
            _ = CliUpdate.RefreshAsync(PanelCli);
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

    public event EventHandler<RunningTaskItem>? TaskPeekRequested;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _elapsedTimer;

    // A task starting or finishing reorders the list: keep the running ones in view at the top.
    private void OnSortedTasksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnSessionCollectionChanged(sender, e);
        if (Session?.SortedTasks.FirstOrDefault() is { } first)
        {
            DispatcherQueue.TryEnqueue(() => RunningTasksList.ScrollIntoView(first, ScrollIntoViewAlignment.Leading));
        }
    }

    private void Attach(ChatViewModel session)
    {
        session.PropertyChanged += OnSessionPropertyChanged;
        session.Todos.CollectionChanged += OnSessionCollectionChanged;
        session.RunningTasks.CollectionChanged += OnSessionCollectionChanged;
        session.SortedTasks.CollectionChanged += OnSortedTasksChanged;
        session.Artifacts.CollectionChanged += OnSessionCollectionChanged;
    }

    private void Detach(ChatViewModel session)
    {
        session.PropertyChanged -= OnSessionPropertyChanged;
        session.Todos.CollectionChanged -= OnSessionCollectionChanged;
        session.RunningTasks.CollectionChanged -= OnSessionCollectionChanged;
        session.SortedTasks.CollectionChanged -= OnSortedTasksChanged;
        session.Artifacts.CollectionChanged -= OnSessionCollectionChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Unloaded detached everything (a tab switch unloads the panel): listen again and
        // catch up on what changed meanwhile, or the live sections stay as they were.
        if (_session is not null)
        {
            Detach(_session);
            Attach(_session);
        }

        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
            _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        }

        NotifySessionProperties();

        _elapsedTimer ??= DispatcherQueue.CreateTimer();
        _elapsedTimer.Interval = TimeSpan.FromSeconds(1);
        _elapsedTimer.Tick -= OnElapsedTick;
        _elapsedTimer.Tick += OnElapsedTick;
        _elapsedTimer.Start();
        _ = CliUpdate.RefreshAsync(PanelCli);
        UpdateHistoryLayout();
    }

    private void OnElapsedTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        foreach (var task in _session?.RunningTasks.Where(t => t.IsRunning).ToList() ?? [])
        {
            task.RefreshElapsed();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _elapsedTimer?.Stop();
        if (_session is not null)
        {
            Detach(_session);
        }

        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        }
    }

    private async void OnUpdateCliClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var cli = CliUpdate.Cli;
            if (cli != PanelCli || !CliUpdate.UpdateAvailable ||
                CliVersionCheck.UpdateCommand(cli) is not { } command || Workspace is null)
            {
                return;
            }

            UpdateCliRequested?.Invoke(this, command);

            if (cli.Id == "codex")
            {
                return;
            }

            for (var i = 0; i < 36 && CliUpdate.UpdateAvailable && CliUpdate.Cli == cli && PanelCli == cli; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (PanelCli != cli || CliUpdate.Cli != cli)
                {
                    break;
                }

                await CliUpdate.RefreshAsync(cli);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("AgentSessionPanel.OnUpdateCliClick", ex.Message, ex);
        }
    }

    private async void OnCompactContextClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Session is { } session)
            {
                await session.CompactAsync();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("AgentSessionPanel.OnCompactContextClick", ex.Message, ex);
        }
    }

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
        var panel = new StackPanel { Spacing = 2, MinWidth = PickerWidth(sender) };
        var flyout = PickerFlyout(panel);

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

            var detail = tier switch
            {
                "lite" => "Fast model, low effort. Quickest useful answers",
                "flash" => "Fast model, medium effort. Balanced everyday work",
                "pro" => "Capable model, medium effort. Most coding tasks",
                "plus" => "Capable model, high effort. Hard problems",
                _ => $"Whatever {chat.ProviderName} starts with",
            };

            var picked = tier;
            panel.Children.Add(PickOption(
                OptionContent("", ChatViewModel.TierTitle(picked), detail),
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

        flyout.ShowAt((FrameworkElement)sender);
    }

    private void OnModeChipClick(object sender, RoutedEventArgs e)
    {
        if (Session is null || !Session.CanChangeMode)
        {
            return;
        }

        var chat = Session;
        var panel = new StackPanel { Spacing = 2, MinWidth = PickerWidth(sender) };
        var flyout = PickerFlyout(panel);
        var current = chat.PermissionMode;

        void Add(string mode, string glyph, string title, string detail, bool isCurrent) =>
            panel.Children.Add(PickOption(OptionContent(glyph, title, detail), isCurrent, "mode",
                () =>
                {
                    chat.RememberModeAsDefault(mode);
                    _ = chat.SetPermissionModeAsync(mode);
                }, flyout));

        Add(ChatViewModel.AutomaticMode, "\uE82F", "Auto",
            chat.Router?.IsAvailable == true
                ? "Picks the right depth per message: answer, clarify, plan or work"
                : "Needs the Claude CLI to pick modes",
            current == ChatViewModel.AutomaticMode);
        Add("manual", "\uE7C9", "Interactive",
            "Reads freely; edits, shell and deletes ask first",
            current is "manual" or "default");
        Add("plan", "\uE707", "Plan",
            "Plans and asks first, then implements with full permission",
            current == "plan");
        Add("auto", "\uE945", "Full",
            "Fully automated; only risky shell commands and deletes confirm",
            current is "auto" or "dontAsk" or "bypassPermissions");
        flyout.ShowAt((FrameworkElement)sender);
    }

    private static Grid OptionContent(string glyph, string title, string? detail)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (detail is { Length: > 0 })
        {
            text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>The flyout is as wide as the chip that opened it, with a floor so the notes stay readable.</summary>
    private static double PickerWidth(object chip) =>
        Math.Max(400, (chip as FrameworkElement)?.ActualWidth ?? 0);

    private Flyout PickerFlyout(UIElement content) => new()
    {
        Content = content,
        FlyoutPresenterStyle = (Style)Resources["PickerFlyout"],
        Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
    };

    private RadioButton PickOption(object content, bool isChecked, string group, Action apply, Flyout flyout)
    {
        var radio = new RadioButton
        {
            Style = (Style)Resources["PickerRow"],
            Padding = new Thickness(12, 7, 12, 7),
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
        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Discard the isolated session?",
            Content = "The work done in the worktree is thrown away and this chat starts a fresh session in the project.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }, DialogTone.Danger, AppDialog.Glyphs.Delete);

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

    private void OnSessionCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            OpenTodosOnFirstGoals();

            // ObservableCollection does not raise PropertyChanged for Count, but the
            // section headers bind to Session.*.Count; refresh those nested paths.
            NotifySessionProperties();

            // A task or todo arriving opens the live sections even before the chat counts as a conversation.
            UpdateHistoryLayout();
        });

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
        var live = conversation || Session.IsTerminalSession || Session.SortedTasks.Count > 0 || Session.Todos.Count > 0;

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

    private async void OnRetitleSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionListItem session || Workspace is null)
        {
            return;
        }

        var input = new TextBox
        {
            Text = session.Title,
            PlaceholderText = "Give this history a short name",
            SelectionStart = 0,
            SelectionLength = session.Title.Length,
        };

        var suggestButton = new Button
        {
            Content = "Suggest with AI",
            Margin = new Thickness(0, 8, 0, 0),
        };

        var hint = AppDialog.Note();
        hint.Margin = new Thickness(0, 4, 0, 0);

        var content = new StackPanel
        {
            Width = 380,
            Spacing = 4,
            Children = { input, suggestButton, hint },
        };

        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename history",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        }, DialogTone.Info, AppDialog.Glyphs.Rename);

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
                    hint.Text = "This history has no description to name it from.";
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
            await ShowErrorAsync("Could not rename history", ex.Message);
        }
    }

    private async void OnRebuildHistoryClick(object sender, RoutedEventArgs e)
    {
        if (Workspace?.Sessions is not { } sessions || sessions.IsOptimizing)
        {
            return;
        }

        try
        {
            await sessions.RebuildAndOptimizeAsync();
            await AppDialog.Apply(new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "History optimized",
                Content = "Search indexes for all history in this project have been rebuilt and the database compacted.",
                CloseButtonText = "OK",
            }, DialogTone.Success).ShowAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not rebuild and optimize history", ex.Message);
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
            await ShowErrorAsync("History is open", "Close its tab before deleting it.");
            return;
        }

        var dialog = AppDialog.Apply(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete \"{session.Title}\"?",
            Content = "This removes this history's summary, turns and file index. The work it did in your workspace is kept.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }, DialogTone.Danger, AppDialog.Glyphs.Delete);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                Workspace.Sessions.Delete(session);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Could not delete history", ex.Message);
            }
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        await AppDialog.AlertAsync(XamlRoot, title, message);
    }

    #endregion
}
