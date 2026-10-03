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
}
