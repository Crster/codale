using System.Collections.ObjectModel;

using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Git;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// Backs the status bar and the session "changes" list. Every number here comes from
/// the CLI itself rather than being estimated: token counts and the context window from
/// the turn result, subscription utilisation from rate_limit_event.
/// </summary>
public sealed partial class StatusViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    public partial string Model { get; set; } = NotConnected;

    public bool HasModel => Model is { Length: > 0 } && Model != NotConnected;

    /// <summary>The placeholder <see cref="Model"/> holds before a session reports one.</summary>
    public const string NotConnected = "not connected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextText))]
    [NotifyPropertyChangedFor(nameof(ContextFraction))]
    [NotifyPropertyChangedFor(nameof(ContextPercent))]
    [NotifyPropertyChangedFor(nameof(ContextPercentText))]
    [NotifyPropertyChangedFor(nameof(HasContextWindow))]
    [NotifyPropertyChangedFor(nameof(HasContext))]
    public partial int ContextTokens { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextText))]
    [NotifyPropertyChangedFor(nameof(ContextFraction))]
    [NotifyPropertyChangedFor(nameof(ContextPercent))]
    [NotifyPropertyChangedFor(nameof(ContextPercentText))]
    [NotifyPropertyChangedFor(nameof(HasContextWindow))]
    public partial int ContextWindow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CostText))]
    [NotifyPropertyChangedFor(nameof(HasCost))]
    public partial decimal SessionCostUsd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    [NotifyPropertyChangedFor(nameof(HasClaudeUsage))]
    [NotifyPropertyChangedFor(nameof(HasFiveHour))]
    [NotifyPropertyChangedFor(nameof(FiveHourPercent))]
    [NotifyPropertyChangedFor(nameof(FiveHourText))]
    [NotifyPropertyChangedFor(nameof(FiveHourSummary))]
    [NotifyPropertyChangedFor(nameof(UsageSummary))]
    public partial double? FiveHourUtilization { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSevenDay))]
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
    public partial long SessionTokens { get; set; }

    public bool HasSessionTokens => SessionTokens > 0;

    /// <summary>Characters of tool output Codale kept out of this session's context by condensing it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SavedText))]
    [NotifyPropertyChangedFor(nameof(HasSaved))]
    public partial long SavedChars { get; set; }

    public bool HasSaved => SavedChars > 0;

    /// <summary>"≈12.3k tokens saved", estimated at four characters a token (there is no tokenizer here).</summary>
    public string SavedText => $"≈{Format((int)Math.Min(SavedChars / 4, int.MaxValue))} tokens saved";

    public void AddSaved(long chars)
    {
        if (chars > 0)
        {
            SavedChars += chars;
        }
    }

    public string TokensText => $"{Format((int)Math.Min(SessionTokens, int.MaxValue))} tokens";

    /// <summary>True while this chat talks to a custom endpoint; the second status bar shows only then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClaudeUsage))]
    public partial bool IsCustomProvider { get; set; }

    /// <summary>The background-task provider's name (or "Claude"); labels its usage button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomButtonText))]
    public partial string CustomProviderName { get; set; } = "Claude";

    /// <summary>The Claude CLI's subscription meter only applies while talking to Anthropic itself.</summary>
    public bool HasClaudeUsage => HasUsage;

    /// <summary>"test · 3 calls · 12.3k tokens": the provider name, its requests and what it has spent.</summary>
    public string CustomButtonText => $"{CustomProviderName} · {CustomCallsText} · {FormatLong(CustomInputTokens + CustomOutputTokens)} tokens";

    /// <summary>Window-wide: shared by every chat, so it outlives any one session.</summary>
    private readonly CustomUsageTally _custom;

    public StatusViewModel() : this(new CustomUsageTally())
    {
    }

    public StatusViewModel(CustomUsageTally custom)
    {
        _custom = custom;
        _custom.Changed += OnCustomChanged;
    }

    private void OnCustomChanged()
    {
        OnPropertyChanged(nameof(CustomCalls));
        OnPropertyChanged(nameof(CustomInputTokens));
        OnPropertyChanged(nameof(CustomOutputTokens));
        OnPropertyChanged(nameof(CustomCallsText));
        OnPropertyChanged(nameof(CustomTokensText));
        OnPropertyChanged(nameof(CustomTokensDetail));
        OnPropertyChanged(nameof(CustomButtonText));
        OnPropertyChanged(nameof(CustomAverageText));
        OnPropertyChanged(nameof(CustomCostText));
    }

    /// <summary>API requests the custom provider has served since the window opened.</summary>
    public int CustomCalls => _custom.Calls;

    public long CustomInputTokens => _custom.InputTokens;

    public long CustomOutputTokens => _custom.OutputTokens;

    public long CustomCacheReadTokens => _custom.CacheReadTokens;

    public long CustomCacheWriteTokens => _custom.CacheWriteTokens;

    public string CustomCallsText => CustomCalls == 1 ? "1 call" : $"{CustomCalls} calls";

    /// <summary>"↑12.3k ↓4.1k": fresh input and output, as the CLI counts them.</summary>
    public string CustomTokensText => $"↑{FormatLong(CustomInputTokens)} ↓{FormatLong(CustomOutputTokens)}";

    public string CustomTokensDetail =>
        $"Input {CustomInputTokens:N0} · Output {CustomOutputTokens:N0} · Cache read {CustomCacheReadTokens:N0} · Cache write {CustomCacheWriteTokens:N0}";

    public string CustomAverageText => CustomCalls > 0
        ? $"avg {FormatLong((CustomInputTokens + CustomOutputTokens) / CustomCalls)} / call"
        : "";

    /// <summary>Tallies one request served by the custom endpoint.</summary>
    public void RecordCustomCall(UsageSnapshot usage, ByokProvider? provider = null) => _custom.Record(usage, provider);

    /// <summary>"≈$0.0123": what the custom provider's requests have cost so far.</summary>
    public string CustomCostText => $"≈${_custom.CostUsd:0.0000}";

    internal static string FormatLong(long tokens) => Format((int)Math.Min(tokens, int.MaxValue));

    /// <summary>A rate-limit reading has arrived (the five-hour window is the one that always comes first).</summary>
    public bool HasUsage => FiveHourUtilization is not null;

    /// <summary>Same as <see cref="HasUsage"/>; the XAML binds both names.</summary>
    public bool HasFiveHour => HasUsage;

    public string ContextText => ContextWindow > 0
        ? $"{Format(ContextTokens)} / {Format(ContextWindow)}"
        : Format(ContextTokens);

    public bool HasContextWindow => ContextWindow > 0;

    /// <summary>Only once the session has reported something to show.</summary>
    public bool HasContext => ContextTokens > 0;

    public bool HasCost => SessionCostUsd > 0;

    /// <summary>0-100, for the status bar meter.</summary>
    public double ContextPercent => ContextFraction * 100;

    public string ContextPercentText => $"{ContextPercent:0}%";

    public double ContextFraction =>
        ContextWindow > 0 ? Math.Clamp(ContextTokens / (double)ContextWindow, 0, 1) : 0;

    public string CostText => $"${SessionCostUsd:0.0000}";

    /// <summary>0-100, for the status bar meters.</summary>
    public double FiveHourPercent => Math.Clamp((FiveHourUtilization ?? 0) * 100, 0, 100);

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

    /// <summary>Status bar label: the percentages alone, e.g. "9% · 67%".</summary>
    public string UsageSummary => string.Join(" · ", new[] { FiveHourUtilization, SevenDayUtilization }
        .Where(u => u is not null)
        .Select(u => $"{Math.Clamp(u!.Value * 100, 0, 100):0}%"));

    public void Apply(UsageSnapshot usage) => ContextTokens = usage.ContextTokens;

    public void Apply(TurnCompleted turn)
    {
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
        FiveHourUtilization = limits.FiveHourUtilization;
        SevenDayUtilization = limits.SevenDayUtilization;
        FiveHourResetsAt = limits.FiveHourResetsAt;
        SevenDayResetsAt = limits.SevenDayResetsAt;
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

        NotifyChangeBar();
    }

    public int ChangeCount => Changes.Count;

    public bool HasChanges => Changes.Count > 0;

    /// <summary>"3 files +42 −7" for the status bar.</summary>
    public string ChangeBarText => $"{Changes.Count} {(Changes.Count == 1 ? "file" : "files")}  +{Changes.Sum(c => c.Added)} −{Changes.Sum(c => c.Removed)}";

    private void NotifyChangeBar()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ChangeBarText));
    }

    /// <summary>"3 files · +42 −7" for the section header.</summary>
    public string ChangeSummary => Changes.Count == 0
        ? "The agent has not changed any files in this session."
        : $"{Changes.Count} {(Changes.Count == 1 ? "file" : "files")} · +{Changes.Sum(c => c.Added)} −{Changes.Sum(c => c.Removed)}";

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
        catch (Exception ex)
        {
            CrashLog.Error("status", "fresh-change highlight failed", ex);
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
