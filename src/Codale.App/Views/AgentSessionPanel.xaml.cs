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
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Codale.App.Views;

/// <summary>
/// The session panel for a specific chat or agent CLI terminal session.
/// A compact rail for account usage and session details, with flyouts for todos, tasks, files and artifacts.
/// </summary>
public sealed partial class AgentSessionPanel : UserControl, INotifyPropertyChanged
{
    private ChatViewModel? _session;
    private WorkspaceViewModel? _workspace;
    private int _todoCountSeen;

    public AgentSessionPanel()
    {
        InitializeComponent();
        Width = 65;
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
        }
    }

    // Context, model and permission mode stay in the composer. Only account usage lives here.
    public bool HasUsage => Session?.Status is { } s && (s.HasFiveHour || s.HasLongWindowOnly || s.HasTokenUsage);
    public double UsagePercent => Session?.Status is not { } s ? 0 :
        s.HasFiveHour ? s.FiveHourPercent : s.HasLongWindowOnly ? s.SevenDayPercent : s.TokenUsagePercent;
    public string UsageSummary => Session?.Status is not { } s ? "" :
        s.HasFiveHour ? s.FiveHourSummary : s.HasLongWindowOnly ? s.SevenDaySummary : s.TokenUsageSummary;
    public string UsagePercentText => $"{UsagePercent:0}%";

    private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var property in new[] { nameof(HasUsage), nameof(UsagePercent), nameof(UsageSummary), nameof(UsagePercentText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private readonly Dictionary<FlyoutBase, (FrameworkElement Anchor, object Content)> _flyoutToolTips = [];

    private void OnDetailFlyoutOpening(object sender, object e)
    {
        // Remove the delayed tooltip while its flyout is open, then restore it on close.
        if (sender is FlyoutBase { Target: { } anchor } flyout && ToolTipService.GetToolTip(anchor) is { } content)
        {
            if (content is ToolTip tip) tip.IsOpen = false;
            _flyoutToolTips[flyout] = (anchor, content);
            ToolTipService.SetToolTip(anchor, null);
        }
    }

    private void OnDetailFlyoutClosed(object sender, object e)
    {
        if (sender is FlyoutBase flyout && _flyoutToolTips.Remove(flyout, out var tooltip))
            ToolTipService.SetToolTip(tooltip.Anchor, tooltip.Content);
    }

    private void HideDetails()
    {
        TodosFlyout.Hide();
        TasksFlyout.Hide();
        FilesFlyout.Hide();
        ArtifactsFlyout.Hide();
    }

    public CliUpdateViewModel CliUpdate { get; } = new();

    private CliDescriptor PanelCli => Session?.TerminalCli ?? Session?.SessionCli ?? CliLocator.ActiveCli;

    public string EndpointLabel =>
        Session?.TerminalCli is { } cli
            ? cli.DisplayName
            : Workspace?.CliEndpoint.ActiveName is { Length: > 0 } name ? name : "Default";

    private void OnProfileItemClick(object sender, ItemClickEventArgs e) => ProfileFlyout.Hide();

    public bool ShowSessionPickers => Session is not null && !Session.IsTerminalSession;

    public string WorkingDirectory => Workspace?.ProjectPath ?? "";

    // Events raised to parent container
    public event EventHandler<string>? FileDiffRequested;
    public event EventHandler<SessionArtifact>? ArtifactRequested;
    public event EventHandler<string>? UpdateCliRequested;

    private void NotifySessionProperties()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Session)));
        OnStatusPropertyChanged(this, new PropertyChangedEventArgs(null));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndpointLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSessionPickers)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkingDirectory)));
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.IsTerminalSession) or
            nameof(ChatViewModel.TerminalCli))
        {
            NotifySessionProperties();
        }

        if (e.PropertyName is nameof(ChatViewModel.SessionCli) or nameof(ChatViewModel.TerminalCli))
        {
            _ = CliUpdate.RefreshAsync(PanelCli);
        }

    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ProjectPath))
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
        session.Status.PropertyChanged += OnStatusPropertyChanged;
        session.Todos.CollectionChanged += OnSessionCollectionChanged;
        session.RunningTasks.CollectionChanged += OnSessionCollectionChanged;
        session.SortedTasks.CollectionChanged += OnSortedTasksChanged;
        session.Artifacts.CollectionChanged += OnSessionCollectionChanged;
    }

    private void Detach(ChatViewModel session)
    {
        session.PropertyChanged -= OnSessionPropertyChanged;
        session.Status.PropertyChanged -= OnStatusPropertyChanged;
        session.Todos.CollectionChanged -= OnSessionCollectionChanged;
        session.RunningTasks.CollectionChanged -= OnSessionCollectionChanged;
        session.SortedTasks.CollectionChanged -= OnSortedTasksChanged;
        session.Artifacts.CollectionChanged -= OnSessionCollectionChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Width = 65;

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

            ProfileFlyout.Hide();
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
        catch (Exception)
        {
        }
    }

    #region Model, Effort and Mode Pickers

    /// <summary>The model picker, anchored at <paramref name="anchor"/>: the composer's chip (opening upward).</summary>
    public void ShowModelPicker(FrameworkElement anchor, bool above)
    {
        var sender = anchor;
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

        Place(flyout, above);
        flyout.ShowAt(anchor);
    }

    /// <summary>The permission mode picker, anchored like <see cref="ShowModelPicker"/>.</summary>
    public void ShowModePicker(FrameworkElement anchor, bool above)
    {
        var sender = anchor;
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
            "Runs reads, builds and project edits; asks before writes, deletes, risky or unknown commands",
            current == ChatViewModel.AutomaticMode);
        Add("manual", "\uE7C9", "Interactive",
            "Reads and read-only commands run; every edit and other command asks first",
            current is "manual" or "default");
        Add("plan", "\uE707", "Plan",
            "Plans read-only (no writes or unknown commands), then implements with full permission",
            current == "plan");
        Add("auto", "\uE945", "Full",
            "Fully automated; never stops to confirm, only for your answers",
            current is "auto" or "dontAsk" or "bypassPermissions");
        Place(flyout, above);
        flyout.ShowAt(anchor);
    }

    private static void Place(Flyout flyout, bool above) =>
        flyout.Placement = above
            ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft
            : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom;

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
        Math.Max(360, (chip as FrameworkElement)?.ActualWidth ?? 0);

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

    #region Tasks, Changes and Artifacts

    private void OnRunningTaskClick(object sender, ItemClickEventArgs e)
    {
        HideDetails();
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
        HideDetails();
        if (e.ClickedItem is SessionFileChange change)
        {
            FileDiffRequested?.Invoke(this, change.Path);
        }
    }

    private void OnArtifactClick(object sender, ItemClickEventArgs e)
    {
        HideDetails();
        if (e.ClickedItem is SessionArtifact artifact)
        {
            ArtifactRequested?.Invoke(this, artifact);
        }
    }

    private void OnSessionCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            OpenTodosOnFirstGoals();

            // Refresh nested collection bindings when the session details change.
            NotifySessionProperties();

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

}
