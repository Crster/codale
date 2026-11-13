using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// Pins the exact shape of the frame that answers a <c>can_use_tool</c> request.
/// Getting this wrong fails silently: the CLI accepts the frame, then reports
/// "the canUseTool callback returned an invalid permission result" as a tool_result
/// error, the tool never runs, and the UI shows an approval that apparently did nothing.
/// </summary>
public sealed class ApprovalResponseTests
{
    private static JsonElement Response(ApprovalDecision decision)
    {
        var json = ClaudeAgentSession.SerializeFrame(
            ClaudeAgentSession.BuildApprovalResponse("req-1", decision));

        return JsonDocument.Parse(json).RootElement
            .GetProperty("response").GetProperty("response").Clone();
    }

    [Fact]
    public void Allow_without_an_edit_omits_updated_input_entirely()
    {
        var response = Response(ApprovalDecision.Allow());

        Assert.Equal("allow", response.GetProperty("behavior").GetString());

        // Must be absent, not null: null is what the CLI rejects.
        Assert.False(response.TryGetProperty("updatedInput", out _));
    }

    [Fact]
    public void Allow_with_an_edited_input_sends_it_as_an_object()
    {
        using var input = JsonDocument.Parse("""{"file_path":"a.txt","content":"edited"}""");

        var response = Response(new ApprovalDecision
        {
            Behavior = ApprovalBehavior.Allow,
            UpdatedInput = input.RootElement.Clone(),
        });

        var updated = response.GetProperty("updatedInput");
        Assert.Equal(JsonValueKind.Object, updated.ValueKind);
        Assert.Equal("edited", updated.GetProperty("content").GetString());
    }

    [Fact]
    public void Deny_always_carries_a_message_for_the_model_to_read()
    {
        var response = Response(ApprovalDecision.Deny("Not allowed to touch that file."));

        Assert.Equal("deny", response.GetProperty("behavior").GetString());
        Assert.Equal("Not allowed to touch that file.", response.GetProperty("message").GetString());
    }

    [Fact]
    public void Deny_without_a_reason_still_sends_a_non_empty_message()
    {
        var response = Response(ApprovalDecision.Deny());

        Assert.Equal("deny", response.GetProperty("behavior").GetString());
        Assert.False(string.IsNullOrWhiteSpace(response.GetProperty("message").GetString()));
    }

    [Fact]
    public void Envelope_correlates_the_answer_with_the_request()
    {
        var json = ClaudeAgentSession.SerializeFrame(
            ClaudeAgentSession.BuildApprovalResponse("req-42", ApprovalDecision.Allow()));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("control_response", root.GetProperty("type").GetString());

        var response = root.GetProperty("response");
        Assert.Equal("success", response.GetProperty("subtype").GetString());
        Assert.Equal("req-42", response.GetProperty("request_id").GetString());
    }

    [Fact]
    public void Accepted_directory_suggestion_is_returned_verbatim_as_updated_permissions()
    {
        var parser = new ClaudeStreamParser();
        var request = Assert.IsType<ApprovalRequested>(Assert.Single(parser.Parse(
            """{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"},"permission_suggestions":[{"type":"addDirectories","directories":["X:\\repo\\src"],"destination":"session"}]}}""")));
        var suggestion = Assert.Single(request.Suggestions);
        Assert.Equal("Allow access to src", suggestion.ToDisplayLabel());

        var response = Response(new ApprovalDecision { Behavior = ApprovalBehavior.Allow, AcceptedSuggestion = suggestion });

        var update = Assert.Single(response.GetProperty("updatedPermissions").EnumerateArray().ToList());
        Assert.Equal("addDirectories", update.GetProperty("type").GetString());
        Assert.Equal("X:\\repo\\src", update.GetProperty("directories")[0].GetString());
    }

    [Fact]
    public void Mode_suggestion_and_plain_allow_send_no_permission_update()
    {
        var mode = new PermissionSuggestion { Type = "setMode", Mode = "acceptEdits", Destination = "session" };
        Assert.False(Response(new ApprovalDecision { Behavior = ApprovalBehavior.Allow, AcceptedSuggestion = mode })
            .TryGetProperty("updatedPermissions", out _));
        Assert.False(Response(ApprovalDecision.Allow()).TryGetProperty("updatedPermissions", out _));
    }

    [Theory]
    [InlineData("setMode", "acceptEdits", "session", "Allow all edits this session")]
    [InlineData("removeDirectories", null, "session", "Remove directories")]
    [InlineData("addDirectories", null, "session", "Allow folder access")]
    public void Suggestion_labels_are_words_not_identifiers(string type, string? mode, string destination, string expected) =>
        Assert.Equal(expected, new PermissionSuggestion { Type = type, Mode = mode, Destination = destination }.ToDisplayLabel());

    [Fact]
    public void Rule_suggestion_names_the_command()
    {
        var rule = new PermissionSuggestion
        {
            Type = "addRules",
            Behavior = "allow",
            Destination = "localSettings",
            Rules = [("Bash", "npm test:*")],
        };

        Assert.Equal("Always allow “npm test”", rule.ToDisplayLabel());
        Assert.Contains("settings.local.json", rule.ToDisplayDetail());
    }
}