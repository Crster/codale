using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class PromptCacheStateTests
{
    [Theory]
    [InlineData("claude", "one", "a", "claude", "one", "a", false, true)]
    [InlineData("claude", "one", "a", "claude", "two", "a", false, true)]
    [InlineData("claude", "one", "a", "claude", "one", "a", true, true)]
    [InlineData("claude", "one", "a", "codex", "one", "a", false, false)]
    [InlineData("claude", "one", "a", "claude", "one", "b", false, false)]
    public void Continuity_does_not_require_cache_compatibility(string cli, string profile, string account,
        string targetCli, string targetProfile, string targetAccount, bool modelChanged, bool native)
    {
        var plan = CacheContinuationPlan.Create(cli, profile, account, targetCli, targetProfile, targetAccount, modelChanged);
        Assert.Equal(native, plan.ResumeNative);
        Assert.NotEmpty(plan.Explanation);
    }

    [Fact]
    public void Missing_telemetry_is_not_a_miss_and_startup_readings_are_ignored()
    {
        var state = new PromptCacheState();
        state.Observe(new() { InputTokens = 1000, CacheReadInputTokens = 900 });
        Assert.Null(state.LatestRequest);
        state.BeginTurn();
        state.Observe(new() { InputTokens = 1000 });
        Assert.Equal("Cache: not reported", state.Summary);
        state.Observe(new() { InputTokens = 1000, CacheUsageReported = true });
        Assert.Equal("Cache: no reuse reported", state.Summary);
        Assert.Contains("First reported request: cache telemetry unavailable", state.Detail);
    }

    [Fact]
    public void A_later_hit_does_not_erase_the_initial_miss()
    {
        var state = new PromptCacheState();
        state.BeginTurn();
        state.Observe(new() { InputTokens = 1000, CacheUsageReported = true });
        state.Observe(new() { InputTokens = 100, CacheReadInputTokens = 900, CacheUsageReported = true });
        Assert.Equal(0, state.FirstRequest!.Value.CacheReadInputTokens);
        Assert.Contains("Cache reused", state.Summary);
        Assert.Contains("90%", state.Detail);
        state.EndTurn();
        state.Observe(new() { InputTokens = 1 });
        Assert.Equal(900, state.LatestRequest!.Value.CacheReadInputTokens);
        state.Prepare("Model changed");
        Assert.Null(state.LatestRequest);
        Assert.Contains("Model changed", state.Detail);
    }

    [Fact]
    public void New_turn_discards_previous_hits_and_output_only_deltas()
    {
        var state = new PromptCacheState();
        state.BeginTurn();
        state.Observe(new() { CacheReadInputTokens = 100 });
        state.BeginTurn();
        state.Observe(new() { OutputTokens = 10, CacheUsageReported = true });
        Assert.Null(state.FirstRequest);
        Assert.Equal("Cache: awaiting usage", state.Summary);
    }

    [Fact]
    public void Turn_totals_confirm_reuse_without_claiming_a_first_request_hit()
    {
        var state = new PromptCacheState();
        state.BeginTurn();
        state.Observe(new() { InputTokens = 100 });
        state.ObserveTurnTotal(new() { InputTokens = 100, CacheReadInputTokens = 900, CacheUsageReported = true });
        state.EndTurn();
        Assert.Contains("Cache reused (turn)", state.Summary);
        Assert.Contains("First reported request: cache telemetry unavailable", state.Detail);
        Assert.Contains("Whole turn", state.Detail);
    }

    [Fact]
    public void Completed_turn_without_telemetry_is_unknown()
    {
        var state = new PromptCacheState();
        state.BeginTurn();
        state.EndTurn();
        Assert.Equal("Cache: not reported", state.Summary);
    }
}
