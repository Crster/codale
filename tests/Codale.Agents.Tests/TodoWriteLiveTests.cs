using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// Captures the CLI's task-list tool calls so the todo panel can be corrected against
/// the actual wire shape. The premise it replaces: claude 2.1.274 has <em>no TodoWrite
/// at all</em> - the task list arrives as <c>TaskCreate</c> / <c>TaskUpdate</c> calls
/// (verified by capture; the old full-snapshot TodoWrite remains supported for older
/// transcripts). Captured inputs become tests/fixtures/claude/task-tools.jsonl.
/// </summary>
/// <remarks>
/// Opt-in: <c>CODALE_LIVE_TESTS=1</c>, plus <c>CODALE_LIVE_RAW_LOG</c> to record the
/// wire exchange.
/// </remarks>
public sealed class TodoWriteLiveTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("CODALE_LIVE_TESTS") == "1";

    [Fact]
    public async Task The_cli_task_tools_carry_parseable_task_state()
    {
        if (!Enabled)
        {
            return;
        }

        var work = Directory.CreateTempSubdirectory("codale-tasks-");
        try
        {
            await using var session = new ClaudeAgentSession(new ClaudeSessionOptions
            {
                WorkingDirectory = work.FullName,
                Model = "haiku",
                PermissionMode = "manual",
                SessionId = Guid.NewGuid(),

                // Task tools change no files, so manual permissions stay out of the way.
                RawLogPath = Environment.GetEnvironmentVariable("CODALE_LIVE_RAW_LOG"),
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            await session.StartAsync(cts.Token);
            await session.SendAsync(new UserTurn(
                "Using your task tools (TaskCreate / TaskUpdate): first create exactly three tasks named " +
                "'plan the work', 'do the work', 'review the work'. Then mark 'do the work' completed. " +
                "No other tools, then reply with just: done"), cts.Token);

            var calls = new List<(string Tool, JsonElement Input)>();
            var seen = new List<string>();

            await foreach (var e in session.Events.ReadAllAsync(cts.Token))
            {
                seen.Add(e.GetType().Name);

                if (e is ToolCallStarted started && started.ToolName.StartsWith("Task"))
                {
                    calls.Add((started.ToolName, started.Input));
                }

                if (e is TurnCompleted)
                {
                    break;
                }
            }

            var trace = string.Join(", ", seen);
            Assert.True(calls.Count > 0, $"The CLI never called a task tool. Saw: {trace}");

            foreach (var (tool, input) in calls)
            {
                Console.WriteLine($"{tool}: {JsonSerializer.Serialize(input)}");
            }
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
