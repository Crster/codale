using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class TemporaryModeRestoreTests
{
    [Fact]
    public void TakeReturnsTheModeTheTurnStartedFrom()
    {
        var session = new object();
        var restore = new TemporaryModeRestore<object>();
        restore.Begin(session, "automatic", "auto");

        var taken = restore.Take(session, "auto");

        Assert.Equal("automatic", taken!.Value.Mode);
        Assert.Same(session, taken.Value.Session);
        Assert.Null(restore.Session);
        Assert.Null(restore.RestoreMode);
    }

    [Fact]
    public void BeginFallsBackWhenNoModeIsInEffect()
    {
        var session = new object();
        var restore = new TemporaryModeRestore<object>();
        restore.Begin(session, "", "auto");

        Assert.Equal("auto", restore.RestoreMode);
    }

    [Fact]
    public void TakeDropsARestoreForAReplacedSession()
    {
        var planned = new object();
        var restore = new TemporaryModeRestore<object>();
        restore.Begin(planned, "automatic", "auto");

        Assert.Null(restore.Take(new object(), "auto"));
        Assert.Null(restore.Session);
    }

    [Fact]
    public void TakeWithNoSessionCurrentDropsTheRestore()
    {
        var restore = new TemporaryModeRestore<object>();
        restore.Begin(new object(), "automatic", "auto");

        Assert.Null(restore.Take(null, "auto"));
        Assert.Null(restore.RestoreMode);
    }

    [Fact]
    public void TakeWithNothingPendingReturnsNull()
    {
        var restore = new TemporaryModeRestore<object>();

        Assert.Null(restore.Take(new object(), "auto"));
    }

    [Fact]
    public void ClearKeepsTheAskFlagAsItWas()
    {
        var restore = new TemporaryModeRestore<object> { AskTurnFromAuto = true };
        restore.Begin(new object(), "automatic", "auto");

        restore.Clear();

        Assert.True(restore.AskTurnFromAuto);
    }
}