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
