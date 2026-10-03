namespace Codale.Agents.Tests;

public sealed class HelperModelTests
{
    [Fact]
    public void Claude_runs_tool_less_and_unsaved_with_the_system_prompt_as_a_flag()
    {
        var args = HelperModel.BuildArguments("Be brief.");

        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("", args[args.ToList().IndexOf("--tools") + 1]);
        Assert.Equal("Be brief.", args[args.ToList().IndexOf("--system-prompt") + 1]);
    }

    [Fact]
    public void Claude_skips_mcp_servers_and_thinking()
    {
        Assert.Contains("--strict-mcp-config", HelperModel.BuildArguments("x"));
        Assert.Equal("0", HelperModel.BuildStartInfo("claude").Environment["MAX_THINKING_TOKENS"]);
    }

    [Fact]
    public void A_warm_claude_reads_its_request_as_stream_json()
    {
        var args = HelperModel.BuildStreamArguments("Be brief.").ToList();

        Assert.Equal("stream-json", args[args.IndexOf("--input-format") + 1]);
        Assert.Equal("stream-json", args[args.IndexOf("--output-format") + 1]);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Contains("--no-session-persistence", args);
        Assert.Equal("Be brief.", args[args.IndexOf("--system-prompt") + 1]);
    }

    [Fact]
    public void The_request_is_one_user_line_with_newlines_escaped()
    {
        var line = HelperModel.BuildStreamRequest("Title: x\nNew message:\nsay \"hi\"");

        Assert.DoesNotContain('\n', line);
        using var document = System.Text.Json.JsonDocument.Parse(line);
        Assert.Equal("user", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("Title: x\nNew message:\nsay \"hi\"",
            document.RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void The_result_event_carries_the_reply_and_other_events_are_skipped()
    {
        Assert.Null(HelperModel.ReadStreamResult("""{"type":"system","subtype":"init"}"""));
        Assert.Null(HelperModel.ReadStreamResult("""{"type":"assistant","message":{"content":[{"type":"text","text":"\"result\""}]}}"""));
        Assert.Null(HelperModel.ReadStreamResult("not json \"result\""));
        Assert.Equal("{\"tool\":\"route\"}", HelperModel.ReadStreamResult(
            """{"type":"result","subtype":"success","is_error":false,"result":"{\"tool\":\"route\"}"}"""));
    }

    [Fact]
    public void A_failed_result_is_an_error_not_a_reply()
    {
        Assert.Throws<Codale.Core.Helper.HelperModelException>(() => HelperModel.ReadStreamResult(
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Not logged in"}"""));
        Assert.Throws<Codale.Core.Helper.HelperModelException>(() => HelperModel.ReadStreamResult(
            """{"type":"result","is_error":false,"result":""}"""));
    }
}
