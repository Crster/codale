using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Text;

namespace Codale.Agents.OpenAi;

/// <summary>Where and as whom to call an OpenAI-compatible Chat Completions API: a BYOK provider's URL, key and model.</summary>
public sealed record OpenAiEndpoint(string BaseUrl, string ApiKey, string Model);

/// <summary>
/// One-shot calls to an OpenAI-compatible Chat Completions API, for the helper model's jobs.
/// Talks to the provider directly, so there is no CLI to start or keep warm; tool calls use
/// the API's own forced <c>tool_choice</c>, so the reply is structured instead of JSON in text.
/// </summary>
public static class OpenAiApiClient
{
    /// <summary>The reply cap for one-line jobs; longer write-ups (a handoff, a file answer) pass their own.</summary>
    internal const int MaxTokens = 1024;

    // One client for the app: its pooled connection is what makes the second call fast.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Free-form generation; the text of the reply.</summary>
    public static async Task<string> CompleteAsync(
        OpenAiEndpoint endpoint, string systemPrompt, string prompt, CancellationToken ct, int maxTokens = MaxTokens,
        Action<UsageSnapshot>? onUsage = null)
    {
        var cap = maxTokens;
        JsonDocument reply;
        string text;
        while (true)
        {
            reply = await SendAsync(endpoint, () => BuildRequest(endpoint, systemPrompt, prompt, tools: null, cap), ct, onUsage)
                .ConfigureAwait(false);
            text = ReplyMessage(reply.RootElement) is { } message ? MessageText(message) : "";

            // A reasoning model can spend the whole cap thinking and cut off before any answer
            // (finish_reason "length", empty content): retry once with room for both.
            if (string.IsNullOrWhiteSpace(text) && cap == maxTokens && HitLengthLimit(reply.RootElement))
            {
                reply.Dispose();
                cap = Math.Max(maxTokens * 8, 8192);
                continue;
            }

            break;
        }

        using var _ = reply;
        var replyJson = reply.RootElement;
        if (string.IsNullOrWhiteSpace(text))
        {
            var responseDebug = replyJson.GetRawText();
            if (responseDebug.Length > 500)
            {
                responseDebug = responseDebug[..500] + "...";
            }

            throw new HelperModelException($"The OpenAI-compatible API returned no text. Response: {responseDebug}");
        }

        return text;
    }

    /// <summary>
    /// Forces the model to call one of <paramref name="tools"/>. Null when the reply has no
    /// usable call; a provider that ignores tools and answers in text is read as JSON.
    /// </summary>
    public static async Task<ToolCall?> CallToolAsync(
        OpenAiEndpoint endpoint,
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct,
        Action<UsageSnapshot>? onUsage = null)
    {
        using var reply = await SendAsync(endpoint, () => BuildRequest(endpoint, systemPrompt, conversation, tools), ct, onUsage)
            .ConfigureAwait(false);
        return ReadToolCall(reply.RootElement, tools);
    }

    /// <summary>
    /// Like <see cref="CallToolAsync"/>, but keeps every valid call of a reply that made
    /// several at once - one round trip instead of one per call. Empty when none is usable.
    /// </summary>
    public static async Task<IReadOnlyList<ToolCall>> CallToolsAsync(
        OpenAiEndpoint endpoint,
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct,
        Action<UsageSnapshot>? onUsage = null)
    {
        using var reply = await SendAsync(endpoint, () => BuildRequest(endpoint, systemPrompt, conversation, tools), ct, onUsage)
            .ConfigureAwait(false);
        return ReadToolCalls(reply.RootElement, tools);
    }

    /// <summary>
    /// The chat completions URL for a base URL in the OpenAI SDK's style (".../v1"); one
    /// that already names the endpoint is kept as it is.
    /// </summary>
    internal static string ChatCompletionsUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/chat/completions";
    }

    internal static string BuildRequest(
        OpenAiEndpoint endpoint, string systemPrompt, string prompt, IReadOnlyList<ToolDefinition>? tools, int maxTokens = MaxTokens)
    {
        return WriteJson(json =>
        {
            json.WriteStartObject();
            json.WriteString("model", endpoint.Model);
            json.WriteNumber("max_tokens", maxTokens);

            json.WriteStartArray("messages");
            json.WriteStartObject();
            json.WriteString("role", "system");
            json.WriteString("content", systemPrompt);
            json.WriteEndObject();
            json.WriteStartObject();
            json.WriteString("role", "user");
            json.WriteString("content", prompt);
            json.WriteEndObject();
            json.WriteEndArray();

            if (tools is { Count: > 0 })
            {
                json.WriteStartArray("tools");
                foreach (var tool in tools)
                {
                    WriteTool(json, tool);
                }

                json.WriteEndArray();

                // One tool is named outright; several leave the pick to the model - but it must pick.
                if (tools.Count == 1)
                {
                    json.WriteStartObject("tool_choice");
                    json.WriteString("type", "function");
                    json.WriteStartObject("function");
                    json.WriteString("name", tools[0].Name);
                    json.WriteEndObject();
                    json.WriteEndObject();
                }
                else
                {
                    json.WriteString("tool_choice", "required");
                }
            }

            json.WriteEndObject();
        });
    }

    /// <summary>Runs <paramref name="write"/> against a fresh writer and returns the JSON it produced.</summary>
    internal static string WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            write(json);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteTool(Utf8JsonWriter json, ToolDefinition tool)
    {
        json.WriteStartObject();
        json.WriteString("type", "function");
        json.WriteStartObject("function");
        json.WriteString("name", tool.Name);
        json.WriteString("description", tool.Description);

        json.WriteStartObject("parameters");
        json.WriteString("type", "object");
        json.WriteStartObject("properties");
        foreach (var parameter in tool.Parameters)
        {
            json.WriteStartObject(parameter.Name);
            json.WriteString("type", parameter.Type switch
            {
                ToolParameterType.Integer => "integer",
                ToolParameterType.Number => "number",
                ToolParameterType.Boolean => "boolean",
                _ => "string",
            });
            json.WriteString("description", parameter.Description);
            if (parameter.AllowedValues is { Count: > 0 } allowed)
            {
                json.WriteStartArray("enum");
                foreach (var value in allowed)
                {
                    json.WriteStringValue(value);
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        json.WriteEndObject();

        json.WriteStartArray("required");
        foreach (var parameter in tool.Parameters.Where(p => p.Required))
        {
            json.WriteStringValue(parameter.Name);
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary>The first choice's <c>message</c>, or null when the reply is not that shape.</summary>
    private static JsonElement? ReplyMessage(JsonElement reply) =>
        reply.Prop("choices") is { ValueKind: JsonValueKind.Array } choices && choices.GetArrayLength() > 0 &&
        choices[0].Prop("message") is { ValueKind: JsonValueKind.Object } message
            ? message
            : null;

    private static bool HitLengthLimit(JsonElement reply) =>
        reply.Prop("choices") is { ValueKind: JsonValueKind.Array } choices && choices.GetArrayLength() > 0 &&
        choices[0].Str("finish_reason") == "length";

    /// <summary>A message's text: a plain string, or the text parts some providers send instead.</summary>
    private static string MessageText(JsonElement message) => message.Prop("content") switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        { ValueKind: JsonValueKind.Array } parts => string.Concat(parts.EnumerateArray().Select(p => p.Str("text")).Where(t => t is not null)),
        _ => "",
    };

    /// <summary>A <c>tool_calls</c> entry as the JSON contract <see cref="HelperToolCalls.Parse"/> reads, or null.</summary>
    private static string? CallJson(JsonElement toolCall)
    {
        if (toolCall.Prop("function") is not { ValueKind: JsonValueKind.Object } function || function.Str("name") is not { } name)
        {
            return null;
        }

        // Arguments arrive as a JSON string; a provider that sends an object is taken as is.
        var arguments = function.Prop("arguments") switch
        {
            { ValueKind: JsonValueKind.String } s when s.GetString() is { Length: > 0 } raw && raw.TrimStart().StartsWith('{') => raw,
            { ValueKind: JsonValueKind.Object } o => o.GetRawText(),
            _ => "{}",
        };

        return $"{{\"tool\":\"{JsonEncodedText.Encode(name)}\",\"arguments\":{arguments}}}";
    }

    /// <summary>The reply's first tool call as a validated call; else its text read as the JSON contract.</summary>
    internal static ToolCall? ReadToolCall(JsonElement reply, IReadOnlyList<ToolDefinition> tools)
    {
        if (ReplyMessage(reply) is not { } message)
        {
            return null;
        }

        foreach (var toolCall in message.Items("tool_calls"))
        {
            // Through the same check the CLI's reply gets: known tool, required
            // arguments present, enum values allowed.
            if (CallJson(toolCall) is { } json)
            {
                return HelperToolCalls.Parse(json, tools);
            }
        }

        return HelperToolCalls.Parse(MessageText(message), tools);
    }

    /// <summary>Every valid tool call of the reply, in order; else its text read as one call.</summary>
    internal static IReadOnlyList<ToolCall> ReadToolCalls(JsonElement reply, IReadOnlyList<ToolDefinition> tools)
    {
        if (ReplyMessage(reply) is not { } message)
        {
            return [];
        }

        var calls = new List<ToolCall>();
        foreach (var toolCall in message.Items("tool_calls"))
        {
            if (CallJson(toolCall) is { } json && HelperToolCalls.Parse(json, tools) is { } call)
            {
                calls.Add(call);
            }
        }

        if (calls.Count > 0)
        {
            return calls;
        }

        return ReadToolCall(reply, tools) is { } single ? [single] : [];
    }

    /// <summary>
    /// A key travels only over https, or to a local server: anything else would put it on the
    /// wire in clear text. A keyless endpoint may use any scheme.
    /// </summary>
    internal static bool IsSafeToSendKey(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback);

    private static async Task<JsonDocument> SendAsync(
        OpenAiEndpoint endpoint, Func<string> buildBody, CancellationToken ct, Action<UsageSnapshot>? onUsage = null)
    {
        try
        {
            // Building the request is inside the guard: a malformed URL or an unencodable
            // prompt must surface as a HelperModelException like every other failure here.
            var url = new Uri(ChatCompletionsUrl(endpoint.BaseUrl), UriKind.Absolute);
            if (endpoint.ApiKey.Length > 0 && !IsSafeToSendKey(url))
            {
                throw new HelperModelException(
                    $"Refusing to send an API key to {url.Scheme}://{url.Authority}: use an https URL, or a local server.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(buildBody(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            if (endpoint.ApiKey.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            }

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HelperModelException(
                    $"The OpenAI-compatible API returned {(int)response.StatusCode}: {ErrorMessage(text)}");
            }

            var document = JsonDocument.Parse(text);
            onUsage?.Invoke(ReadUsage(document.RootElement));
            return document;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or ArgumentException or InvalidOperationException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new HelperModelException($"The OpenAI-compatible API call failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The reply's <c>usage</c> block in Anthropic terms; zeros when a provider leaves it out.
    /// Cached prompt tokens are reported apart, as cache reads, like Anthropic does.
    /// </summary>
    internal static UsageSnapshot ReadUsage(JsonElement reply)
    {
        if (reply.Prop("usage") is not { ValueKind: JsonValueKind.Object } usage)
        {
            return default;
        }

        var prompt = usage.Int("prompt_tokens") ?? 0;
        var cached = usage.Prop("prompt_tokens_details") is { } details ? details.Int("cached_tokens") ?? 0 : 0;
        return new UsageSnapshot
        {
            InputTokens = Math.Max(0, prompt - cached),
            OutputTokens = usage.Int("completion_tokens") ?? 0,
            CacheReadInputTokens = cached,
        };
    }

    /// <summary>The API's <c>error.message</c> (or a bare string <c>error</c>), or the start of the body when it is neither.</summary>
    internal static string ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            switch (document.RootElement.Prop("error"))
            {
                case { ValueKind: JsonValueKind.Object } error when error.Str("message") is { Length: > 0 } text:
                    return text;
                case { ValueKind: JsonValueKind.String } error when error.GetString() is { Length: > 0 } text:
                    return text;
            }
        }
        catch (JsonException)
        {
        }

        body = body.Trim();
        return TextClip.Truncate(body, 300);
    }
}
