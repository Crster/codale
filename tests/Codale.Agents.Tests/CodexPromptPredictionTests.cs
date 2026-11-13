using System.Text.Json;
using Codale.Agents.Codex;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public sealed class CodexPromptPredictionTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static CodexAgentSession Create(bool enabled, List<JsonElement> requests, bool reject = false)
    {
        CodexAgentSession? session = null;
        session = new(new CodexSessionOptions { WorkingDirectory = ".", PromptSuggestions = enabled })
        {
            MessageWriter = (message, _) =>
            {
                var frame = Json(message);
                requests.Add(frame);
                session!.HandleResponse(reject
                    ? Json(new { id = frame.GetProperty("id").GetInt32(), error = new { code = -32601, message = "Method not found" } })
                    : Json(new { id = frame.GetProperty("id").GetInt32(), result = new { } }));
                return Task.CompletedTask;
            },
        };
        session.AdoptThread("main");
        return session;
    }

    private static Task Complete(CodexAgentSession session, string status = "completed", string thread = "main", string turn = "turn-1") =>
        session.HandleNotificationAsync("turn/completed", Json(new { threadId = thread, turn = new { id = turn, status } }));

    private static Task Predict(CodexAgentSession session, string? text = "Run the tests", string source = "turn-1", string thread = "main", string type = "completed") =>
        session.HandleNotificationAsync("thread/prediction/updated", Json(new
        {
            threadId = thread, sourceTurnId = source, result = new { type, text },
        }));

    private static void Drain(CodexAgentSession session)
    {
        while (session.Events.TryRead(out _)) { }
    }

    [Fact]
    public async Task Successful_turn_requests_prediction_and_publishes_trimmed_text_once()
    {
        var requests = new List<JsonElement>();
        await using var session = Create(true, requests);
        await Complete(session);
        Assert.IsType<TurnCompleted>(await session.Events.ReadAsync());
        var request = Assert.Single(requests);
        Assert.Equal("thread/prediction/request", request.GetProperty("method").GetString());
        Assert.Equal("main", request.GetProperty("params").GetProperty("threadId").GetString());
        Assert.Equal("turn-1", request.GetProperty("params").GetProperty("sourceTurnId").GetString());
        await Predict(session, "  Run the tests \n");
        Assert.Equal("Run the tests", Assert.IsType<PromptSuggested>(await session.Events.ReadAsync()).Text);
        await Predict(session);
        Assert.False(session.Events.TryRead(out _));
    }

    [Theory]
    [InlineData(false, "completed")]
    [InlineData(true, "failed")]
    [InlineData(true, "interrupted")]
    public async Task Disabled_or_unsuccessful_turns_do_not_request_or_publish(bool enabled, string status)
    {
        var requests = new List<JsonElement>();
        await using var session = Create(enabled, requests);
        await Complete(session, status);
        Drain(session);
        await Predict(session);
        Assert.Empty(requests);
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public async Task Foreign_or_stale_predictions_are_ignored_without_discarding_current_request()
    {
        await using var session = Create(true, []);
        await Complete(session);
        Drain(session);
        await Predict(session, thread: "worker");
        await Predict(session, source: "older-turn");
        Assert.False(session.Events.TryRead(out _));
        await Predict(session);
        Assert.IsType<PromptSuggested>(await session.Events.ReadAsync());
    }

    [Theory]
    [InlineData(null, "completed")]
    [InlineData("   ", "completed")]
    [InlineData("ignored", "failed")]
    public async Task Empty_or_failed_prediction_has_no_suggestion(string? text, string type)
    {
        await using var session = Create(true, []);
        await Complete(session);
        Drain(session);
        await Predict(session, text, type: type);
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public async Task Sending_a_new_turn_invalidates_previous_prediction_before_turn_started_notification()
    {
        await using var session = Create(true, []);
        await Complete(session);
        Drain(session);
        await session.SendAsync(new UserTurn("Another task"));
        await Predict(session);
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public async Task Started_turn_invalidates_previous_prediction()
    {
        await using var session = Create(true, []);
        await Complete(session);
        Drain(session);
        await session.HandleNotificationAsync("turn/started", Json(new { threadId = "main", turn = new { id = "turn-2" } }));
        await Predict(session);
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public async Task Unsupported_api_does_not_fail_turn_or_publish_a_prediction()
    {
        await using var session = Create(true, [], reject: true);
        await Complete(session);
        Assert.IsType<TurnCompleted>(await session.Events.ReadAsync());
        await Predict(session);
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public async Task Subagent_completion_does_not_request_prediction()
    {
        var requests = new List<JsonElement>();
        await using var session = Create(true, requests);
        await Complete(session, thread: "worker");
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Prediction_request_does_not_block_notifications_and_is_cancelled_on_disposal()
    {
        var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = ".", PromptSuggestions = true })
        {
            // Leave the RPC response pending, as it would be until the read loop runs again.
            MessageWriter = (_, _) => Task.CompletedTask,
        };
        session.AdoptThread("main");
        try
        {
            await Complete(session).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<TurnCompleted>(await session.Events.ReadAsync());
            await Predict(session);
            Assert.IsType<PromptSuggested>(await session.Events.ReadAsync());
        }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
