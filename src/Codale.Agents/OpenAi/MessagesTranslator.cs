using System.Text;
using System.Text.Json;

using Codale.Agents.Claude;

namespace Codale.Agents.OpenAi;

/// <summary>
/// Converts between the Anthropic Messages API the Claude CLI speaks and the OpenAI Chat
/// Completions API a BYOK provider serves: requests one way, replies (whole or streamed,
/// see <see cref="MessagesStream"/>) and errors the other.
/// </summary>
/// <remarks>
/// Only fields with a Chat Completions meaning are carried over; Anthropic-only ones
/// (<c>cache_control</c>, <c>thinking</c>, <c>metadata</c>, server tools) are dropped
/// rather than sent to a provider that would reject them. Thinking blocks the CLI sends
/// back are dropped too: they belong to the reply that made them.
/// </remarks>
internal static class MessagesTranslator
{
    /// <summary>Optional request fields some providers reject; a retry leaves out the ones an error names.</summary>
    internal static readonly string[] OptionalFields = ["max_tokens", "stream_options", "parallel_tool_calls"];

    /// <summary>
    /// The Chat Completions request for an Anthropic Messages <paramref name="request"/>,
    /// asking for <paramref name="model"/>, without the <paramref name="omit"/>ted optional fields.
    /// </summary>
    public static string ToChatRequest(JsonElement request, string model, IReadOnlySet<string>? omit = null)
    {
        omit ??= new HashSet<string>();
        return OpenAiApiClient.WriteJson(json =>
        {
            json.WriteStartObject();
            json.WriteString("model", model);

            json.WriteStartArray("messages");
            if (SystemText(request) is { Length: > 0 } system)
            {
                json.WriteStartObject();
                json.WriteString("role", "system");
                json.WriteString("content", system);
                json.WriteEndObject();
            }

            foreach (var message in request.Items("messages"))
            {
                if (message.Str("role") == "assistant")
                {
                    WriteAssistant(json, message);
                }
                else
                {
                    WriteUser(json, message);
                }
            }

            json.WriteEndArray();

            var tools = request.Items("tools").Where(t => t.Prop("input_schema") is { ValueKind: JsonValueKind.Object } && t.Str("name") is { Length: > 0 }).ToList();
            if (tools.Count > 0)
            {
                json.WriteStartArray("tools");
                foreach (var tool in tools)
                {
                    WriteTool(json, tool);
                }

                json.WriteEndArray();
                WriteToolChoice(json, request.Prop("tool_choice"), omit);
            }

            if (!omit.Contains("max_tokens") && request.Int("max_tokens") is { } maxTokens)
            {
                json.WriteNumber("max_tokens", maxTokens);
            }

            if (request.Prop("temperature") is { ValueKind: JsonValueKind.Number } temperature)
            {
                json.WritePropertyName("temperature");
                temperature.WriteTo(json);
            }

            if (request.Prop("top_p") is { ValueKind: JsonValueKind.Number } topP)
            {
                json.WritePropertyName("top_p");
                topP.WriteTo(json);
            }

            if (request.StrArray("stop_sequences") is { Count: > 0 } stops)
            {
                json.WriteStartArray("stop");
                foreach (var stop in stops)
                {
                    json.WriteStringValue(stop);
                }

                json.WriteEndArray();
            }

            if (request.Bool("stream"))
            {
                json.WriteBoolean("stream", true);

                // Without this most providers never report usage on a stream.
                if (!omit.Contains("stream_options"))
                {
                    json.WriteStartObject("stream_options");
                    json.WriteBoolean("include_usage", true);
                    json.WriteEndObject();
                }
            }

            json.WriteEndObject();
        });
    }

    /// <summary>The system prompt: a plain string, or its text blocks joined.</summary>
    private static string SystemText(JsonElement request) => request.Prop("system") switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        { ValueKind: JsonValueKind.Array } blocks => string.Join("\n\n", blocks.EnumerateArray()
            .Select(b => b.Str("text")).Where(t => !string.IsNullOrEmpty(t))),
        _ => "",
    };

    /// <summary>
    /// A user turn: its tool results become <c>tool</c> messages (which must directly follow
    /// the assistant's calls), and what remains a user message after them.
    /// </summary>
    private static void WriteUser(Utf8JsonWriter json, JsonElement message)
    {
        if (message.Prop("content") is { ValueKind: JsonValueKind.String } plain)
        {
            json.WriteStartObject();
            json.WriteString("role", "user");
            json.WriteString("content", plain.GetString());
            json.WriteEndObject();
            return;
        }

        var parts = new List<(string? Text, string? ImageUrl)>();
        foreach (var block in message.Items("content"))
        {
            switch (block.Str("type"))
            {
                case "text" when block.Str("text") is { Length: > 0 } text:
                    parts.Add((text, null));
                    break;
                case "image" when ImageUrl(block) is { } url:
                    parts.Add((null, url));
                    break;
                case "document":
                    parts.Add((DocumentText(block), null));
                    break;
                case "tool_result":
                    json.WriteStartObject();
                    json.WriteString("role", "tool");
                    json.WriteString("tool_call_id", block.Str("tool_use_id") ?? "");
                    json.WriteString("content", ToolResultText(block, parts));
                    json.WriteEndObject();
                    break;
            }
        }

        if (parts.Count == 0)
        {
            return;
        }

        json.WriteStartObject();
        json.WriteString("role", "user");
        if (parts.All(p => p.ImageUrl is null))
        {
            json.WriteString("content", string.Join("\n\n", parts.Select(p => p.Text)));
        }
        else
        {
            json.WriteStartArray("content");
            foreach (var (text, imageUrl) in parts)
            {
                json.WriteStartObject();
                if (imageUrl is not null)
                {
                    json.WriteString("type", "image_url");
                    json.WriteStartObject("image_url");
                    json.WriteString("url", imageUrl);
                    json.WriteEndObject();
                }
                else
                {
                    json.WriteString("type", "text");
                    json.WriteString("text", text);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// A tool result's text. A tool message carries only text, so its images go to
    /// <paramref name="parts"/>, the user message that follows.
    /// </summary>
    private static string ToolResultText(JsonElement block, List<(string? Text, string? ImageUrl)> parts)
    {
        var text = block.Prop("content") switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
            { ValueKind: JsonValueKind.Array } items => string.Join("\n", items.EnumerateArray().Select(item =>
            {
                if (item.Str("type") == "image" && ImageUrl(item) is { } url)
                {
                    parts.Add((null, url));
                    return null;
                }

                return item.Str("text");
            }).Where(t => !string.IsNullOrEmpty(t))),
            _ => "",
        };

        // Some providers refuse an empty tool message.
        if (text.Length == 0)
        {
            text = "(no output)";
        }

        return block.Bool("is_error") ? "Error: " + text : text;
    }

    /// <summary>An image block as a URL: a data URI for inline bytes, else the URL it points at.</summary>
    private static string? ImageUrl(JsonElement block)
    {
        if (block.Prop("source") is not { ValueKind: JsonValueKind.Object } source)
        {
            return null;
        }

        return source.Str("type") switch
        {
            "base64" when source.Str("data") is { Length: > 0 } data => $"data:{source.Str("media_type") ?? "image/png"};base64,{data}",
            "url" => source.Str("url"),
            _ => null,
        };
    }

    /// <summary>A document block: plain-text sources inline; anything else (a PDF) has no Chat Completions form.</summary>
    private static string DocumentText(JsonElement block) =>
        block.Prop("source") is { ValueKind: JsonValueKind.Object } source && source.Str("type") == "text" && source.Str("data") is { } data
            ? data
            : "[A document was attached here, but this provider cannot read documents.]";

    /// <summary>An assistant turn: its text and its tool calls, without thinking blocks.</summary>
    private static void WriteAssistant(Utf8JsonWriter json, JsonElement message)
    {
        if (message.Prop("content") is { ValueKind: JsonValueKind.String } plain)
        {
            json.WriteStartObject();
            json.WriteString("role", "assistant");
            json.WriteString("content", plain.GetString());
            json.WriteEndObject();
            return;
        }

        var text = string.Concat(message.Items("content").Where(b => b.Str("type") == "text").Select(b => b.Str("text")));
        var calls = message.Items("content").Where(b => b.Str("type") == "tool_use" && b.Str("name") is { Length: > 0 }).ToList();
        if (text.Length == 0 && calls.Count == 0)
        {
            return;
        }

        json.WriteStartObject();
        json.WriteString("role", "assistant");
        if (text.Length > 0)
        {
            json.WriteString("content", text);
        }
        else
        {
            json.WriteNull("content");
        }

        if (calls.Count > 0)
        {
            json.WriteStartArray("tool_calls");
            foreach (var call in calls)
            {
                json.WriteStartObject();
                json.WriteString("id", call.Str("id") ?? NewId("call_"));
                json.WriteString("type", "function");
                json.WriteStartObject("function");
                json.WriteString("name", call.Str("name"));
                json.WriteString("arguments", call.Prop("input") is { ValueKind: JsonValueKind.Object } input ? input.GetRawText() : "{}");
                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    private static void WriteTool(Utf8JsonWriter json, JsonElement tool)
    {
        json.WriteStartObject();
        json.WriteString("type", "function");
        json.WriteStartObject("function");
        json.WriteString("name", tool.Str("name"));
        if (tool.Str("description") is { } description)
        {
            json.WriteString("description", description);
        }

        // "$schema" is noise to the model, and some providers refuse it.
        json.WriteStartObject("parameters");
        foreach (var property in tool.Prop("input_schema")!.Value.EnumerateObject())
        {
            if (property.Name != "$schema")
            {
                property.WriteTo(json);
            }
        }

        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void WriteToolChoice(Utf8JsonWriter json, JsonElement? choice, IReadOnlySet<string> omit)
    {
        if (choice is not { ValueKind: JsonValueKind.Object } c)
        {
            return;
        }

        switch (c.Str("type"))
        {
            case "auto":
                json.WriteString("tool_choice", "auto");
                break;
            case "any":
                json.WriteString("tool_choice", "required");
                break;
            case "none":
                json.WriteString("tool_choice", "none");
                break;
            case "tool" when c.Str("name") is { Length: > 0 } name:
                json.WriteStartObject("tool_choice");
                json.WriteString("type", "function");
                json.WriteStartObject("function");
                json.WriteString("name", name);
                json.WriteEndObject();
                json.WriteEndObject();
                break;
        }

        if (c.Bool("disable_parallel_tool_use") && !omit.Contains("parallel_tool_calls"))
        {
            json.WriteBoolean("parallel_tool_calls", false);
        }
    }

    /// <summary>The Anthropic Messages reply for a whole (non-streamed) Chat Completions <paramref name="reply"/>.</summary>
    public static string ToMessagesResponse(JsonElement reply, string model)
    {
        var choice = reply.Prop("choices") is { ValueKind: JsonValueKind.Array } choices && choices.GetArrayLength() > 0 ? choices[0] : default;
        var message = choice.Prop("message") ?? default;
        var usage = OpenAiApiClient.ReadUsage(reply);

        return OpenAiApiClient.WriteJson(json =>
        {
            json.WriteStartObject();
            json.WriteString("id", "msg_" + (reply.Str("id") ?? NewId("")));
            json.WriteString("type", "message");
            json.WriteString("role", "assistant");
            json.WriteString("model", model);

            json.WriteStartArray("content");
            if (Reasoning(message) is { Length: > 0 } reasoning)
            {
                json.WriteStartObject();
                json.WriteString("type", "thinking");
                json.WriteString("thinking", reasoning);
                json.WriteString("signature", "");
                json.WriteEndObject();
            }

            var text = message.Prop("content") switch
            {
                { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
                { ValueKind: JsonValueKind.Array } parts => string.Concat(parts.EnumerateArray().Select(p => p.Str("text")).Where(t => t is not null)),
                _ => "",
            };
            if (text.Length > 0)
            {
                json.WriteStartObject();
                json.WriteString("type", "text");
                json.WriteString("text", text);
                json.WriteEndObject();
            }

            var hadTools = false;
            foreach (var call in message.Items("tool_calls"))
            {
                if (call.Prop("function") is not { ValueKind: JsonValueKind.Object } function || function.Str("name") is not { Length: > 0 } name)
                {
                    continue;
                }

                hadTools = true;
                json.WriteStartObject();
                json.WriteString("type", "tool_use");
                json.WriteString("id", call.Str("id") ?? NewId("toolu_"));
                json.WriteString("name", name);
                json.WritePropertyName("input");
                json.WriteRawValue(Arguments(function.Prop("arguments")));
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteString("stop_reason", StopReason(choice.Str("finish_reason"), hadTools));
            json.WriteNull("stop_sequence");
            WriteUsage(json, usage);
            json.WriteEndObject();
        });
    }

    /// <summary>A message's or delta's reasoning text, under either name providers use for it.</summary>
    internal static string? Reasoning(JsonElement message) => message.Str("reasoning_content") ?? message.Str("reasoning");

    /// <summary>Tool arguments as a JSON object: the provider's string parsed, or <c>{}</c> when it is missing or not an object.</summary>
    internal static string Arguments(JsonElement? arguments)
    {
        switch (arguments)
        {
            case { ValueKind: JsonValueKind.Object } o:
                return o.GetRawText();
            case { ValueKind: JsonValueKind.String } s:
                return Arguments(s.GetString());
            default:
                return "{}";
        }
    }

    internal static string Arguments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.GetRawText() : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    /// <summary>A Chat Completions finish reason in Anthropic terms; a reply with tool calls always stops for them.</summary>
    internal static string StopReason(string? finishReason, bool hadTools) => finishReason switch
    {
        "length" => "max_tokens",
        "tool_calls" or "function_call" => "tool_use",
        _ when hadTools => "tool_use",
        _ => "end_turn",
    };

    internal static void WriteUsage(Utf8JsonWriter json, Codale.Core.Agents.UsageSnapshot usage)
    {
        json.WriteStartObject("usage");
        json.WriteNumber("input_tokens", usage.InputTokens);
        json.WriteNumber("output_tokens", usage.OutputTokens);
        json.WriteNumber("cache_read_input_tokens", usage.CacheReadInputTokens);
        json.WriteNumber("cache_creation_input_tokens", 0);
        json.WriteEndObject();
    }

    /// <summary>An Anthropic error body for a failure with this HTTP <paramref name="status"/>, so the CLI shows the message and retries what is worth retrying.</summary>
    public static string ToAnthropicError(int status, string message) => OpenAiApiClient.WriteJson(json =>
    {
        json.WriteStartObject();
        json.WriteString("type", "error");
        json.WriteStartObject("error");
        json.WriteString("type", status switch
        {
            400 or 413 or 422 => "invalid_request_error",
            401 => "authentication_error",
            403 => "permission_error",
            404 => "not_found_error",
            429 => "rate_limit_error",
            503 or 529 => "overloaded_error",
            _ => "api_error",
        });
        json.WriteString("message", message);
        json.WriteEndObject();
        json.WriteEndObject();
    });

    /// <summary>The optional fields an upstream error message names, which a retry can leave out.</summary>
    public static IEnumerable<string> FieldsNamedIn(string errorMessage) =>
        OptionalFields.Where(field => errorMessage.Contains(field, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A rough token count for <c>count_tokens</c>: Chat Completions has no such endpoint,
    /// and the CLI only uses the number to judge how full the context is.
    /// </summary>
    public static int EstimateTokens(JsonElement request)
    {
        var characters = SystemText(request).Length;
        foreach (var message in request.Items("messages"))
        {
            characters += message.Prop("content") is { } content ? content.GetRawText().Length : 0;
        }

        foreach (var tool in request.Items("tools"))
        {
            characters += tool.GetRawText().Length;
        }

        return Math.Max(1, characters / 4);
    }

    internal static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..24];

    /// <summary>One server-sent event in the Messages API's framing.</summary>
    internal static string Event(string type, Action<Utf8JsonWriter> body)
    {
        var data = OpenAiApiClient.WriteJson(json =>
        {
            json.WriteStartObject();
            json.WriteString("type", type);
            body(json);
            json.WriteEndObject();
        });
        return new StringBuilder("event: ").Append(type).Append("\ndata: ").Append(data).Append("\n\n").ToString();
    }
}
