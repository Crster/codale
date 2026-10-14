using System.Text.Json;

using Codale.Agents.Codex;

namespace Codale.Agents.Tests;

/// <summary>
/// Pins the mapping from the app-server's account/rateLimits payload (primary/secondary
/// windows, usedPercent 0-100) onto the normalised status-bar snapshot (0.0-1.0).
/// </summary>
public sealed class CodexRateLimitsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Primary_and_secondary_windows_map_to_five_hour_and_week()
    {
        var limits = CodexAgentSession.ParseRateLimits(Parse("""
            {
                "planType": "plus",
                "primary": { "usedPercent": 42, "resetsAt": 1800000000, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 7, "resetsAt": 1800600000, "windowDurationMins": 10080 }
            }
            """));

        Assert.Equal(0.42, limits.FiveHourUtilization);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), limits.FiveHourResetsAt);
        Assert.Equal(0.07, limits.SevenDayUtilization);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800600000), limits.SevenDayResetsAt);
        Assert.Equal(0.42, limits.WorstUtilization);
    }

    [Fact]
    public void Sparse_updates_keep_unknown_windows_null()
    {
        var limits = CodexAgentSession.ParseRateLimits(Parse("""
            { "primary": { "usedPercent": 90, "windowDurationMins": 300 } }
            """));

        Assert.Equal(0.9, limits.FiveHourUtilization);
        Assert.Null(limits.SevenDayUtilization);
        Assert.Null(limits.SevenDayResetsAt);
    }

    [Fact]
    public void A_reached_limit_is_carried_as_the_status()
    {
        var limits = CodexAgentSession.ParseRateLimits(Parse("""
            {
                "primary": { "usedPercent": 100, "windowDurationMins": 300 },
                "rateLimitReachedType": "rate_limit_reached"
            }
            """));

        Assert.Equal("rate_limit_reached", limits.Status);
    }
}
