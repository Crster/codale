using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class TurnResolutionTests
{
    [Fact]
    public void SuccessCompletesWithoutANotice()
    {
        var turn = TurnResolution.Resolve("success", null, interruptPending: false, autoModeUnavailable: false);

        Assert.Equal(TurnEnding.Completed, turn.Ending);
        Assert.Equal(TurnNotice.None, turn.Notice);
    }

    [Fact]
    public void AnInterruptedTurnStopsWithAnInfoNotice()
    {
        var turn = TurnResolution.Resolve("error_during_execution", null, interruptPending: true, autoModeUnavailable: false);

        Assert.Equal(TurnEnding.Stopped, turn.Ending);
        Assert.Equal(TurnNotice.Interrupted, turn.Notice);
        Assert.Equal("Turn interrupted.", turn.NoticeText);
    }

    [Fact]
    public void AnErrorDuringExecutionWithoutAnInterruptIsAFailure()
    {
        var turn = TurnResolution.Resolve("error_during_execution", "boom", interruptPending: false, autoModeUnavailable: false);

        Assert.Equal(TurnEnding.Failed, turn.Ending);
        Assert.Equal(TurnNotice.Failed, turn.Notice);
        Assert.Equal("Turn ended: boom", turn.NoticeText);
    }

    [Fact]
    public void AFailureWithoutAnErrorNamesTheSubtype()
    {
        var turn = TurnResolution.Resolve("error_max_turns", null, interruptPending: false, autoModeUnavailable: false);

        Assert.Equal("Turn ended: error_max_turns", turn.NoticeText);
    }

    [Fact]
    public void AnErrorWhileAutoModeIsDownPausesRatherThanFails()
    {
        var turn = TurnResolution.Resolve("error_during_execution", "down", interruptPending: false, autoModeUnavailable: true);

        Assert.Equal(TurnEnding.Stopped, turn.Ending);
        Assert.Equal(TurnNotice.Paused, turn.Notice);
    }

    [Fact]
    public void AnInterruptWinsOverAutoModeBeingDown()
    {
        var turn = TurnResolution.Resolve("error_during_execution", null, interruptPending: true, autoModeUnavailable: true);

        Assert.Equal(TurnNotice.Interrupted, turn.Notice);
    }

    [Fact]
    public void AutoModeBeingDownDoesNotPauseASuccessfulTurn()
    {
        var turn = TurnResolution.Resolve("success", null, interruptPending: false, autoModeUnavailable: true);

        Assert.Equal(TurnEnding.Completed, turn.Ending);
        Assert.Equal(TurnNotice.None, turn.Notice);
    }
}
