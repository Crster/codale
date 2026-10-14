namespace Codale.Core.Agents;

/// <summary>
/// Subscription usage as reported by the CLI's <c>rate_limit_event</c>.
/// This is what the status bar's "remaining" indicator renders.
/// </summary>
public readonly record struct RateLimitSnapshot
{
    /// <summary>e.g. "allowed", "rejected".</summary>
    public string? Status { get; init; }

    public string? LimitType { get; init; }
    public bool IsUsingOverage { get; init; }

    /// <summary>0.0 - 1.0 utilisation of the rolling five hour window.</summary>
    public double? FiveHourUtilization { get; init; }

    public DateTimeOffset? FiveHourResetsAt { get; init; }

    /// <summary>0.0 - 1.0 utilisation of the rolling seven day window.</summary>
    public double? SevenDayUtilization { get; init; }

    public DateTimeOffset? SevenDayResetsAt { get; init; }

    /// <summary>Length of the long window in minutes when the CLI reports it (Codex free plans have one 30-day window); null means weekly.</summary>
    public long? LongWindowMins { get; init; }

    /// <summary>The window closest to exhaustion — what the status bar should lead with.</summary>
    public double? WorstUtilization => new[] { FiveHourUtilization, SevenDayUtilization }
        .Where(u => u is not null)
        .Max();
}
