using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// Captured live from a real interrupt: the host sends
/// <c>{"type":"control_request","request_id":...,"request":{"subtype":"interrupt"}}</c>
/// (outgoing, so not in this incoming-only capture), the CLI answers
/// <c>control_response</c> with <c>{"subtype":"success","response":{"still_queued":[]}}</c>,
/// closes the turn with a <c>result</c> of subtype <c>error_during_execution</c> and
/// <c>stop_reason: null</c>, and injects a synthetic user message saying the request was
/// interrupted. See <see cref="InterruptLiveTests"/> for how the fixture was captured.
/// </summary>
public sealed class InterruptReplayTests
{
    [Fact]
    public void An_interrupted_turn_ends_as_error_during_execution_with_no_stop_reason()
    {
        var events = Replay("interrupt.jsonl");

        var turn = events.OfType<TurnCompleted>().First();
        Assert.Equal("error_during_execution", turn.Subtype);
        Assert.True(turn.IsError);
        Assert.Null(turn.StopReason);
        Assert.Equal(0, turn.TotalCostUsd);
    }

    [Fact]
    public void The_control_response_acknowledgement_and_the_synthetic_user_line_yield_no_events()
    {
        // The ack is consumed by the transport, and the CLI's "[Request interrupted by
        // user]" line must not surface as a user bubble in the transcript.
        var events = Replay("interrupt.jsonl");

        Assert.DoesNotContain(events, e => e is UserMessageRecorded);
        Assert.Contains(events, e => e is AssistantTextDelta or AssistantMessageCompleted);
    }

    [Fact]
    public void The_session_keeps_working_after_an_interrupt()
    {
        // The last frame is the follow-up turn's result: a normal success.
        var turn = Replay("interrupt.jsonl").OfType<TurnCompleted>().Last();

        Assert.Equal("success", turn.Subtype);
        Assert.False(turn.IsError);
    }

    private static IReadOnlyList<AgentEvent> Replay(string fixtureName)
    {
        var parser = new Codale.Agents.Claude.ClaudeStreamParser();
        return File.ReadLines(Fixtures.Path(fixtureName))
            .SelectMany(parser.Parse)
            .ToList();
    }
}
