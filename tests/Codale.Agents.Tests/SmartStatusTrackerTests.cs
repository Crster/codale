using Codale.App.Services;

namespace Codale.Agents.Tests;

public sealed class SmartStatusTrackerTests
{
    private static SmartStatusTracker Create()
    {
        var tracker = new SmartStatusTracker();
        tracker.Configure(SmartFeatureSettings.Groups.Select(g => (g.Id, true, (string?)null)));
        return tracker;
    }

    [Fact]
    public void Simultaneous_requests_show_only_the_latest_feature_then_resume_remaining_work()
    {
        var tracker = Create();
        tracker.Start("a", "summary"); tracker.Start("b", "embedding"); tracker.Start("c", "completion");
        Assert.True(tracker.Snapshot().Visible);
        Assert.Equal("Smart: Suggesting", tracker.Snapshot().Text);
        tracker.Finish("a", "completed"); tracker.Finish("c", "completed");
        Assert.Equal("Smart: Processing", tracker.Snapshot().Text);
        tracker.Finish("b", "completed");
        Assert.Equal("Smart: Idle", tracker.Snapshot().Text);
        Assert.True(tracker.Snapshot().Visible);
    }

    [Fact]
    public void Error_survives_other_feature_success_and_cancellation_until_successful_retry()
    {
        var tracker = Create();
        tracker.Start("a", "shell"); tracker.Finish("a", "error", "HTTP 401");
        tracker.Start("b", "summary");
        Assert.Equal("Smart: Processing", tracker.Snapshot().Text);
        tracker.Finish("b", "completed");
        tracker.Start("c", "shell"); tracker.Finish("c", "cancelled");
        Assert.Equal("Smart: Error", tracker.Snapshot().Text);
        Assert.True(tracker.Snapshot().Visible);
        Assert.Contains("HTTP 401", tracker.Snapshot().Detail);
        tracker.Start("d", "shell"); tracker.Finish("d", "completed");
        Assert.Equal("Smart: Idle", tracker.Snapshot().Text);
    }

    [Fact]
    public void Reconfiguration_discards_old_outcomes_and_embedding_only_is_enabled()
    {
        var tracker = Create();
        tracker.Start("old", "embedding");
        tracker.Configure([("embedding", true, (string?)null)]);
        tracker.Start("stale", "embedding", DateTimeOffset.UtcNow.AddMinutes(-1));
        tracker.Finish("stale", "error", "Outdated request");
        Assert.Equal("Smart: Idle", tracker.Snapshot().Text);
        tracker.Finish("old", "error", "Old endpoint failed");
        tracker.Start("new", "embedding");
        Assert.Equal("Smart: Processing", tracker.Snapshot().Text);
        tracker.Configure([]);
        tracker.Finish("new", "completed");
        Assert.Equal("Smart: Off", tracker.Snapshot().Text);
        Assert.True(tracker.Snapshot().Visible);
    }
}
