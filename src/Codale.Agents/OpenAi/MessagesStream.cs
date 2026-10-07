using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;
using Codale.Core.Text;

namespace Codale.Agents.OpenAi;

/// <summary>
/// Turns one Chat Completions stream into the Messages API's events: <c>message_start</c>,
/// a content block per run of reasoning or text, a <c>tool_use</c> block per call, then
/// <c>message_delta</c> and <c>message_stop</c>. Feed it each <c>data:</c> payload in order.
/// </summary>
/// <remarks>
/// Text and reasoning stream through as they come. Tool calls are held until the stream
/// ends: providers disagree on how they split them (an index or not, the id and name in
/// the first chunk or a later one, arguments in pieces or whole), and only the finished
/// call can be checked to be a JSON object. While a call builds, a <c>ping</c> now and then
/// keeps the connection from looking idle.
/// </remarks>
internal sealed class MessagesStream(string model)
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    private enum Block { None, Thinking, Text }

    private sealed class PendingCall
    {
        public string? Id;
        public string? Name;
        public string? Signature;
        public readonly StringBuilder Arguments = new();
    }

    private readonly List<PendingCall> _calls = [];

    /// <summary>A small model may write its reasoning into the text as &lt;think&gt;; it is shown as thinking, not as the answer.</summary>
    private readonly InlineReasoningSplitter _inline = new();
    private readonly Stopwatch _sincePing = Stopwatch.StartNew();
    private Block _open = Block.None;
    private int _nextIndex;
    private string? _finishReason;
    private UsageSnapshot _usage;
    private bool _finished;

    /// <summary>The opening <c>message_start</c>, sent before the provider's first chunk.</summary>
    public string Start() => MessagesTranslator.Event("message_start", json =>
    {
        json.WriteStartObject("message");
        json.WriteString("id", MessagesTranslator.NewId("msg_"));
        json.WriteString("type", "message");
        json.WriteString("role", "assistant");
        json.WriteString("model", model);
        json.WriteStartArray("content");
        json.WriteEndArray();
        json.WriteNull("stop_reason");
        json.WriteNull("stop_sequence");
        MessagesTranslator.WriteUsage(json, default);
        json.WriteEndObject();
    });

    /// <summary>The events for one chunk's JSON; "" when it adds nothing to show yet.</summary>
    /// <exception cref="MessagesStreamException">The provider reported an error inside the stream.</exception>
    public string Feed(string data)
    {
        using var document = JsonDocument.Parse(data);
        var chunk = document.RootElement;
        if (chunk.Prop("error") is { ValueKind: not JsonValueKind.Null } error)
        {
            throw new MessagesStreamException(error.ValueKind == JsonValueKind.Object ? error.Str("message") ?? error.GetRawText() : error.ToString());
        }

        var output = new StringBuilder();
        if (chunk.Prop("usage") is { ValueKind: JsonValueKind.Object })
        {
            _usage = OpenAiApiClient.ReadUsage(chunk);
        }

        foreach (var choice in chunk.Items("choices"))
        {
            if (choice.Str("finish_reason") is { Length: > 0 } finish)
            {
                _finishReason = finish;
            }

            if (choice.Prop("delta") is not { ValueKind: JsonValueKind.Object } delta)
            {
                continue;
            }

            if (MessagesTranslator.Reasoning(delta) is { Length: > 0 } reasoning)
            {
                Open(output, Block.Thinking);
                output.Append(Delta("thinking_delta", "thinking", reasoning));
            }

            var text = delta.Prop("content") switch
            {
                { ValueKind: JsonValueKind.String } s => s.GetString(),
                { ValueKind: JsonValueKind.Array } parts => string.Concat(parts.EnumerateArray().Select(p => p.Str("text")).Where(t => t is not null)),
                _ => null,
            };
            if (!string.IsNullOrEmpty(text))
            {
                Append(output, _inline.Feed(text));
            }

            foreach (var call in delta.Items("tool_calls"))
            {
                Collect(call);
            }
        }

        if (output.Length > 0)
        {
            _sincePing.Restart();
        }
        else if (_sincePing.Elapsed >= PingInterval)
        {
            _sincePing.Restart();
            output.Append(MessagesTranslator.Event("ping", _ => { }));
        }

        return output.ToString();
    }

    /// <summary>Adds one tool call piece to the call it continues, or starts a new one.</summary>
    private void Collect(JsonElement piece)
    {
        var id = piece.Str("id") is { Length: > 0 } i ? i : null;
        PendingCall? call = null;
        if (piece.Int("index") is int index and >= 0 and < 256)
        {
            while (_calls.Count <= index)
            {
                _calls.Add(new PendingCall());
            }

            call = _calls[index];
        }
        else if (id is not null)
        {
            // No index: a new id is a new call, a known one continues its call.
            call = _calls.FirstOrDefault(c => c.Id == id);
            if (call is null)
            {
                call = new PendingCall();
                _calls.Add(call);
            }
        }
        else if (_calls.Count > 0)
        {
            call = _calls[^1];
        }
        else
        {
            call = new PendingCall();
            _calls.Add(call);
        }

        call.Id ??= id;
        call.Signature ??= MessagesTranslator.ThoughtSignature(piece);
        if (piece.Prop("function") is { ValueKind: JsonValueKind.Object } function)
        {
            if (function.Str("name") is { Length: > 0 } name)
            {
                call.Name ??= name;
            }

            switch (function.Prop("arguments"))
            {
                case { ValueKind: JsonValueKind.String } s:
                    call.Arguments.Append(s.GetString());
                    break;
                case { ValueKind: JsonValueKind.Object } o:
                    call.Arguments.Append(o.GetRawText());
                    break;
            }
        }
    }

    /// <summary>The closing events: the open block's end, the held tool calls, the stop reason and usage. "" once done.</summary>
    public string Finish()
    {
        if (_finished)
        {
            return "";
        }

        _finished = true;
        var output = new StringBuilder();
        Append(output, _inline.Flush());
        Close(output);

        var hadTools = false;
        foreach (var call in _calls.Where(c => c.Name is not null))
        {
            hadTools = true;
            var index = _nextIndex++;
            output.Append(MessagesTranslator.Event("content_block_start", json =>
            {
                json.WriteNumber("index", index);
                json.WriteStartObject("content_block");
                json.WriteString("type", "tool_use");
                json.WriteString("id", MessagesTranslator.WithSignature(call.Id ?? MessagesTranslator.NewId("toolu_"), call.Signature));
                json.WriteString("name", call.Name);
                json.WriteStartObject("input");
                json.WriteEndObject();
                json.WriteEndObject();
            }));
            var arguments = MessagesTranslator.Arguments(call.Arguments.ToString());
            output.Append(MessagesTranslator.Event("content_block_delta", json =>
            {
                json.WriteNumber("index", index);
                json.WriteStartObject("delta");
                json.WriteString("type", "input_json_delta");
                json.WriteString("partial_json", arguments);
                json.WriteEndObject();
            }));
            output.Append(MessagesTranslator.Event("content_block_stop", json => json.WriteNumber("index", index)));
        }

        output.Append(MessagesTranslator.Event("message_delta", json =>
        {
            json.WriteStartObject("delta");
            json.WriteString("stop_reason", MessagesTranslator.StopReason(_finishReason, hadTools));
            json.WriteNull("stop_sequence");
            json.WriteEndObject();
            MessagesTranslator.WriteUsage(json, _usage);
        }));
        output.Append(MessagesTranslator.Event("message_stop", _ => { }));
        return output.ToString();
    }

    /// <summary>An <c>error</c> event, for a failure after the stream has begun.</summary>
    public static string Error(string message) => MessagesTranslator.Event("error", json =>
    {
        json.WriteStartObject("error");
        json.WriteString("type", "api_error");
        json.WriteString("message", message);
        json.WriteEndObject();
    });

    /// <summary>Content text as blocks: inline reasoning as thinking, the rest as text.</summary>
    private void Append(StringBuilder output, IReadOnlyList<(bool Thinking, string Text)> parts)
    {
        foreach (var (thinking, text) in parts)
        {
            Open(output, thinking ? Block.Thinking : Block.Text);
            output.Append(thinking ? Delta("thinking_delta", "thinking", text) : Delta("text_delta", "text", text));
        }
    }

    /// <summary>Makes <paramref name="kind"/> the open block, closing another first.</summary>
    private void Open(StringBuilder output, Block kind)
    {
        if (_open == kind)
        {
            return;
        }

        Close(output);
        _open = kind;
        var index = _nextIndex;
        output.Append(MessagesTranslator.Event("content_block_start", json =>
        {
            json.WriteNumber("index", index);
            json.WriteStartObject("content_block");
            if (kind == Block.Thinking)
            {
                json.WriteString("type", "thinking");
                json.WriteString("thinking", "");
                json.WriteString("signature", "");
            }
            else
            {
                json.WriteString("type", "text");
                json.WriteString("text", "");
            }

            json.WriteEndObject();
        }));
    }

    private void Close(StringBuilder output)
    {
        if (_open == Block.None)
        {
            return;
        }

        var index = _nextIndex++;
        _open = Block.None;
        output.Append(MessagesTranslator.Event("content_block_stop", json => json.WriteNumber("index", index)));
    }

    private string Delta(string type, string field, string value)
    {
        var index = _nextIndex;
        return MessagesTranslator.Event("content_block_delta", json =>
        {
            json.WriteNumber("index", index);
            json.WriteStartObject("delta");
            json.WriteString("type", type);
            json.WriteString(field, value);
            json.WriteEndObject();
        });
    }
}

/// <summary>A provider reported an error in the middle of a stream.</summary>
internal sealed class MessagesStreamException(string message) : Exception(message);
