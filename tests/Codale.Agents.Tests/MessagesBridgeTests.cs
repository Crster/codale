using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Codale.Agents.OpenAi;

namespace Codale.Agents.Tests;

public sealed class MessagesBridgeTests
{
    private static JsonElement ChatRequest(string anthropicRequest, IReadOnlySet<string>? omit = null)
    {
        using var request = JsonDocument.Parse(anthropicRequest);
        return JsonDocument.Parse(MessagesTranslator.ToChatRequest(request.RootElement, "deepseek-chat", omit)).RootElement.Clone();
    }

    [Fact]
    public void A_conversation_with_tool_use_maps_to_chat_messages_in_order()
    {
        var chat = ChatRequest("""
            {"model":"claude-sonnet-5-5","max_tokens":32000,"stream":true,
             "system":[{"type":"text","text":"You are Claude Code."},{"type":"text","text":"Be terse.","cache_control":{"type":"ephemeral"}}],
             "messages":[
               {"role":"user","content":"List files"},
               {"role":"assistant","content":[
                  {"type":"thinking","thinking":"hmm","signature":"x"},
                  {"type":"text","text":"Looking."},
                  {"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]},
               {"role":"user","content":[
                  {"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"a.txt"}]},
                  {"type":"text","text":"Thanks"}]}],
             "tools":[{"name":"Bash","description":"Run","input_schema":{"$schema":"http://json-schema.org/draft-07/schema#","type":"object","properties":{"command":{"type":"string"}}}},
                      {"type":"web_search_20250305","name":"web_search"}],
             "tool_choice":{"type":"auto","disable_parallel_tool_use":true},
             "thinking":{"type":"enabled","budget_tokens":1000},
             "metadata":{"user_id":"u"}}
            """);

        Assert.Equal("deepseek-chat", chat.GetProperty("model").GetString());
        Assert.Equal(32000, chat.GetProperty("max_tokens").GetInt32());
        Assert.True(chat.GetProperty("stream").GetBoolean());
        Assert.True(chat.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.False(chat.TryGetProperty("thinking", out _));
        Assert.False(chat.TryGetProperty("metadata", out _));

        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user", "assistant", "tool", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("You are Claude Code.\n\nBe terse.", messages[0].GetProperty("content").GetString());

        var assistant = messages[2];
        Assert.Equal("Looking.", assistant.GetProperty("content").GetString());
        var call = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("toolu_1", call.GetProperty("id").GetString());
        Assert.Equal("Bash", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"command":"ls"}""", call.GetProperty("function").GetProperty("arguments").GetString());

        Assert.Equal("toolu_1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("a.txt", messages[3].GetProperty("content").GetString());
        Assert.Equal("Thanks", messages[4].GetProperty("content").GetString());

        // The server tool has no Chat Completions form; the custom one loses its "$schema".
        var tools = chat.GetProperty("tools").EnumerateArray().ToList();
        Assert.Single(tools);
        Assert.False(tools[0].GetProperty("function").GetProperty("parameters").TryGetProperty("$schema", out _));
        Assert.Equal("auto", chat.GetProperty("tool_choice").GetString());
        Assert.False(chat.GetProperty("parallel_tool_calls").GetBoolean());
    }

    [Fact]
    public void Images_become_image_parts_and_tool_result_images_follow_the_tool_message()
    {
        var chat = ChatRequest("""
            {"messages":[
               {"role":"assistant","content":[{"type":"tool_use","id":"t","name":"Read","input":{}}]},
               {"role":"user","content":[
                 {"type":"tool_result","tool_use_id":"t","is_error":true,"content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]},
                 {"type":"image","source":{"type":"url","url":"https://x/y.png"}}]}]}
            """);

        var messages = chat.GetProperty("messages").EnumerateArray().ToList();
        Assert.True(messages[0].GetProperty("content").ValueKind == JsonValueKind.Null);
        Assert.Equal("Error: (no output)", messages[1].GetProperty("content").GetString());

        var parts = messages[2].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("data:image/png;base64,AAAA", parts[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("https://x/y.png", parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void Omitted_optional_fields_are_left_out_on_a_retry()
    {
        var chat = ChatRequest("""{"max_tokens":64000,"stream":true,"messages":[]}""", new HashSet<string> { "max_tokens", "stream_options" });

        Assert.False(chat.TryGetProperty("max_tokens", out _));
        Assert.False(chat.TryGetProperty("stream_options", out _));
        Assert.Equal(["max_tokens"], MessagesTranslator.FieldsNamedIn("max_tokens must be <= 8192"));
    }

    [Theory]
    [InlineData("tool", """{"type":"tool","name":"Bash"}""")]
    [InlineData("required", """{"type":"any"}""")]
    [InlineData("none", """{"type":"none"}""")]
    public void Tool_choice_maps_to_its_chat_form(string expected, string choice)
    {
        var chat = ChatRequest($$$"""{"messages":[],"tools":[{"name":"Bash","input_schema":{"type":"object"}}],"tool_choice":{{{choice}}}}""");

        var mapped = chat.GetProperty("tool_choice");
        Assert.Equal(expected, mapped.ValueKind == JsonValueKind.String ? mapped.GetString() : "tool");
    }

    [Fact]
    public void A_whole_reply_maps_to_a_message_with_its_tool_calls_and_usage()
    {
        using var reply = JsonDocument.Parse("""
            {"id":"abc","choices":[{"finish_reason":"stop","message":{"content":"Running.","reasoning_content":"plan",
              "tool_calls":[{"id":"call_9","function":{"name":"Bash","arguments":"{\"command\":\"ls\"}"}},
                            {"id":"call_10","function":{"name":"Read","arguments":"not json"}}]}}],
             "usage":{"prompt_tokens":10,"completion_tokens":5}}
            """);

        using var message = JsonDocument.Parse(MessagesTranslator.ToMessagesResponse(reply.RootElement, "claude-sonnet-5-5"));
        var root = message.RootElement;

        Assert.Equal("claude-sonnet-5-5", root.GetProperty("model").GetString());
        Assert.Equal("tool_use", root.GetProperty("stop_reason").GetString());
        var content = root.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["thinking", "text", "tool_use", "tool_use"], content.Select(c => c.GetProperty("type").GetString()));
        Assert.Equal("ls", content[2].GetProperty("input").GetProperty("command").GetString());
        Assert.Equal("{}", content[3].GetProperty("input").GetRawText());
        Assert.Equal(10, root.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(5, root.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    /// <summary>The events of a stream as (type, data) pairs.</summary>
    private static List<(string Type, JsonElement Data)> Events(string sse)
    {
        var events = new List<(string, JsonElement)>();
        foreach (var frame in sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = frame.Split('\n');
            var type = lines.First(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = lines.First(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            events.Add((type, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return events;
    }

    [Fact]
    public void A_stream_of_reasoning_text_and_split_tool_calls_becomes_messages_events()
    {
        var stream = new MessagesStream("claude-sonnet-5-5");
        var sse = new StringBuilder(stream.Start());
        foreach (var chunk in new[]
        {
            """{"choices":[{"delta":{"role":"assistant","reasoning_content":"Think"}}]}""",
            """{"choices":[{"delta":{"content":"Hel"}}]}""",
            """{"choices":[{"delta":{"content":"lo"}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"Bash","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"comm"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_2","function":{"name":"Read","arguments":"{\"path\":\"a\"}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"and\":\"ls\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3}}""",
        })
        {
            sse.Append(stream.Feed(chunk));
        }

        sse.Append(stream.Finish());
        var events = Events(sse.ToString());

        Assert.Equal(
            ["message_start",
             "content_block_start", "content_block_delta", "content_block_stop",
             "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop",
             "content_block_start", "content_block_delta", "content_block_stop",
             "content_block_start", "content_block_delta", "content_block_stop",
             "message_delta", "message_stop"],
            events.Select(e => e.Type));

        Assert.Equal("thinking", events[1].Data.GetProperty("content_block").GetProperty("type").GetString());
        Assert.Equal("Hel", events[5].Data.GetProperty("delta").GetProperty("text").GetString());
        Assert.Equal(1, events[5].Data.GetProperty("index").GetInt32());

        var bash = events[8].Data.GetProperty("content_block");
        Assert.Equal(("tool_use", "call_1", "Bash"), (bash.GetProperty("type").GetString(), bash.GetProperty("id").GetString(), bash.GetProperty("name").GetString()));
        Assert.Equal("""{"command":"ls"}""", events[9].Data.GetProperty("delta").GetProperty("partial_json").GetString());
        Assert.Equal(2, events[9].Data.GetProperty("index").GetInt32());
        Assert.Equal("Read", events[11].Data.GetProperty("content_block").GetProperty("name").GetString());

        var final = events[14].Data;
        Assert.Equal("tool_use", final.GetProperty("delta").GetProperty("stop_reason").GetString());
        Assert.Equal(12, final.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(3, final.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public void Tool_calls_without_an_index_are_told_apart_by_id()
    {
        var stream = new MessagesStream("m");
        stream.Start();
        stream.Feed("""{"choices":[{"delta":{"tool_calls":[{"id":"a","function":{"name":"Read","arguments":"{\"path\":\"1\"}"}}]}}]}""");
        stream.Feed("""{"choices":[{"delta":{"tool_calls":[{"id":"b","function":{"name":"Read","arguments":"{\"path\":\"2\"}"}}]}}]}""");

        var events = Events(stream.Finish());

        Assert.Equal(["a", "b"], events.Where(e => e.Type == "content_block_start")
            .Select(e => e.Data.GetProperty("content_block").GetProperty("id").GetString()));
        Assert.Equal("tool_use", events.Single(e => e.Type == "message_delta").Data.GetProperty("delta").GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void An_error_inside_the_stream_is_raised()
    {
        var stream = new MessagesStream("m");

        Assert.Throws<MessagesStreamException>(() => stream.Feed("""{"error":{"message":"overloaded"}}"""));
    }

    [Theory]
    [InlineData("claude-opus-5-5", "smart")]
    [InlineData("claude-haiku-5-5", "default")]
    [InlineData("sonnet", "default")]
    [InlineData("", "default")]
    [InlineData("qwen3-coder", "qwen3-coder")]
    public void Claude_model_ids_map_to_the_providers_models(string requested, string expected) =>
        Assert.Equal(expected, MessagesBridge.ProviderModel(requested, new OpenAiRoute("https://x/v1", "", "default", "smart")));

    /// <summary>A loopback Chat Completions API that answers each request with the next canned reply.</summary>
    private sealed class FakeProvider : IDisposable
    {
        private readonly HttpListener _listener = new();

        public FakeProvider(params (int Status, string ContentType, string Body)[] replies)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{port}/v1";

            _ = Task.Run(async () =>
            {
                foreach (var (status, contentType, body) in replies)
                {
                    var context = await _listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    Requests.Add((context.Request.Url!.AbsolutePath, context.Request.Headers["Authorization"], await reader.ReadToEndAsync()));

                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = contentType;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public string BaseUrl { get; }

        public List<(string Path, string? Authorization, string Body)> Requests { get; } = [];

        public void Dispose() => _listener.Close();
    }

    private static async Task<HttpResponseMessage> PostAsync(string baseUrl, string token, string path, string body)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.Add("x-api-key", token);
        request.Headers.Add("anthropic-version", "2023-06-01");
        var response = await http.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    [Fact]
    public async Task The_bridge_streams_a_providers_reply_as_messages_events_with_the_real_key()
    {
        using var provider = new FakeProvider((200, "text/event-stream",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
            ": keep-alive\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":1}}\n\n" +
            "data: [DONE]\n\n"));
        using var bridge = new MessagesBridge();
        var (baseUrl, token) = bridge.Register(new OpenAiRoute(provider.BaseUrl, "sk-real", "deepseek-chat", ""));

        using var response = await PostAsync(baseUrl, token, "/v1/messages?beta=true",
            """{"model":"claude-sonnet-5-5","max_tokens":100,"stream":true,"messages":[{"role":"user","content":"hello"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = Events((await response.Content.ReadAsStringAsync()).Replace("\r\n", "\n"));
        Assert.Equal(["message_start", "content_block_start", "content_block_delta", "content_block_stop", "message_delta", "message_stop"],
            events.Select(e => e.Type));
        Assert.Equal("Hi", events[2].Data.GetProperty("delta").GetProperty("text").GetString());

        var (path, authorization, sent) = Assert.Single(provider.Requests);
        Assert.Equal("/v1/chat/completions", path);
        Assert.Equal("Bearer sk-real", authorization);
        Assert.Equal("deepseek-chat", JsonDocument.Parse(sent).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_rejected_optional_field_is_retried_without_it_and_errors_keep_their_status()
    {
        using var provider = new FakeProvider(
            (400, "application/json", """{"error":{"message":"Invalid max_tokens value, the valid range is [1, 8192]"}}"""),
            (200, "application/json", """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        using var bridge = new MessagesBridge();
        var (baseUrl, token) = bridge.Register(new OpenAiRoute(provider.BaseUrl, "", "m", ""));

        using var response = await PostAsync(baseUrl, token, "/v1/messages",
            """{"model":"m","max_tokens":64000,"messages":[{"role":"user","content":"hello"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(2, provider.Requests.Count);
        Assert.False(JsonDocument.Parse(provider.Requests[1].Body).RootElement.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task A_provider_error_is_relayed_in_anthropic_form()
    {
        using var provider = new FakeProvider((429, "application/json", """{"error":{"message":"Rate limit reached"}}"""));
        using var bridge = new MessagesBridge();
        var (baseUrl, token) = bridge.Register(new OpenAiRoute(provider.BaseUrl, "", "m", ""));

        using var response = await PostAsync(baseUrl, token, "/v1/messages", """{"model":"m","messages":[]}""");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("rate_limit_error", error.GetProperty("type").GetString());
        Assert.Contains("Rate limit reached", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_unknown_token_is_refused_and_tokens_are_stable_per_route()
    {
        using var bridge = new MessagesBridge();
        var route = new OpenAiRoute("https://x/v1", "k", "m", "");
        var (baseUrl, token) = bridge.Register(route);

        Assert.Equal(token, bridge.Register(route with { }).Token);
        Assert.NotEqual(token, bridge.Register(route with { ApiKey = "other" }).Token);

        using var response = await PostAsync(baseUrl, "sk-guess", "/v1/messages", "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var count = await PostAsync(baseUrl, token, "/v1/messages/count_tokens", """{"messages":[{"role":"user","content":"hello there"}]}""");
        Assert.True(JsonDocument.Parse(await count.Content.ReadAsStringAsync()).RootElement.GetProperty("input_tokens").GetInt32() > 0);
    }
}
