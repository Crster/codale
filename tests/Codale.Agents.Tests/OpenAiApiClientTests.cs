using System.Net;
using System.Text;
using System.Text.Json;

using Codale.Agents.OpenAi;
using Codale.Core.Helper;

namespace Codale.Agents.Tests;

public sealed class OpenAiApiClientTests
{
    private static readonly OpenAiEndpoint Endpoint = new("https://gateway.example/v1", "sk-test", "deepseek-chat");

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://gateway.example/v1/chat/completions", "https://gateway.example/v1/chat/completions")]
    public void The_chat_completions_url_is_built_from_any_base_url_style(string baseUrl, string expected) =>
        Assert.Equal(expected, OpenAiApiClient.ChatCompletionsUrl(baseUrl));

    [Fact]
    public void A_single_tool_is_forced_with_its_schema()
    {
        using var request = JsonDocument.Parse(OpenAiApiClient.BuildRequest(
            Endpoint, "Be brief.", "hello", [MessageRouting.RouteTool]));
        var root = request.RootElement;

        Assert.Equal("deepseek-chat", root.GetProperty("model").GetString());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Be brief.", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("hello" + OpenAiApiClient.NoThink, root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());

        var choice = root.GetProperty("tool_choice");
        Assert.Equal("function", choice.GetProperty("type").GetString());
        Assert.Equal("route", choice.GetProperty("function").GetProperty("name").GetString());

        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        var schema = tool.GetProperty("function").GetProperty("parameters");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(["ask", "plan", "act"],
            schema.GetProperty("properties").GetProperty("intent").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["intent", "related"],
            schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Several_tools_leave_the_choice_to_the_model_but_require_one()
    {
        using var request = JsonDocument.Parse(OpenAiApiClient.BuildRequest(
            Endpoint, "s", "p", [MessageRouting.RouteTool, new ToolDefinition { Name = "answer", Description = "Finish." }]));

        Assert.Equal("required", request.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public void An_untuned_request_leaves_temperature_to_the_model()
    {
        using var request = JsonDocument.Parse(OpenAiApiClient.BuildRequest(Endpoint, "s", "p", tools: null, tuned: false));

        Assert.False(request.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void Plain_generation_sends_no_tools()
    {
        using var request = JsonDocument.Parse(OpenAiApiClient.BuildRequest(Endpoint, "s", "p", tools: null));

        Assert.False(request.RootElement.TryGetProperty("tools", out _));
        Assert.False(request.RootElement.TryGetProperty("tool_choice", out _));
    }

    private static string Reply(string message) => $$"""{"choices":[{"message":{{message}},"finish_reason":"stop"}]}""";

    [Fact]
    public void A_tool_call_becomes_a_validated_call()
    {
        using var reply = JsonDocument.Parse(Reply("""
            {"content":"ok","tool_calls":[{"id":"c1","type":"function","function":{"name":"route",
             "arguments":"{\"message\":\"Fix the button.\",\"intent\":\"act\",\"related\":\"yes\"}"}}]}
            """));

        var call = OpenAiApiClient.ReadToolCall(reply.RootElement, [MessageRouting.RouteTool]);

        Assert.Equal("route", call!.Tool);
        Assert.Equal("Fix the button.", call.GetString("message"));
        Assert.Equal("act", call.GetString("intent"));
    }

    [Fact]
    public void Every_valid_tool_call_of_a_reply_is_kept_in_order()
    {
        using var reply = JsonDocument.Parse(Reply("""
            {"tool_calls":[
              {"function":{"name":"route","arguments":"{\"message\":\"a\",\"intent\":\"act\",\"related\":\"yes\"}"}},
              {"function":{"name":"route","arguments":"{\"message\":\"b\",\"intent\":\"maybe\",\"related\":\"yes\"}"}},
              {"function":{"name":"route","arguments":"{\"message\":\"c\",\"intent\":\"ask\",\"related\":\"no\"}"}}]}
            """));

        var calls = OpenAiApiClient.ReadToolCalls(reply.RootElement, [MessageRouting.RouteTool]);

        // The middle call's intent is outside the allowed list: dropped, not the whole reply.
        Assert.Equal(["a", "c"], calls.Select(c => c.GetString("message")));
    }

    [Fact]
    public void A_call_with_a_value_outside_the_allowed_list_is_refused()
    {
        using var reply = JsonDocument.Parse(Reply("""
            {"tool_calls":[{"function":{"name":"route","arguments":"{\"message\":\"x\",\"intent\":\"maybe\",\"related\":\"yes\"}"}}]}
            """));

        Assert.Null(OpenAiApiClient.ReadToolCall(reply.RootElement, [MessageRouting.RouteTool]));
    }

    [Fact]
    public void A_provider_that_answers_in_text_is_read_as_the_json_contract()
    {
        using var reply = JsonDocument.Parse(Reply(
            """{"content":"{\"tool\":\"route\",\"arguments\":{\"message\":\"x\",\"intent\":\"ask\",\"related\":\"no\"}}"}"""));

        Assert.Equal("ask", OpenAiApiClient.ReadToolCall(reply.RootElement, [MessageRouting.RouteTool])!.GetString("intent"));
    }

    [Fact]
    public void Nothing_usable_is_null()
    {
        using var reply = JsonDocument.Parse(Reply("""{"content":"I cannot help."}"""));

        Assert.Null(OpenAiApiClient.ReadToolCall(reply.RootElement, [MessageRouting.RouteTool]));
    }

    [Fact]
    public void Usage_reports_cached_prompt_tokens_apart()
    {
        using var reply = JsonDocument.Parse(
            """{"usage":{"prompt_tokens":100,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":60}}}""");

        var usage = OpenAiApiClient.ReadUsage(reply.RootElement);

        Assert.Equal(40, usage.InputTokens);
        Assert.Equal(60, usage.CacheReadInputTokens);
        Assert.Equal(7, usage.OutputTokens);
    }

    [Theory]
    [InlineData("""{"error":{"message":"Incorrect API key provided","type":"invalid_request_error"}}""", "Incorrect API key provided")]
    [InlineData("""{"error":"model not found"}""", "model not found")]
    [InlineData("upstream timeout", "upstream timeout")]
    public void An_error_body_is_reduced_to_its_message(string body, string expected) =>
        Assert.Equal(expected, OpenAiApiClient.ErrorMessage(body));

    [Fact]
    public void The_helper_is_available_through_the_api_without_any_cli()
    {
        using var helper = new HelperModel(() => Endpoint);

        Assert.True(helper.IsAvailable);

        // Nothing to start: a warm-up request must not spawn anything or throw.
        helper.Prewarm(MessageRouting.SystemPrompt, [MessageRouting.RouteTool]);
    }

    /// <summary>A loopback Chat Completions API: records the request and answers with a canned reply.</summary>
    private sealed class FakeApi : IDisposable
    {
        private readonly HttpListener _listener = new();

        public FakeApi(int status, string reply)
        {
            var port = new Random().Next(20000, 40000);
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{port}/v1";

            Served = Task.Run(async () =>
            {
                var context = await _listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                Seen = (context.Request.Url!.AbsolutePath, context.Request.Headers["Authorization"], await reader.ReadToEndAsync());

                var bytes = Encoding.UTF8.GetBytes(reply);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            });
        }

        public string BaseUrl { get; }

        public Task Served { get; }

        public (string Path, string? Authorization, string Body) Seen { get; private set; }

        public void Dispose() => _listener.Close();
    }

    [Fact]
    public async Task A_tool_call_round_trips_over_http_with_a_bearer_key()
    {
        using var api = new FakeApi(200, Reply("""
            {"tool_calls":[{"function":{"name":"route","arguments":"{\"message\":\"Fix the button.\",\"intent\":\"act\",\"related\":\"yes\"}"}}]}
            """));

        var call = await OpenAiApiClient.CallToolAsync(
            new OpenAiEndpoint(api.BaseUrl, "sk-live", "m"), "sys", "fix teh button", [MessageRouting.RouteTool], default);
        await api.Served;

        Assert.Equal("/v1/chat/completions", api.Seen.Path);
        Assert.Equal("Bearer sk-live", api.Seen.Authorization);
        Assert.Contains("fix teh button", api.Seen.Body);
        Assert.Equal("Fix the button.", call!.GetString("message"));
    }

    [Theory]
    [InlineData("http://gateway.example")]
    [InlineData("http://203.0.113.9:8080/v1")]
    [InlineData("ftp://gateway.example")]
    public async Task A_key_is_never_sent_over_a_clear_text_or_unknown_scheme(string baseUrl)
    {
        var ex = await Assert.ThrowsAsync<HelperModelException>(() =>
            OpenAiApiClient.CompleteAsync(new OpenAiEndpoint(baseUrl, "sk-secret", "m"), "sys", "hi", default));

        Assert.Contains("Refusing", ex.Message);
        Assert.DoesNotContain("sk-secret", ex.Message);
    }

    [Theory]
    [InlineData("https://gateway.example", true)]
    [InlineData("http://127.0.0.1:9000", true)]
    [InlineData("http://localhost:9000", true)]
    [InlineData("http://[::1]:9000", true)]
    [InlineData("http://gateway.example", false)]
    public void Only_https_or_loopback_may_carry_a_key(string url, bool safe) =>
        Assert.Equal(safe, OpenAiApiClient.IsSafeToSendKey(new Uri(url)));

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task A_malformed_base_url_is_a_helper_error_not_a_raw_exception(string baseUrl) =>
        await Assert.ThrowsAsync<HelperModelException>(() =>
            OpenAiApiClient.CompleteAsync(new OpenAiEndpoint(baseUrl, "", "m"), "sys", "hi", default));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"choices":"text"}""")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""")]
    [InlineData("[1,2]")]
    public async Task A_reply_without_the_expected_shape_is_a_helper_error(string reply)
    {
        using var api = new FakeApi(200, reply);

        await Assert.ThrowsAsync<HelperModelException>(() =>
            OpenAiApiClient.CompleteAsync(new OpenAiEndpoint(api.BaseUrl, "k", "m"), "sys", "diff", default));
    }

    [Fact]
    public async Task Plain_generation_returns_the_reply_text()
    {
        using var api = new FakeApi(200, Reply("""{"content":"feat: add dark theme"}"""));

        var text = await OpenAiApiClient.CompleteAsync(new OpenAiEndpoint(api.BaseUrl, "k", "m"), "sys", "diff", default);

        Assert.Equal("feat: add dark theme", text);
    }

    [Fact]
    public async Task An_http_error_carries_the_apis_message()
    {
        using var api = new FakeApi(401, """{"error":{"message":"Incorrect API key provided","type":"invalid_request_error"}}""");

        var ex = await Assert.ThrowsAsync<HelperModelException>(() => OpenAiApiClient.CompleteAsync(
            new OpenAiEndpoint(api.BaseUrl, "bad", "m"), "sys", "p", default));

        Assert.Contains("401", ex.Message);
        Assert.Contains("Incorrect API key provided", ex.Message);
    }

    [Fact]
    public async Task The_helper_model_uses_the_api_when_an_endpoint_is_set()
    {
        using var api = new FakeApi(200, Reply("""
            {"tool_calls":[{"function":{"name":"route","arguments":"{\"message\":\"Hi.\",\"intent\":\"ask\",\"related\":\"no\"}"}}]}
            """));
        using var helper = new HelperModel(() => new OpenAiEndpoint(api.BaseUrl, "k", "m"));

        var call = await helper.CallToolAsync("sys", "hi", [MessageRouting.RouteTool]);

        Assert.Equal("ask", call!.GetString("intent"));
    }
}
