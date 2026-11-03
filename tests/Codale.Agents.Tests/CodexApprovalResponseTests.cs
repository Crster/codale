using System.Text.Json;

using Codale.Agents.Codex;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// Pins the shapes of Codex approval and user-input responses matching Codex app-server protocol schemas:
/// <c>ToolRequestUserInputResponse</c> and <c>CommandExecutionRequestApprovalResponse</c>.
/// </summary>
public sealed class CodexApprovalResponseTests
{
    private static JsonElement Serialize(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    [Fact]
    public async Task A_request_codex_clears_itself_closes_its_card_but_our_own_answer_does_not()
    {
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." })
        {
            MessageWriter = (_, _) => Task.CompletedTask,
        };
        async Task<ApprovalRequested> Ask(int id)
        {
            await session.HandleServerRequestAsync(Serialize(id), "item/commandExecution/requestApproval", Serialize(new
            {
                @params = new { threadId = "main", itemId = "cmd-" + id, command = "rm -rf build" },
            }));
            Assert.True(session.Events.TryRead(out var requested));
            return Assert.IsType<ApprovalRequested>(requested);
        }

        // Interrupted while open: codex resolves it itself, and the host hears the card is gone.
        await Ask(7);
        await session.HandleNotificationAsync("serverRequest/resolved", Serialize(new { @params = new { threadId = "main", requestId = 7 } }));
        Assert.True(session.Events.TryRead(out var withdrawn));
        Assert.Equal("7", Assert.IsType<ApprovalCancelled>(withdrawn).RequestId);

        // Answered by us: the same notification follows, and is not a withdrawal.
        var answered = await Ask(8);
        await session.RespondToApprovalAsync(answered.RequestId, ApprovalDecision.Deny());
        await session.HandleNotificationAsync("serverRequest/resolved", Serialize(new { @params = new { threadId = "main", requestId = 8 } }));
        Assert.False(session.Events.TryRead(out _));
    }

    [Theory]
    [InlineData("question-item", true)]
    [InlineData(null, true)]
    [InlineData("question-item", false)]
    [InlineData(null, false)]
    public async Task Answering_a_question_completes_its_transcript_row_once(string? itemId, bool allow)
    {
        var frames = new List<JsonElement>();
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." })
        {
            MessageWriter = (frame, _) =>
            {
                frames.Add(Serialize(frame));
                return Task.CompletedTask;
            },
        };
        await session.HandleServerRequestAsync(Serialize(7), "item/tool/requestUserInput", Serialize(new
        {
            @params = new
            {
                threadId = "main", itemId,
                questions = new[] { new { id = "choice", question = "Which option?" } },
            },
        }));
        Assert.True(session.Events.TryRead(out var requested));
        var approval = Assert.IsType<ApprovalRequested>(requested);
        Assert.Equal(itemId ?? "7", approval.ToolUseId);
        Assert.False(session.Events.TryRead(out _));

        var decision = allow
            ? new ApprovalDecision
            {
                Behavior = ApprovalBehavior.Allow,
                UpdatedInput = Serialize(new { answers = new { choice = "Selected" } }),
            }
            : ApprovalDecision.Deny();
        await session.RespondToApprovalAsync(approval.RequestId, decision);

        var response = Assert.Single(frames);
        Assert.Equal(7, response.GetProperty("id").GetInt32());
        var answers = response.GetProperty("result").GetProperty("answers");
        if (allow)
            Assert.Equal("Selected", answers.GetProperty("choice").GetProperty("answers")[0].GetString());
        else
            Assert.Empty(answers.EnumerateObject());
        Assert.True(session.Events.TryRead(out var ended));
        var completed = Assert.IsType<ToolCallCompleted>(ended);
        Assert.Equal(approval.ToolUseId, completed.ToolUseId);
        Assert.Equal(!allow, completed.IsError);
        Assert.False(session.Events.TryRead(out _));

        await session.RespondToApprovalAsync(approval.RequestId, decision);
        Assert.Single(frames);
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public async Task Failed_answer_delivery_does_not_report_question_completion()
    {
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." })
        {
            MessageWriter = (_, _) => Task.FromException(new IOException("Connection closed")),
        };
        await session.HandleServerRequestAsync(Serialize(7), "item/tool/requestUserInput", Serialize(new
        {
            @params = new { itemId = "question-item", questions = Array.Empty<object>() },
        }));
        Assert.True(session.Events.TryRead(out _));

        await Assert.ThrowsAsync<IOException>(() => session.RespondToApprovalAsync("7", ApprovalDecision.Allow()));
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public async Task Allowing_a_command_waits_for_its_actual_completion()
    {
        await using var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." })
        {
            MessageWriter = (_, _) => Task.CompletedTask,
        };
        await session.HandleServerRequestAsync(Serialize(7), "item/commandExecution/requestApproval", Serialize(new
        {
            @params = new { itemId = "command-item", command = "dotnet build" },
        }));
        Assert.True(session.Events.TryRead(out _));

        await session.RespondToApprovalAsync("7", ApprovalDecision.Allow());
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public void RequestUserInput_with_answers_maps_to_question_id_with_answers_array()
    {
        using var originalDoc = JsonDocument.Parse("""
        {
            "questions": [
                {
                    "id": "db_choice",
                    "header": "Database",
                    "question": "Which database would you prefer?",
                    "options": [
                        { "label": "PostgreSQL", "description": "Relational" },
                        { "label": "SQLite", "description": "Embedded" }
                    ]
                }
            ]
        }
        """);

        using var updatedDoc = JsonDocument.Parse("""
        {
            "questions": [],
            "answers": {
                "db_choice": "PostgreSQL"
            }
        }
        """);

        var decision = new ApprovalDecision
        {
            Behavior = ApprovalBehavior.Allow,
            UpdatedInput = updatedDoc.RootElement.Clone(),
        };

        var result = Serialize(CodexAgentSession.BuildRequestUserInputResult(originalDoc.RootElement, decision));

        Assert.True(result.TryGetProperty("answers", out var answers));
        Assert.True(answers.TryGetProperty("db_choice", out var dbAnswer));
        Assert.True(dbAnswer.TryGetProperty("answers", out var items));
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal("PostgreSQL", items[0].GetString());
    }

    [Fact]
    public void RequestUserInput_matches_by_question_text_when_id_differs()
    {
        using var originalDoc = JsonDocument.Parse("""
        {
            "questions": [
                {
                    "id": "q1",
                    "header": "Auth",
                    "question": "How should users authenticate?",
                    "options": [
                        { "label": "OAuth", "description": "Social" }
                    ]
                }
            ]
        }
        """);

        using var updatedDoc = JsonDocument.Parse("""
        {
            "answers": {
                "How should users authenticate?": "OAuth"
            }
        }
        """);

        var decision = new ApprovalDecision
        {
            Behavior = ApprovalBehavior.Allow,
            UpdatedInput = updatedDoc.RootElement.Clone(),
        };

        var result = Serialize(CodexAgentSession.BuildRequestUserInputResult(originalDoc.RootElement, decision));

        Assert.True(result.TryGetProperty("answers", out var answers));
        Assert.True(answers.TryGetProperty("q1", out var qAnswer));
        var items = qAnswer.GetProperty("answers");
        Assert.Equal("OAuth", items[0].GetString());
    }

    [Fact]
    public void RequestUserInput_supports_multiselect_arrays()
    {
        using var originalDoc = JsonDocument.Parse("""
        {
            "questions": [
                {
                    "id": "features",
                    "question": "Which features to include?"
                }
            ]
        }
        """);

        using var updatedDoc = JsonDocument.Parse("""
        {
            "answers": {
                "features": ["Auth", "Logging", "Metrics"]
            }
        }
        """);

        var decision = new ApprovalDecision
        {
            Behavior = ApprovalBehavior.Allow,
            UpdatedInput = updatedDoc.RootElement.Clone(),
        };

        var result = Serialize(CodexAgentSession.BuildRequestUserInputResult(originalDoc.RootElement, decision));

        var items = result.GetProperty("answers").GetProperty("features").GetProperty("answers");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal("Auth", items[0].GetString());
        Assert.Equal("Logging", items[1].GetString());
        Assert.Equal("Metrics", items[2].GetString());
    }

    [Fact]
    public void RequestUserInput_denial_or_skip_returns_empty_answers_map()
    {
        using var originalDoc = JsonDocument.Parse("""
        {
            "questions": [
                { "id": "q1", "question": "Something?" }
            ]
        }
        """);

        var decision = ApprovalDecision.Deny("The user chose not to answer.");

        var result = Serialize(CodexAgentSession.BuildRequestUserInputResult(originalDoc.RootElement, decision));

        Assert.True(result.TryGetProperty("answers", out var answers));
        Assert.Equal(JsonValueKind.Object, answers.ValueKind);
        Assert.False(answers.EnumerateObject().Any());
    }
}
