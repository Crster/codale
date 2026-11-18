using System.Collections.ObjectModel;

using Codale.Core.Agents;
using Codale.Git;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// Backs the status bar and the session "changes" list. Every number here comes from
/// the CLI where available, with explicit fallback cost estimates: token counts and context from
/// the turn result, subscription utilisation from rate_limit_event.
/// </summary>
public sealed partial class StatusViewModel : ObservableObject
{
    private readonly PromptCacheState _promptCache = new();
    public string CacheSummary => _promptCache.Summary;
    public string CacheDetail => _promptCache.Detail;

    public void PrepareCache(string explanation)
    {
        _promptCache.Prepare(explanation);
        NotifyCache();
    }

    public void ObserveCache(UsageSnapshot usage)
    {
        _promptCache.Observe(usage);
        NotifyCache();
    }

    public void EndCacheTurn()
    {
        _promptCache.EndTurn();
        NotifyCache();
    }

    public void ResetCacheObservation() => PrepareCache(_promptCache.Continuation);

    public void ObserveCacheTurnTotal(UsageSnapshot usage)
    {
        _promptCache.ObserveTurnTotal(usage);
        NotifyCache();
    }

    public void BeginCacheTurn()
    {
        _promptCache.BeginTurn();
        NotifyCache();
    }

    private void NotifyCache()
    {
        OnPropertyChanged(nameof(CacheSummary));
        OnPropertyChanged(nameof(CacheDetail));
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    public partial string Model { get; set; } = NotConnected;

    public bool HasModel => Model is { Length: > 0 } && Model != NotConnected;

    /// <summary>The placeholder <see cref="Model"/> holds before a session reports one.</summary>
    public const string NotConnected = "not connected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextText))]
    [NotifyPropertyChangedFor(nameof(ContextSummary))]
    [NotifyPropertyChangedFor(nameof(ContextFraction))]
    [NotifyPropertyChangedFor(nameof(ContextPercent))]
    [NotifyPropertyChangedFor(nameof(ContextPercentText))]
    [NotifyPropertyChangedFor(nameof(HasContextWindow))]
    [NotifyPropertyChangedFor(nameof(HasContext))]
    [NotifyPropertyChangedFor(nameof(HasUsageMeter))]
    public partial int ContextTokens { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextText))]
    [NotifyPropertyChangedFor(nameof(ContextSummary))]
    [NotifyPropertyChangedFor(nameof(ContextFraction))]
    [NotifyPropertyChangedFor(nameof(ContextPercent))]
    [NotifyPropertyChangedFor(nameof(ContextPercentText))]
    [NotifyPropertyChangedFor(nameof(HasContextWindow))]
    public partial int ContextWindow { get; set; }

    [ObservableProperty]
    public partial decimal SessionCostUsd { get; set; }

    [ObservableProperty]
    public partial decimal EstimatedCostUsd { get; set; }

    public decimal UsageCostUsd => SessionCostUsd > 0 ? SessionCostUsd : EstimatedCostUsd;
    private decimal _fallbackCostAdjustment;
    private decimal _fallbackMaxCostAdjustment;
    public decimal FallbackCostUsd => Math.Max(0, UsageCostUsd + _fallbackCostAdjustment);

    public decimal FallbackMaxCostUsd => Math.Max(0, UsageCostUsd + _fallbackMaxCostAdjustment);

    /// <summary>Calibrate the budget reading; future session spend adds to this amount.</summary>
    public void SetFallbackUsage(decimal spentUsd)
    {
        _fallbackCostAdjustment = Math.Max(0, spentUsd) - UsageCostUsd;
        NotifyFallbackUsage();
    }

    public void SetFallbackMaxUsage(decimal spentUsd)
    {
        _fallbackMaxCostAdjustment = Math.Max(0, spentUsd) - UsageCostUsd;
        NotifyFallbackUsage();
    }

    public void ResetFallbackUsage()
    {
        SetFallbackUsage(0);
        SetFallbackMaxUsage(0);
    }

    public bool HasFallbackUsage => !HasUsage;
    public double FallbackUsagePercent => Math.Clamp((double)(FallbackCostUsd / 10m * 100m), 0, 100);
    public double FallbackMaxUsagePercent => Math.Clamp((double)(FallbackMaxCostUsd / 100m * 100m), 0, 100);
    public string FallbackUsageSummary => $"${FallbackCostUsd:0.00} \u00b7 {FallbackUsagePercent:0}%";
    public string FallbackMaxUsageSummary => $"${FallbackMaxCostUsd:0.00} \u00b7 {FallbackMaxUsagePercent:0}%";

    partial void OnSessionCostUsdChanged(decimal value) => NotifyFallbackUsage();
    partial void OnEstimatedCostUsdChanged(decimal value) => NotifyFallbackUsage();
    partial void OnFiveHourUtilizationChanged(double? value) => NotifyFallbackUsage();
    partial void OnSevenDayUtilizationChanged(double? value) => NotifyFallbackUsage();

    private void NotifyFallbackUsage()
    {
        OnPropertyChanged(nameof(HasFallbackUsage));
        OnPropertyChanged(nameof(UsageCostUsd));
        OnPropertyChanged(nameof(FallbackCostUsd));
        OnPropertyChanged(nameof(FallbackMaxCostUsd));
        OnPropertyChanged(nameof(FallbackUsagePercent));
        OnPropertyChanged(nameof(FallbackMaxUsagePercent));
        OnPropertyChanged(nameof(FallbackUsageSummary));
        OnPropertyChanged(nameof(FallbackMaxUsageSummary));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    [NotifyPropertyChangedFor(nameof(HasClaudeUsage))]
    [NotifyPropertyChangedFor(nameof(HasFiveHour))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyUsage))]
    [NotifyPropertyChangedFor(nameof(HasUsageMeter))]
    [NotifyPropertyChangedFor(nameof(FiveHourPercent))]
    [NotifyPropertyChangedFor(nameof(PreviousFiveHourPercent))]
    [NotifyPropertyChangedFor(nameof(CurrentPromptPercent))]
    [NotifyPropertyChangedFor(nameof(FiveHourText))]
    [NotifyPropertyChangedFor(nameof(FiveHourSummary))]
    [NotifyPropertyChangedFor(nameof(UsageSummary))]
    [NotifyPropertyChangedFor(nameof(UsageMeterText))]
    public partial double? FiveHourUtilization { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSevenDay))]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    [NotifyPropertyChangedFor(nameof(HasClaudeUsage))]
    [NotifyPropertyChangedFor(nameof(HasLongWindowOnly))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyUsage))]
    [NotifyPropertyChangedFor(nameof(HasUsageMeter))]
    [NotifyPropertyChangedFor(nameof(UsageMeterText))]
    [NotifyPropertyChangedFor(nameof(SevenDayPercent))]
    [NotifyPropertyChangedFor(nameof(SevenDayText))]
    [NotifyPropertyChangedFor(nameof(SevenDaySummary))]
    [NotifyPropertyChangedFor(nameof(UsageSummary))]
    public partial double? SevenDayUtilization { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FiveHourResetText))]
    [NotifyPropertyChangedFor(nameof(FiveHourSummary))]
    public partial DateTimeOffset? FiveHourResetsAt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SevenDayResetText))]
    [NotifyPropertyChangedFor(nameof(SevenDaySummary))]
    public partial DateTimeOffset? SevenDayResetsAt { get; set; }

    /// <summary>Files the agent changed this session, most recent first.</summary>
    public ObservableCollection<SessionFileChange> Changes { get; } = [];

    /// <summary>Input + output tokens spent this session; the only meter a BYOK provider has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokensText))]
    [NotifyPropertyChangedFor(nameof(HasSessionTokens))]
    [NotifyPropertyChangedFor(nameof(HasTokenUsage))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyUsage))]
    [NotifyPropertyChangedFor(nameof(TokenUsagePercent))]
    [NotifyPropertyChangedFor(nameof(TokenUsageSummary))]
    public partial long SessionTokens { get; set; }

    public bool HasSessionTokens => SessionTokens > 0;

    public string TokensText => $"{Format((int)Math.Min(SessionTokens, int.MaxValue))} session tokens";

    /// <summary>
    /// What a full usage bar stands for under an override. The endpoint publishes no quota
    /// Codale can read, so the bar measures the session's tokens against a fixed budget.
    /// </summary>
    public const long TokenUsageBudget = 100_000_000;

    /// <summary>The usage bar replaces the subscription windows while an override is active.</summary>
    public bool HasTokenUsage => IsOverrideCli && SessionTokens > 0;

    /// <summary>Nothing has reported yet: the panel shows a zeroed usage bar rather than none.</summary>
    public bool ShowEmptyUsage => !HasFiveHour && !HasLongWindowOnly && !HasTokenUsage;

    /// <summary>0-100: the session's tokens as a share of <see cref="TokenUsageBudget"/>.</summary>
    public double TokenUsagePercent => Math.Clamp(SessionTokens * 100.0 / TokenUsageBudget, 0, 100);

    /// <summary>"1.2M / 100M   1%" for the usage bar's row.</summary>
    public string TokenUsageSummary =>
        $"{FormatLong(SessionTokens)} / {FormatLong(TokenUsageBudget)}   {TokenUsagePercent:0}%";

    /// <summary>True while this chat talks to a custom endpoint; the second status bar shows only then.</summary>
    [ObservableProperty]
    public partial bool IsCustomProvider { get; set; }

    /// <summary>
    /// True while this chat's CLI talks to a third-party endpoint - Codale's bridge, or an
    /// ANTHROPIC_BASE_URL override inherited from the environment (a Z.ai-style session
    /// override): no Claude subscription sits behind it, so only context is metered.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    [NotifyPropertyChangedFor(nameof(HasUsageMeter))]
    [NotifyPropertyChangedFor(nameof(UsageMeterLabel))]
    [NotifyPropertyChangedFor(nameof(UsageMeterText))]
    [NotifyPropertyChangedFor(nameof(HasTokenUsage))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyUsage))]
    public partial bool IsOverrideCli { get; set; }

    partial void OnIsOverrideCliChanged(bool value)
    {
        NotifyFallbackUsage();
        if (value)
        {
            // Windows left behind by an earlier Claude-login session are not this
            // endpoint's: drop them so only context shows.
            FiveHourUtilization = null;
            SevenDayUtilization = null;
            FiveHourResetsAt = null;
            SevenDayResetsAt = null;
        }
    }

    /// <summary>Drops the context and subscription readings of the CLI this chat used before.</summary>
    public void ResetCliReadings()
    {
        ContextTokens = 0;
        ContextWindow = 0;
        SessionTokens = 0;
        SessionCostUsd = 0;
        EstimatedCostUsd = 0;
        ResetFallbackUsage();
        FiveHourUtilization = null;
        SevenDayUtilization = null;
        FiveHourResetsAt = null;
        SevenDayResetsAt = null;
        LongWindowMins = null;
        _promptBaseline = null;
        OnPropertyChanged(nameof(LongWindowLabel));
    }

    /// <summary>The Claude CLI's subscription meter only applies while talking to Anthropic itself.</summary>
    public bool HasClaudeUsage => HasUsage;

    internal static string FormatLong(long tokens) => Format((int)Math.Min(tokens, int.MaxValue));

    /// <summary>
    /// A subscription reading has arrived. Under a CLI override there is no subscription to
    /// read - the meter shows context only - so rate-limit windows are never applied.
    /// </summary>
    public bool HasUsage => (FiveHourUtilization is not null || SevenDayUtilization is not null) && !IsOverrideCli;

    /// <summary>Same as <see cref="HasUsage"/>; the XAML binds the name.</summary>
    public bool HasFiveHour => FiveHourUtilization is not null && !IsOverrideCli;

    /// <summary>A long window with no five-hour one (a Codex free plan's single 30-day quota); the panel shows it on its own row.</summary>
    public bool HasLongWindowOnly => SevenDayUtilization is not null && FiveHourUtilization is null && !IsOverrideCli;

    /// <summary>"Weekly limit", or "Monthly limit" when the CLI reported a ~30-day window.</summary>
    public string LongWindowLabel => LongWindowMins switch
    {
        > 20000 => "Monthly limit",
        _ => "Weekly limit",
    };

    public long? LongWindowMins { get; private set; }

    /// <summary>The status bar's usage button: Claude subscription windows, or context only under an override.</summary>
    public bool HasUsageMeter => HasUsage || HasContext;

    /// <summary>The connected CLI's name ("Claude CLI" / "Codex CLI"); labels the usage button. Set when the session connects.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageMeterLabel))]
    public partial string CliName { get; set; } = "Claude CLI";

    /// <summary>The button's fixed label: the CLI, or the override when one is active.</summary>
    public string UsageMeterLabel => IsOverrideCli ? "Context" : CliName;

    /// <summary>Summary beside the label: the window percentages, or empty under an override.</summary>
    public string UsageMeterText => IsOverrideCli ? "" : UsageSummary;

    public string ContextText => ContextWindow > 0
        ? $"{Format(ContextTokens)} / {Format(ContextWindow)}"
        : ContextTokens == 0 ? "0 / 1.0M" : Format(ContextTokens);

    /// <summary>"45k / 200k Â\u00b7 22%" for the session panel's context bar; a dash before the session reports anything.</summary>
    public string ContextSummary => !HasContext
        ? "0 Â\u00b7 0%"
        : HasContextWindow ? $"{ContextText} Â\u00b7 {ContextPercentText}" : ContextText;

    public bool HasContextWindow => ContextWindow > 0;

    /// <summary>Only once the session has reported something to show.</summary>
    public bool HasContext => ContextTokens > 0;

    /// <summary>0-100, for the status bar meter.</summary>
    public double ContextPercent => ContextFraction * 100;

    public string ContextPercentText => $"{ContextPercent:0}%";

    public double ContextFraction =>
        ContextWindow > 0 ? Math.Clamp(ContextTokens / (double)ContextWindow, 0, 1) : 0;

    /// <summary>0-100, for the status bar meters.</summary>
    public double FiveHourPercent => Math.Clamp((FiveHourUtilization ?? 0) * 100, 0, 100);

    private double? _promptBaseline;
    /// <summary>Marks the 5-hour usage so far as history; what is spent after this is the current prompt's.</summary>
    public void BeginPrompt()
    {
        _promptBaseline = FiveHourPercent;
        OnPropertyChanged(nameof(PreviousFiveHourPercent));
        OnPropertyChanged(nameof(CurrentPromptPercent));
    }

    /// <summary>
    /// The 5-hour usage before the current prompt (the whole bar once the window has rolled over).
    /// Before any prompt in this run there is no baseline, so all usage counts as history.
    /// </summary>
    public double PreviousFiveHourPercent => Math.Min(_promptBaseline ?? FiveHourPercent, FiveHourPercent);

    /// <summary>The share of the 5-hour window the current prompt spent.</summary>
    public double CurrentPromptPercent => FiveHourPercent - PreviousFiveHourPercent;

    public double SevenDayPercent => Math.Clamp((SevenDayUtilization ?? 0) * 100, 0, 100);

    public bool HasSevenDay => SevenDayUtilization is not null;

    public string FiveHourText => $"{FiveHourPercent:0}%";

    public string SevenDayText => $"{SevenDayPercent:0}%";

    public string FiveHourResetText => Countdown(FiveHourResetsAt);

    public string SevenDayResetText => Countdown(SevenDayResetsAt);

    /// <summary>"Resets in 4 hr 16 min   9%" for a window's flyout row.</summary>
    public string FiveHourSummary => WindowSummary(FiveHourResetText, FiveHourText);

    public string SevenDaySummary => WindowSummary(SevenDayResetText, SevenDayText);

    private static string WindowSummary(string reset, string percent) =>
        reset.Length > 0 ? $"{reset}   {percent}" : percent;

    /// <summary>"Resets in 4 hr 16 min", or empty once the window has rolled over.</summary>
    private static string Countdown(DateTimeOffset? at)
    {
        var remaining = (at ?? DateTimeOffset.MinValue) - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "";
        }

        var text = remaining.TotalDays >= 1 ? $"{(int)remaining.TotalDays} d {remaining.Hours} hr"
            : remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours} hr {remaining.Minutes} min"
            : $"{Math.Max(1, remaining.Minutes)} min";
        return "Resets in " + text;
    }

    /// <summary>Status bar label: the percentages alone, e.g. "9% Â\u00b7 67%".</summary>
    public string UsageSummary => string.Join(" Â\u00b7 ", new[] { FiveHourUtilization, SevenDayUtilization }
        .Where(u => u is not null)
        .Select(u => $"{Math.Clamp(u!.Value * 100, 0, 100):0}%"));

    public void Apply(UsageSnapshot usage) => ContextTokens = usage.ContextTokens;

    public void Apply(TurnCompleted turn)
    {
        // Turn usage is cumulative across every request in the turn; price it once here.
        if (turn.Usage is { } usage)
            EstimatedCostUsd += (usage.ContextTokens * 2m + usage.OutputTokens * 10m) / 1_000_000m;
        if (turn.ContextWindow is { } window)
        {
            ContextWindow = window;
        }

        // Deliberately no ContextTokens here: the turn result's usage is the whole
        // turn's cumulative total (every request summed), not the window's occupancy.
        // ChatViewModel applies the last assistant message's usage instead.

        if (turn.TotalCostUsd is { } cost)
        {
            // The CLI reports the running total for the session, not a per-turn delta.
            SessionCostUsd = cost;
        }
    }

    public void Apply(RateLimitSnapshot limits)
    {
        if (IsOverrideCli)
        {
            // The endpoint in front of the CLI is not the subscription these windows
            // describe; the numbers would sit beside a meter that never spends it.
            return;
        }

        // A turn's rate_limit_event can carry one window only; keep the other's last reading.
        FiveHourUtilization = limits.FiveHourUtilization ?? FiveHourUtilization;
        SevenDayUtilization = limits.SevenDayUtilization ?? SevenDayUtilization;
        FiveHourResetsAt = limits.FiveHourResetsAt ?? FiveHourResetsAt;
        SevenDayResetsAt = limits.SevenDayResetsAt ?? SevenDayResetsAt;
        LongWindowMins = limits.LongWindowMins ?? LongWindowMins;
        OnPropertyChanged(nameof(LongWindowLabel));
    }

    /// <summary>
    /// Records one agent-reported file change against the session list: one row per
    /// file, newest on top, its totals measured from before the agent's first touch.
    /// The row lights up for a few seconds so the panel shows where the agent just was.
    /// </summary>
    public void RecordFileChange(FileChange change, FileDiff? diff)
    {
        var row = Changes.FirstOrDefault(c => string.Equals(c.Path, change.FilePath, StringComparison.OrdinalIgnoreCase));

        if (row is null)
        {
            row = new SessionFileChange { Path = change.FilePath };
            Changes.Insert(0, row);
        }
        else if (Changes.IndexOf(row) > 0)
        {
            Changes.Move(Changes.IndexOf(row), 0);
        }

        row.Record(change, diff);
        MarkFresh(row);

        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(ChangeSummary));
        NotifyChangeBar();
    }

    /// <summary>A new conversation starts with a clean list.</summary>
    public void ClearSessionChanges()
    {
        Changes.Clear();
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(ChangeSummary));
        NotifyChangeBar();
        HasFreshChanges = false;
    }

    /// <summary>
    /// Settles rows whose totals a write burst coalesced away; runs at turn end, when
    /// the last edit has landed and one final read+diff per touched file is enough.
    /// </summary>
    public void RefreshSessionChanges()
    {
        foreach (var row in Changes)
        {
            row.RefreshCountsIfDirty();
        }

        OnPropertyChanged(nameof(ChangeSummary));
        NotifyChangeBar();
    }

    public int ChangeCount => Changes.Count;

    public bool HasChanges => Changes.Count > 0;

    /// <summary>"3 files +42 âˆ’7" for the status bar.</summary>
    public string ChangeBarText => $"{Changes.Count} {(Changes.Count == 1 ? "file" : "files")}  +{Changes.Sum(c => c.Added)} âˆ’{Changes.Sum(c => c.Removed)}";

    private void NotifyChangeBar()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ChangeBarText));
    }

    /// <summary>"3 files Â\u00b7 +42 âˆ’7" for the section header.</summary>
    public string ChangeSummary => Changes.Count == 0
        ? "The agent has not changed any files in this session."
        : $"{Changes.Count} {(Changes.Count == 1 ? "file" : "files")} Â\u00b7 +{Changes.Sum(c => c.Added)} âˆ’{Changes.Sum(c => c.Removed)}";

    /// <summary>Any row still lit from a change moments ago: drives the section badge's pulse.</summary>
    [ObservableProperty]
    public partial bool HasFreshChanges { get; set; }

    private int _freshStamp;

    private async void MarkFresh(SessionFileChange row)
    {
        var stamp = ++_freshStamp;
        row.IsFresh = true;
        HasFreshChanges = true;

        try
        {
            // Awaited on the UI thread, so the continuation lands back on it.
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
        }
        finally
        {
            row.IsFresh = false;
            if (stamp == _freshStamp)
            {
                HasFreshChanges = false;
            }
        }
    }

    private static string Format(int tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.0}M",
        >= 1_000 => $"{tokens / 1_000.0:0.0}k",
        _ => tokens.ToString(),
    };
}
