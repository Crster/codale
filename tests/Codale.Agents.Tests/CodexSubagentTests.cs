using System.Text.Json;

using Codale.Agents.Codex;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>A subagent runs on its own thread: it is listed as one Agent call, and none of its work lands in the main transcript.</summary>
public sealed class CodexSubagentTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CodexAgentSession Session()
    {
        var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." });
        session.AdoptThread("main");
        return session;
    }

    private static List<AgentEvent> Drain(CodexAgentSession session)
    {
        var events = new List<AgentEvent>();
        while (session.Events.TryRead(out var e))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task Other_threads_work_never_reaches_the_main_transcript()
    {
        var session = Session();

        await session.HandleNotificationAsync("item/agentMessage/delta", Json("""{"threadId":"sub-1","delta":"hello"}"""));
        await session.HandleNotificationAsync("item/completed", Json("""{"threadId":"sub-1","item":{"type":"agentMessage","id":"m1","text":"hi"}}"""));
        await session.HandleNotificationAsync("thread/tokenUsage/updated", Json("""{"threadId":"sub-1","usage":{}}"""));

        // Its words reach the host only as the subagent's own (its task log), never as the main reply.
        var events = Drain(session);
        Assert.DoesNotContain(events, e => e is AssistantTextDelta or UsageUpdated);
        Assert.DoesNotContain(events, e => e is AssistantMessageCompleted { ParentToolUseId: null });
        Assert.Equal("sub-1", Assert.Single(events.OfType<AssistantMessageCompleted>()).ParentToolUseId);
    }

    [Fact]
    public async Task A_new_thread_is_listed_once_as_an_Agent_call_and_closed_when_its_turn_completes()
    {
        var session = Session();

        await session.HandleNotificationAsync("thread/started", Json("""{"thread":{"id":"sub-1","name":"Scout"}}"""));
        await session.HandleNotificationAsync("item/started", Json("""{"threadId":"sub-1","item":{"type":"commandExecution","id":"c1","command":"ls"}}"""));
        await session.HandleNotificationAsync("turn/completed", Json("""{"threadId":"sub-1","turn":{"status":"completed"}}"""));

        var events = Drain(session);
        var started = Assert.Single(events.OfType<ToolCallStarted>(), e => e.ParentToolUseId is null);
        Assert.Equal("Agent", started.ToolName);
        Assert.Equal("sub-1", started.ToolUseId);
        Assert.True(BackgroundTaskDetector.IsSubagentTool(started.ToolName));
        Assert.Equal("Scout", started.Input.GetProperty("description").GetString());

        // Its command is its own activity, nested under the Agent call.
        var command = Assert.Single(events.OfType<ToolCallStarted>(), e => e.ParentToolUseId is not null);
        Assert.Equal(("c1", "sub-1"), (command.ToolUseId, command.ParentToolUseId));

        // Listed as a background task keyed by the thread, so it can be stopped; ended before its call closes.
        var task = events.OfType<BackgroundTaskChanged>().ToList();
        Assert.Equal([null, "completed"], task.Select(t => t.Status));
        Assert.All(task, t => Assert.Equal(("sub-1", "sub-1"), (t.TaskId, t.ToolUseId)));
        Assert.True(events.IndexOf(task[^1]) < events.FindIndex(e => e is ToolCallCompleted));

        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.Equal("sub-1", done.ToolUseId);
        Assert.False(done.IsError);
    }

    [Fact]
    public async Task Stopping_a_subagent_is_refused_for_an_unknown_thread()
    {
        var session = Session();

        Assert.False(await session.CancelTaskAsync("nobody"));
    }

    [Fact]
    public async Task The_main_threads_events_are_untouched()
    {
        var session = Session();

        await session.HandleNotificationAsync("item/agentMessage/delta", Json("""{"threadId":"main","delta":"hello"}"""));

        Assert.Contains(Drain(session), e => e is AssistantTextDelta);
    }
}