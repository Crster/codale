using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Text;

namespace Codale.Agents;

/// <summary>Where and as whom to call an Anthropic Messages API: a BYOK provider's URL, key and model.</summary>
public sealed record AnthropicEndpoint(string BaseUrl, string ApiKey, string Model);

/// <summary>
/// One-shot calls to an Anthropic Messages API, for the helper model's jobs. Talks to the
/// provider directly, so there is no CLI to start or keep warm; tool calls use the API's
/// own forced <c>tool_use</c>, so the reply is structured instead of JSON in text.
/// </summary>
public static class AnthropicApiClient
{
    private const string Version = "2023-06-01";
    /// <summary>The reply cap for one-line jobs; longer write-ups (a handoff, a file answer) pass their own.</summary>
    internal const int MaxTokens = 1024;

    // One client for the app: its pooled connection is what makes the second call fast.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Free-form generation; the concatenated text of the reply.</summary>
    public static async Task<string> CompleteAsync(
        AnthropicEndpoint endpoint, string systemPrompt, string prompt, CancellationToken ct, int maxTokens = MaxTokens,
        Action<UsageSnapshot>? onUsage = null)
    {
        using var reply = await SendAsync(endpoint, () => BuildRequest(endpoint, systemPrompt, prompt, tools: null, maxTokens), ct, onUsage)
            .ConfigureAwait(false);

        var text = reply.RootElement.ValueKind == JsonValueKind.Object &&
                   reply.RootElement.TryGetProperty("content", out var content) &&
                   content.ValueKind == JsonValueKind.Array
            ? string.Concat(content.EnumerateArray()
                .Where(b => b.ValueKind == JsonValueKind.Object &&
                            b.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "text")
                .Select(b => b.TryGetProperty("text", out var part) && part.ValueKind == JsonValueKind.String ? part.GetString() : null))
            : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new HelperModelException("The Anthropic API returned no text.");
        }

        return text;
    }

    /// <summary>
    /// Forces the model to call one of <paramref name="tools"/>. Null when the reply has no
    /// usable call; a gateway that ignores tools and answers in text is read as JSON.
    /// </summary>
    public static async Task<ToolCall?> CallToolAsync(
        AnthropicEndpoint endpoint,
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
        AnthropicEndpoint endpoint,
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

    internal static string MessagesUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) ? trimmed
            : trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed + "/messages"
            : trimmed + "/v1/messages";
    }

    internal static string BuildRequest(
        AnthropicEndpoint endpoint, string systemPrompt, string prompt, IReadOnlyList<ToolDefinition>? tools, int maxTokens = MaxTokens)
    {
        return WriteJson(json =>
        {
            json.WriteStartObject();
            json.WriteString("model", endpoint.Model);
            json.WriteNumber("max_tokens", maxTokens);
            json.WriteString("system", systemPrompt);

            // These jobs are one-liners: reasoning first only adds seconds (on a reasoning
            // model, most of the wait) and changes nothing in the answer.
            json.WriteStartObject("thinking");
            json.WriteString("type", "disabled");
            json.WriteEndObject();

            json.WriteStartArray("messages");
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
                json.WriteStartObject("tool_choice");
                if (tools.Count == 1)
                {
                    json.WriteString("type", "tool");
                    json.WriteString("name", tools[0].Name);
                }
                else
                {
                    json.WriteString("type", "any");
                }

                json.WriteEndObject();
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
        json.WriteString("name", tool.Name);
        json.WriteString("description", tool.Description);

        json.WriteStartObject("input_schema");
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
    }

    /// <summary>The reply's <c>tool_use</c> block as a validated call; else its text read as the JSON contract.</summary>
    internal static ToolCall? ReadToolCall(JsonElement reply, IReadOnlyList<ToolDefinition> tools)
    {
        if (reply.ValueKind != JsonValueKind.Object ||
            !reply.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type == "tool_use" && block.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var input = block.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object
                    ? i.GetRawText()
                    : "{}";

                // Through the same check the CLI's reply gets: known tool, required
                // arguments present, enum values allowed.
                return HelperToolCalls.Parse($"{{\"tool\":\"{JsonEncodedText.Encode(name.GetString()!)}\",\"arguments\":{input}}}", tools);
            }

            if (type == "text" && block.TryGetProperty("text", out var part) && part.ValueKind == JsonValueKind.String)
            {
                text.Append(part.GetString());
            }
        }

        return HelperToolCalls.Parse(text.ToString(), tools);
    }

    /// <summary>Every valid <c>tool_use</c> block of the reply, in order; else its text read as one call.</summary>
    internal static IReadOnlyList<ToolCall> ReadToolCalls(JsonElement reply, IReadOnlyList<ToolDefinition> tools)
    {
        if (reply.ValueKind != JsonValueKind.Object ||
            !reply.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var calls = new List<ToolCall>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object &&
                block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "tool_use" &&
                block.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var input = block.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object
                    ? i.GetRawText()
                    : "{}";

                if (HelperToolCalls.Parse($"{{\"tool\":\"{JsonEncodedText.Encode(name.GetString()!)}\",\"arguments\":{input}}}", tools) is { } call)
                {
                    calls.Add(call);
                }
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
        AnthropicEndpoint endpoint, Func<string> buildBody, CancellationToken ct, Action<UsageSnapshot>? onUsage = null)
    {
        try
        {
            // Building the request is inside the guard: a malformed URL or an unencodable
            // prompt must surface as a HelperModelException like every other failure here.
            var url = new Uri(MessagesUrl(endpoint.BaseUrl), UriKind.Absolute);
            if (endpoint.ApiKey.Length > 0 && !IsSafeToSendKey(url))
            {
                throw new HelperModelException(
                    $"Refusing to send an API key to {url.Scheme}://{url.Authority}: use an https URL, or a local server.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(buildBody(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            request.Headers.Add("anthropic-version", Version);
            if (endpoint.ApiKey.Length > 0)
            {
                request.Headers.Add("x-api-key", endpoint.ApiKey);
            }

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HelperModelException(
                    $"The Anthropic API returned {(int)response.StatusCode}: {ErrorMessage(text)}");
            }

            var document = JsonDocument.Parse(text);
            onUsage?.Invoke(ReadUsage(document.RootElement));
            return document;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or ArgumentException or InvalidOperationException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new HelperModelException($"The Anthropic API call failed: {ex.Message}", ex);
        }
    }

    /// <summary>The reply's <c>usage</c> block; zeros when a gateway leaves it out.</summary>
    internal static UsageSnapshot ReadUsage(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object ||
            !reply.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        int Read(string name) =>
            usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;

        return new UsageSnapshot
        {
            InputTokens = Read("input_tokens"),
            OutputTokens = Read("output_tokens"),
            CacheReadInputTokens = Read("cache_read_input_tokens"),
            CacheCreationInputTokens = Read("cache_creation_input_tokens"),
        };
    }

    /// <summary>The API's <c>error.message</c>, or the start of the body when it is not that shape.</summary>
    internal static string ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                message.GetString() is { Length: > 0 } text)
            {
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
