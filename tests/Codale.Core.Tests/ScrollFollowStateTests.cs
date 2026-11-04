using Codale.Core.Text;

namespace Codale.Core.Tests;

public class ScrollFollowStateTests
{
    [Fact]
    public void Growth_before_the_next_follow_pass_does_not_unpin()
    {
        var state = new ScrollFollowState();
        state.Observe(500, 1000, 500, false);
        state.Observe(500, 1500, 500, false);
        Assert.True(state.IsFollowing);
    }

    [Fact]
    public void User_scrolling_up_cancels_follow_and_returning_to_end_resumes_it()
    {
        var state = new ScrollFollowState();
        state.Observe(1000, 1500, 500, false);
        state.Pause();
        state.Observe(900, 1500, 500, false);
        Assert.False(state.IsFollowing);
        state.Observe(1000, 1500, 500, false);
        Assert.True(state.IsFollowing);
    }

    [Fact]
    public void Resize_and_virtual_estimate_corrections_preserve_reading_mode()
    {
        var state = new ScrollFollowState();
        state.Observe(1000, 1500, 500, false);
        state.Pause();
        state.Observe(800, 1200, 400, false);
        Assert.False(state.IsFollowing);
        state.Follow();
        state.Observe(800, 1700, 400, false);
        Assert.True(state.IsFollowing);
    }

    [Fact]
    public void Delayed_programmatic_view_changes_do_not_override_user_pause()
    {
        var state = new ScrollFollowState();
        state.Observe(500, 1000, 500, false);
        state.Pause();
        state.Observe(1000, 1500, 500, true);
        state.Observe(1001, 1500, 500, true);
        Assert.False(state.IsFollowing);
    }

    [Fact]
    public void Native_reanchoring_with_unchanged_extent_keeps_following()
    {
        var state = new ScrollFollowState();
        state.Observe(1000, 1500, 500, false);
        // An extra wheel event at the end need not produce a view change. A later
        // native correction must not be mistaken for a fresh gesture scrolling up.
        state.Observe(700, 1500, 500, false);
        state.Observe(1000, 1500, 500, true);
        Assert.True(state.IsFollowing);
    }

    [Fact]
    public void Reset_for_another_conversation_starts_following()
    {
        var state = new ScrollFollowState();
        state.Pause();
        state.Reset();
        state.Observe(0, 2000, 500, false);
        Assert.True(state.IsFollowing);
    }
}
