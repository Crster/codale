using System.Collections.Specialized;
using System.ComponentModel;

using Codale.Agents;
using Codale.App.Services;
using Codale.App.ViewModels;

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
    /// <summary>The project window that owns this view's pickers and attention feedback.</summary>
    public IWindowOwner? Owner { get; set; }


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
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            if (field is not null)
            {
                Detach(field);
            }

            field = value;

            if (field is not null)
            {
                Attach(field);
            }

            NotifySessionProperties();
            _ = CliUpdate.RefreshAsync(PanelCli);
        }
    }

    /// <summary>The parent workspace view model.</summary>
    public WorkspaceViewModel? Workspace
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field?.PropertyChanged -= OnWorkspacePropertyChanged;

            field = value;

            field?.PropertyChanged += OnWorkspacePropertyChanged;

            NotifySessionProperties();
        }
    }

    // Model and permission mode stay in the composer. Account usage and context (with Compact) live here.
    // The rail button shows whenever there is a session, so context is always one click away.
    public bool HasUsage => Session is not null;

    /// <summary>The shortest reported quota window, or the $10 fallback budget.</summary>
    public double UsagePercent => Session?.Status is not { } s ? 0 :
        s.HasFiveHour ? s.FiveHourPercent :
        s.HasLongWindowOnly ? s.SevenDayPercent : s.HasFallbackUsage ? s.FallbackUsagePercent : 0;

    private string QuotaSummary => Session?.Status is not { } s ? "" :
        s.HasFiveHour ? s.FiveHourSummary : s.HasLongWindowOnly ? s.SevenDaySummary :
        s.ShowSubscriptionUsageStatus ? s.SubscriptionUsageStatus : s.FallbackUsageSummary;

    public string UsageSummary => Session?.Status is not { } s ? "" :
        string.Join("\n", new[] { $"Context {s.ContextSummary}", QuotaSummary }.Where(t => t.Length > 0));

    public bool ShowCompactIcon => CanCompact && Session?.Status.ContextPercent >= 80;

    public string UsagePercentText => Session?.Status.ShowSubscriptionUsageStatus == true ? "—" : $"{UsagePercent:0}%";

    /// <summary>What the current prompt cost of the 5-hour window: the measure of how expensive a call was.</summary>
    public string CurrentPromptText => Session?.Status is { HasFiveHour: true, CurrentPromptPercent: >= 0.05 } s
        ? $"This turn +{s.CurrentPromptPercent:0.#}%"
        : "";

    /// <summary>A usage meter segment: a share of 0-100 as a star column width.</summary>
    public GridLength Star(double percent) => new(Math.Clamp(percent, 0, 100), GridUnitType.Star);

    /// <summary>The unfilled rest of a usage meter.</summary>
    public GridLength Remainder(double percent) => Star(100 - percent);

    /// <summary>Session cost and tokens as the CLI reports them; empty when it reports neither.</summary>
    public string SessionTotalsText => Session?.Status is not { } s ? "" : string.Join("  ·  ", new[]
    {
        $"{(s.SessionCostUsd > 0 ? "Session cost" : "Estimated cost")} ${s.UsageCostUsd:0.00}",
        s.HasSessionTokens ? s.TokensText : null,
    }.Where(t => t is not null));

    private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var property in new[] { nameof(HasUsage), nameof(UsagePercent), nameof(UsageSummary), nameof(UsagePercentText), nameof(ShowCompactIcon), nameof(CurrentPromptText), nameof(SessionTotalsText), nameof(CanCompact) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    /// <summary>Compact is offered once there is context to free and no turn is running.</summary>
    public bool CanCompact => Session is { IsConnected: true, IsBusy: false } s && s.Status.HasContext;

    private async void OnCompactClick(object sender, RoutedEventArgs e)
    {
        UsageFlyout.Hide();
        if (Session is { } session)
            await session.CompactAsync();
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

    private void OnFallbackUsagePointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ActualWidth: > 0 } bar ||
            Session?.Status is not { HasFallbackUsage: true } status) return;
        var point = e.GetCurrentPoint(bar);
        if (!point.Properties.IsLeftButtonPressed) return;
        var fraction = Math.Clamp(point.Position.X / bar.ActualWidth, 0, 1);
        var budget = bar.Tag as string == "100" ? 100m : 10m;
        var spentUsd = decimal.Round((decimal)fraction * budget, 2);
        if (budget == 100m) status.SetFallbackMaxUsage(spentUsd);
        else status.SetFallbackUsage(spentUsd);
        e.Handled = true;
    }

    private async void OnUsageDetailsClick(object sender, RoutedEventArgs e)
    {
        var codex = Session?.Status.CliName.Contains("Codex", StringComparison.OrdinalIgnoreCase) == true;
        var url = codex ? "https://chatgpt.com/settings/usage?tab=overview" : "https://claude.ai/new#settings/usage";
        UsageFlyout.Hide();
        await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    private void HideDetails()
    {
        UsageFlyout.Hide();
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

    /// <summary>A prompt's group of changed files was picked; the workspace shows them in the side preview.</summary>
    public event EventHandler<PromptChangeGroup>? PromptChangesRequested;
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

        if (e.PropertyName is nameof(ChatViewModel.IsBusy) or nameof(ChatViewModel.IsConnected))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCompact)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCompactIcon)));
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
        if (Session is not null)
        {
            Detach(Session);
            Attach(Session);
        }

        if (Workspace is not null)
        {
            Workspace.PropertyChanged -= OnWorkspacePropertyChanged;
            Workspace.PropertyChanged += OnWorkspacePropertyChanged;
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
        DeterminateRingSync.Refresh(QuotaRing);
        foreach (var task in Session?.RunningTasks.Where(t => t.IsRunning).ToList() ?? [])
        {
            task.RefreshElapsed();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _elapsedTimer?.Stop();
        if (Session is not null)
        {
            Detach(Session);
        }

        Workspace?.PropertyChanged -= OnWorkspacePropertyChanged;
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
        if (Session is not { CanPickModel: true } chat)
        {
            return;
        }

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
                "flash" => "Fast model, high effort. Balanced everyday work",
                "pro" => "Capable model, low effort. Most coding tasks",
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
                () => _ = chat.SetPermissionModeAsync(mode, remember: true), flyout));

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
        AreOpenCloseAnimationsEnabled = false,
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

    private void OnPromptGroupClick(object sender, ItemClickEventArgs e)
    {
        HideDetails();
        if (e.ClickedItem is PromptChangeGroup group)
        {
            PromptChangesRequested?.Invoke(this, group);
        }
    }

    private async void OnSaveArtifactClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: SessionArtifact artifact } button) return;
        button.IsEnabled = false;
        try
        {
            var source = artifact.SourcePath is { Length: > 0 } path
                ? System.IO.Path.GetFullPath(path, WorkingDirectory) : null;
            var name = source is not null ? System.IO.Path.GetFileName(source) : artifact.Title;
            foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            if (string.IsNullOrWhiteSpace(name)) name = "Artifact";
            var extension = source is not null ? System.IO.Path.GetExtension(source) : ".md";
            if (string.IsNullOrEmpty(extension)) extension = ".txt";
            var windowId = PickerOwner.WindowId(Owner?.WindowHandle);
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(windowId)
            {
                SuggestedFileName = name,
            };
            picker.FileTypeChoices.Add("Artifact", [extension]);
            if (await picker.PickSaveFileAsync() is not { } destination) return;
            if (source is not null)
            {
                if (!string.Equals(source, System.IO.Path.GetFullPath(destination.Path), StringComparison.OrdinalIgnoreCase))
                    System.IO.File.Copy(source, destination.Path, overwrite: true);
            }
            else
            {
                await System.IO.File.WriteAllTextAsync(destination.Path, artifact.Markdown);
            }
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Couldn't save artifact",
                Content = ex.Message,
                CloseButtonText = "OK",
            }.ShowAsync();
        }
        finally
        {
            button.IsEnabled = true;
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
            // Refresh nested collection bindings when the session details change.
            NotifySessionProperties();

        });

    #endregion

}
