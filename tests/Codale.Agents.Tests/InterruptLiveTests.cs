using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// Verifies <see cref="ClaudeAgentSession.InterruptAsync"/> against the real CLI: the
/// wire shape (<c>control_request</c> with subtype <c>interrupt</c> over stdin) was
/// assumed, never captured, which is exactly how the approval <c>updatedInput</c> trap
/// slipped through. This test captures the real behaviour and pins it.
/// </summary>
/// <remarks>
/// Opt-in like every live test: <c>CODALE_LIVE_TESTS=1</c>. Set
/// <c>CODALE_LIVE_RAW_LOG</c> to capture the wire exchange as a replay fixture.
/// </remarks>
public sealed class InterruptLiveTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("CODALE_LIVE_TESTS") == "1";

    [Fact]
    public async Task Interrupt_stops_a_running_turn_and_the_session_survives()
    {
        if (!Enabled)
        {
            return;
        }

        var work = Directory.CreateTempSubdirectory("codale-interrupt-");
        try
        {
            await using var session = new ClaudeAgentSession(new ClaudeSessionOptions
            {
                WorkingDirectory = work.FullName,
                Model = "haiku",
                PermissionMode = "manual",
                SessionId = Guid.NewGuid(),

                // No tools: the interrupted turn is pure generation, so the assertion
                // measures the interrupt itself rather than an approval detour.
                ExtraArguments = ["--disallowedTools", "*"],
                RawLogPath = Environment.GetEnvironmentVariable("CODALE_LIVE_RAW_LOG"),
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            await session.StartAsync(cts.Token);

            // A long, tool-free generation: plenty of streamed deltas to interrupt mid-turn.
            await session.SendAsync(new UserTurn(
                "Write the numbers from 1 to 2000 spelled out as English words, one per line. " +
                "No commentary, no markdown, only the numbers."), cts.Token);

            var deltas = 0;

            await foreach (var e in session.Events.ReadAllAsync(cts.Token))
            {
                if (e is AssistantTextDelta)
                {
                    deltas++;

                    // Let the turn get properly underway before cutting it.
                    if (deltas >= 20)
                    {
                        break;
                    }
                }
            }

            Assert.True(deltas >= 20, "the turn never produced a stream to interrupt.");

            await session.InterruptAsync(cts.Token);

            // The interrupted turn must end promptly; the capture shows how the CLI
            // reports it (turn_result subtype / control_response) rather than us guessing.
            var seen = new List<string>();
            TurnCompleted? turn = null;

            await foreach (var e in session.Events.ReadAllAsync(cts.Token))
            {
                seen.Add(e.GetType().Name);

                if (e is TurnCompleted completed)
                {
                    turn = completed;
                    break;
                }
            }

            Assert.NotNull(turn);
            Assert.False(
                turn!.Subtype == "success",
                $"The turn ran to completion instead of being interrupted (subtype '{turn.Subtype}').");

            // The session must still be usable afterwards: a short turn completes well.
            await session.SendAsync(new UserTurn("Reply with exactly: ok"), cts.Token);

            TurnCompleted? second = null;

            await foreach (var e in session.Events.ReadAllAsync(cts.Token))
            {
                if (e is TurnCompleted done)
                {
                    second = done;
                    break;
                }
            }

            Assert.NotNull(second);
            Assert.False(second!.IsError);
            _ = seen; // kept for debugger inspection of the interrupt exchange
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
