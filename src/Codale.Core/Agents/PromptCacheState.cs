namespace Codale.Core.Agents;

/// <summary>Separates conversation continuity from provider-owned, non-portable prompt caches.</summary>
public readonly record struct CacheContinuationPlan(bool ResumeNative, string Explanation)
{
    public static CacheContinuationPlan Create(string sourceCli, string sourceProfile, string sourceAccount,
        string targetCli, string targetProfile, string targetAccount, bool modelChanged = false)
    {
        if (!string.Equals(sourceCli, targetCli, StringComparison.OrdinalIgnoreCase))
            return new(false, "CLI changed. A transcript handoff starts a new native session; the previous cache cannot be transferred.");
        if (!string.Equals(sourceAccount, targetAccount, StringComparison.Ordinal))
            return new(false, "Account changed. A transcript handoff starts a new native session; the previous account's cache cannot be transferred.");
        if (!string.Equals(sourceProfile, targetProfile, StringComparison.OrdinalIgnoreCase))
            return new(true, "Provider/profile changed. Native history is retained, but the previous provider's cache is not assumed reusable.");
        if (modelChanged)
            return new(true, "Model or reasoning settings changed. Native history is retained; cache compatibility must be confirmed by usage telemetry.");
        return new(true, "Native history is retained to allow cache reuse. Matching prompt content and an unexpired provider cache are still required.");
    }
}

/// <summary>Latest turn's first and latest request readings. Never infers a hit from a session ID.</summary>
public sealed class PromptCacheState
{
    public string Continuation { get; private set; } = "Cache reuse is determined by the provider.";
    public UsageSnapshot? FirstRequest { get; private set; }
    public UsageSnapshot? LatestRequest { get; private set; }
    public UsageSnapshot? TurnTotal { get; private set; }
    private bool _active;
    private bool _finished;

    public void Prepare(string explanation)
    {
        Continuation = explanation;
        FirstRequest = LatestRequest = null;
        TurnTotal = null;
        _active = false;
        _finished = false;
    }

    public void BeginTurn()
    {
        FirstRequest = LatestRequest = null;
        TurnTotal = null;
        _active = true;
        _finished = false;
    }

    public void Observe(UsageSnapshot usage)
    {
        // Output-only deltas and restored session readings cannot establish the first request's cache result.
        if (!_active || usage.ContextTokens <= 0) return;
        FirstRequest ??= usage;
        LatestRequest = usage;
    }

    public void EndTurn()
    {
        if (_active) _finished = true;
        _active = false;
    }

    public void ObserveTurnTotal(UsageSnapshot usage)
    {
        if (_active && usage.ContextTokens > 0) TurnTotal = usage;
    }

    private static bool Reported(UsageSnapshot usage) => usage.CacheUsageReported || usage.CacheReadInputTokens > 0;

    private UsageSnapshot? SummaryReading => LatestRequest is { } latest && Reported(latest)
        ? latest : TurnTotal ?? LatestRequest;

    public string Summary => SummaryReading is not { } usage ? (_finished ? "Cache: not reported" : "Cache: awaiting usage")
        : !Reported(usage) ? "Cache: not reported"
        : usage.CacheReadInputTokens > 0 ? $"Cache reused{(LatestRequest is { } last && Reported(last) ? "" : " (turn)")} · {usage.CacheReadInputTokens:N0} tokens"
        : "Cache: no reuse reported";

    public string Detail => Continuation + "\nFirst reported request: " + Describe(FirstRequest)
        + "\nLatest reported request: " + Describe(LatestRequest)
        + (TurnTotal is { } total ? "\nWhole turn (may include multiple requests): " + Describe(total) : "")
        + "\nCache reads still occupy context. A hit does not prove that the previous session's cache was reused.";

    private static string Describe(UsageSnapshot? reading) => reading is not { } usage ? "awaiting usage"
        : !Reported(usage) ? "cache telemetry unavailable"
        : $"{usage.CacheReadInputTokens:N0} cached input tokens reused, {usage.CacheCreationInputTokens:N0} written"
            + (usage.ContextTokens > 0 ? $" ({usage.CacheReadInputTokens * 100.0 / usage.ContextTokens:0.#}% of input reused)" : "");
}
