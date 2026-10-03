using System.Text.Json;

using Codale.Agents.Claude;

namespace Codale.Agents.Tests;

/// <summary>
/// The CLI blocks on every control_request until the host answers it. Requests other
/// than can_use_tool used to be dropped without a reply, which froze the turn with
/// nothing on screen; they now get an immediate error reply.
/// </summary>
public sealed class UnsupportedControlRequestTests
{
    [Theory]
    [InlineData("hook_callback")]
    [InlineData("mcp_message")]
    [InlineData("elicitation")]
    public void Requests_without_a_handler_are_recognised(string subtype)
    {
        var line = $$$"""{"type":"control_request","request_id":"req-7","request":{"subtype":"{{{subtype}}}","x":1}}""";

        Assert.Equal(("req-7", subtype), ClaudeAgentSession.UnsupportedControlRequest(line));
    }

    [Theory]
    [InlineData("""{"type":"control_request","request_id":"req-1","request":{"subtype":"can_use_tool","tool_name":"Bash"}}""")]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","request_id":"req-1"}}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"a \"control_request\" in prose"}]}}""")]
    [InlineData("not json but mentions \"control_request\"")]
    public void Permission_requests_and_other_lines_are_left_alone(string line)
    {
        Assert.Null(ClaudeAgentSession.UnsupportedControlRequest(line));
    }

    [Fact]
    public void The_reply_is_an_error_response_for_the_same_request()
    {
        var json = ClaudeAgentSession.SerializeFrame(
            ClaudeAgentSession.BuildUnsupportedControlResponse("req-7", "elicitation"));

        var response = JsonDocument.Parse(json).RootElement.GetProperty("response");
        Assert.Equal("control_response", JsonDocument.Parse(json).RootElement.GetProperty("type").GetString());
        Assert.Equal("error", response.GetProperty("subtype").GetString());
        Assert.Equal("req-7", response.GetProperty("request_id").GetString());
        Assert.Contains("elicitation", response.GetProperty("error").GetString());
    }
}
