using System.Net;
using System.Text;
using System.Text.Json;

using Codale.Core.Helper;

namespace Codale.Agents.Tests;

public sealed class AnthropicApiClientTests
{
    private static readonly AnthropicEndpoint Endpoint = new("https://gateway.example", "sk-test", "claude-haiku-4-5");

    [Theory]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://gateway.example/anthropic/v1", "https://gateway.example/anthropic/v1/messages")]
    [InlineData("https://gateway.example/v1/messages", "https://gateway.example/v1/messages")]
    public void The_messages_url_is_built_from_any_base_url_style(string baseUrl, string expected) =>
        Assert.Equal(expected, AnthropicApiClient.MessagesUrl(baseUrl));

    [Fact]
    public void A_single_tool_is_forced_with_its_schema()
    {
        using var request = JsonDocument.Parse(AnthropicApiClient.BuildRequest(
            Endpoint, "Be brief.", "hello", [MessageRouting.RouteTool]));
        var root = request.RootElement;

        Assert.Equal("claude-haiku-4-5", root.GetProperty("model").GetString());
        Assert.Equal("Be brief.", root.GetProperty("system").GetString());
        Assert.Equal("hello", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());

        var choice = root.GetProperty("tool_choice");
        Assert.Equal("tool", choice.GetProperty("type").GetString());
        Assert.Equal("route", choice.GetProperty("name").GetString());

        var schema = root.GetProperty("tools")[0].GetProperty("input_schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(["ask", "plan", "act"],
            schema.GetProperty("properties").GetProperty("intent").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["message", "intent", "related"],
            schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Several_tools_leave_the_choice_to_the_model_but_require_one()
    {
        using var request = JsonDocument.Parse(AnthropicApiClient.BuildRequest(
            Endpoint, "s", "p", [MessageRouting.RouteTool, new ToolDefinition { Name = "answer", Description = "Finish." }]));

        Assert.Equal("any", request.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Fact]
    public void Plain_generation_sends_no_tools()
    {
        using var request = JsonDocument.Parse(AnthropicApiClient.BuildRequest(Endpoint, "s", "p", tools: null));

        Assert.False(request.RootElement.TryGetProperty("tools", out _));
        Assert.False(request.RootElement.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public void A_tool_use_block_becomes_a_validated_call()
    {
        using var reply = JsonDocument.Parse("""
            {"content":[{"type":"text","text":"ok"},
                        {"type":"tool_use","id":"t1","name":"route",
                         "input":{"message":"Fix the button.","intent":"act","related":"yes"}}]}
            """);

        var call = AnthropicApiClient.ReadToolCall(reply.RootElement, [MessageRouting.RouteTool]);

        Assert.Equal("route", call!.Tool);
        Assert.Equal("Fix the button.", call.GetString("message"));
        Assert.Equal("act", call.GetString("intent"));
    }

    [Fact]
    public void Every_valid_tool_use_block_of_a_reply_is_kept_in_order()
    {
        using var reply = JsonDocument.Parse("""
            {"content":[{"type":"tool_use","name":"route","input":{"message":"a","intent":"act","related":"yes"}},
                        {"type":"tool_use","name":"route","input":{"message":"b","intent":"maybe","related":"yes"}},
                        {"type":"tool_use","name":"route","input":{"message":"c","intent":"ask","related":"no"}}]}
            """);

        var calls = AnthropicApiClient.ReadToolCalls(reply.RootElement, [MessageRouting.RouteTool]);

        // The middle call's intent is outside the allowed list: dropped, not the whole reply.
        Assert.Equal(["a", "c"], calls.Select(c => c.GetString("message")));
    }

    [Fact]
    public void A_call_with_a_value_outside_the_allowed_list_is_refused()
    {
        using var reply = JsonDocument.Parse("""
            {"content":[{"type":"tool_use","name":"route","input":{"message":"x","intent":"maybe","related":"yes"}}]}
            """);

        Assert.Null(AnthropicApiClient.ReadToolCall(reply.RootElement, [MessageRouting.RouteTool]));
    }

    [Fact]
    public void A_gateway_that_answers_in_text_is_read_as_the_json_contract()
    {
        using var reply = JsonDocument.Parse(
            """{"content":[{"type":"text","text":"{\"tool\":\"route\",\"arguments\":{\"message\":\"x\",\"intent\":\"ask\",\"related\":\"no\"}}"}]}""");

        Assert.Equal("ask", AnthropicApiClient.ReadToolCall(reply.RootElement, [MessageRouting.RouteTool])!.GetString("intent"));
    }

    [Fact]
    public void Nothing_usable_is_null()
    {
        using var reply = JsonDocument.Parse("""{"content":[{"type":"text","text":"I cannot help."}]}""");

        Assert.Null(AnthropicApiClient.ReadToolCall(reply.RootElement, [MessageRouting.RouteTool]));
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", "invalid x-api-key")]
    [InlineData("upstream timeout", "upstream timeout")]
    public void An_error_body_is_reduced_to_its_message(string body, string expected) =>
        Assert.Equal(expected, AnthropicApiClient.ErrorMessage(body));

    [Fact]
    public void The_helper_is_available_through_the_api_without_any_cli()
    {
        using var helper = new HelperModel(() => Endpoint);

        Assert.True(helper.IsAvailable);

        // Nothing to start: a warm-up request must not spawn anything or throw.
        helper.Prewarm(MessageRouting.SystemPrompt, [MessageRouting.RouteTool]);
    }

    /// <summary>A loopback Messages API: records the request and answers with a canned reply.</summary>
    private sealed class FakeApi : IDisposable
    {
        private readonly HttpListener _listener = new();

        public FakeApi(int status, string reply)
        {
            var port = new Random().Next(20000, 40000);
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{port}";

            Served = Task.Run(async () =>
            {
                var context = await _listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                Seen = (context.Request.Url!.AbsolutePath, context.Request.Headers["x-api-key"],
                        context.Request.Headers["anthropic-version"], await reader.ReadToEndAsync());

                var bytes = Encoding.UTF8.GetBytes(reply);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            });
        }

        public string BaseUrl { get; }

        public Task Served { get; }

        public (string Path, string? Key, string? Version, string Body) Seen { get; private set; }

        public void Dispose() => _listener.Close();
    }

    [Fact]
    public async Task A_tool_call_round_trips_over_http_with_the_key_and_version_headers()
    {
        using var api = new FakeApi(200,
            """{"content":[{"type":"tool_use","name":"route","input":{"message":"Fix the button.","intent":"act","related":"yes"}}]}""");

        var call = await AnthropicApiClient.CallToolAsync(
            new AnthropicEndpoint(api.BaseUrl, "sk-live", "m"), "sys", "fix teh button", [MessageRouting.RouteTool], default);
        await api.Served;

        Assert.Equal("/v1/messages", api.Seen.Path);
        Assert.Equal("sk-live", api.Seen.Key);
        Assert.Equal("2023-06-01", api.Seen.Version);
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
            AnthropicApiClient.CompleteAsync(new AnthropicEndpoint(baseUrl, "sk-secret", "m"), "sys", "hi", default));

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
        Assert.Equal(safe, AnthropicApiClient.IsSafeToSendKey(new Uri(url)));

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task A_malformed_base_url_is_a_helper_error_not_a_raw_exception(string baseUrl) =>
        await Assert.ThrowsAsync<HelperModelException>(() =>
            AnthropicApiClient.CompleteAsync(new AnthropicEndpoint(baseUrl, "", "m"), "sys", "hi", default));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"content":"text"}""")]
    [InlineData("""{"content":[{"type":"text"}]}""")]
    [InlineData("[1,2]")]
    public async Task A_reply_without_the_expected_shape_is_a_helper_error(string reply)
    {
        using var api = new FakeApi(200, reply);

        await Assert.ThrowsAsync<HelperModelException>(() =>
            AnthropicApiClient.CompleteAsync(new AnthropicEndpoint(api.BaseUrl, "k", "m"), "sys", "diff", default));
    }

    [Fact]
    public async Task Plain_generation_returns_the_reply_text()
    {
        using var api = new FakeApi(200, """{"content":[{"type":"text","text":"feat: add dark theme"}]}""");

        var text = await AnthropicApiClient.CompleteAsync(new AnthropicEndpoint(api.BaseUrl, "k", "m"), "sys", "diff", default);

        Assert.Equal("feat: add dark theme", text);
    }

    [Fact]
    public async Task An_http_error_carries_the_apis_message()
    {
        using var api = new FakeApi(401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");

        var ex = await Assert.ThrowsAsync<HelperModelException>(() => AnthropicApiClient.CompleteAsync(
            new AnthropicEndpoint(api.BaseUrl, "bad", "m"), "sys", "p", default));

        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid x-api-key", ex.Message);
    }

    [Fact]
    public async Task The_helper_model_uses_the_api_when_an_endpoint_is_set()
    {
        using var api = new FakeApi(200,
            """{"content":[{"type":"tool_use","name":"route","input":{"message":"Hi.","intent":"ask","related":"no"}}]}""");
        using var helper = new HelperModel(
            () => new AnthropicEndpoint(api.BaseUrl, "k", "m"));

        var call = await helper.CallToolAsync("sys", "hi", [MessageRouting.RouteTool]);

        Assert.Equal("ask", call!.GetString("intent"));
    }
}
