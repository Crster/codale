using Codale.Core.Browser;

namespace Codale.Core.Tests;

public sealed class BrowserViewportControlTests
{
    [Fact]
    public async Task Restore_is_acknowledged_after_the_browser_finishes_and_can_repeat()
    {
        var pipe = "codale-viewport-test-" + Guid.NewGuid().ToString("N");
        var calls = 0;
        await using var control = new BrowserViewportControl(pipe, () =>
        {
            calls++;
            return Task.CompletedTask;
        });
        Assert.True(await BrowserViewportControl.RestoreAsync(pipe));
        Assert.Equal(1, calls);
        Assert.True(await BrowserViewportControl.RestoreAsync(pipe));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_missing_browser_returns_without_starting_one()
    {
        Assert.False(await BrowserViewportControl.RestoreAsync("codale-missing-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Restore_failure_does_not_break_future_requests()
    {
        var pipe = "codale-viewport-test-" + Guid.NewGuid().ToString("N");
        var calls = 0;
        await using var control = new BrowserViewportControl(pipe, () =>
            ++calls == 1 ? Task.FromException(new InvalidOperationException("browser closed")) : Task.CompletedTask);

        Assert.False(await BrowserViewportControl.RestoreAsync(pipe));
        Assert.True(await BrowserViewportControl.RestoreAsync(pipe));
    }
}
