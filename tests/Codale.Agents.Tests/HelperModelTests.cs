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
    public void Claude_runs_haiku_on_its_login_and_the_providers_flash_model_behind_a_byok_override()
    {
        var endpoint = new HelperCliEndpoint("glm-flash", new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = "https://byok.example/anthropic" });

        var login = HelperModel.BuildArguments("x").ToList();
        var byok = HelperModel.BuildStreamArguments("x", endpoint.Model).ToList();

        Assert.Equal(HelperModel.ClaudeModel, login[login.IndexOf("--model") + 1]);
        Assert.Equal("haiku", HelperModel.ClaudeModel);
        Assert.Equal("glm-flash", byok[byok.IndexOf("--model") + 1]);
        Assert.Equal("https://byok.example/anthropic", HelperModel.BuildStartInfo("claude", endpoint).Environment["ANTHROPIC_BASE_URL"]);
    }

    [Fact]
    public void Codex_background_jobs_default_to_luna()
    {
        Assert.Equal("gpt-5.6-luna", HelperModel.DefaultCodexModel);
    }

    [Fact]
    public void A_spare_for_the_login_is_never_one_for_a_byok_model()
    {
        var endpoint = new HelperCliEndpoint("glm-flash", new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = "https://byok.example" });

        Assert.Equal("prompt", HelperModel.SpareKey("prompt", null));
        Assert.NotEqual(HelperModel.SpareKey("prompt", null), HelperModel.SpareKey("prompt", endpoint));
        Assert.NotEqual(
            HelperModel.SpareKey("prompt", endpoint),
            HelperModel.SpareKey("prompt", endpoint with { Model = "glm-pro" }));
    }

    [Fact]
    public void Codex_provider_arguments_match_the_chat_sessions()
    {
        var provider = new Codex.CodexCustomProvider("My Gateway", "https://api.example.com/v1");
        var session = string.Join(' ', new Codex.CodexSessionOptions { WorkingDirectory = ".", CustomProvider = provider }.BuildArguments());

        Assert.Contains(string.Join(' ', Codex.CodexSessionOptions.ProviderArguments(provider)), session);
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

    [Fact]
    public void A_json_run_gives_its_reply_and_what_it_spent()
    {
        Codale.Core.Agents.UsageSnapshot? seen = null;
        var reply = HelperModel.ReadJsonResult(
            """{"type":"result","is_error":false,"result":"hi","usage":{"input_tokens":12,"output_tokens":3}}""", u => seen = u);

        Assert.Equal("hi", reply);
        Assert.Equal(12, seen!.Value.InputTokens);
        Assert.Equal(3, seen.Value.OutputTokens);
        Assert.Equal("plain text", HelperModel.ReadJsonResult("plain text", null));
    }

    [Fact]
    public void Codex_reports_one_token_total()
    {
        Assert.Equal(1234, HelperModel.ReadCodexTokens("done" + Environment.NewLine + "tokens used" + Environment.NewLine + "1,234"));
        Assert.Equal(0, HelperModel.ReadCodexTokens("no counts here"));
    }
}