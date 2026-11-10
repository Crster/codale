using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codale.Smart;

internal sealed record SmartToolCall(string Name, IReadOnlyDictionary<string, string> Arguments);

/// <summary>
/// How much the model reasons before answering. Granite 4.2's chat template has three modes:
/// <c>enable_thinking=False</c> (an empty <c>&lt;think&gt;&lt;/think&gt;</c>), low effort
/// (<c>{reasoning effort: low}</c> after the last user message) and full reasoning.
/// </summary>
public enum SmartReasoning { Off, Low, Full }

internal interface ISmartConversation
{
    string Render(ISmartRuntime runtime, int reserve, bool final = false);
    string Visible(string response);
    string FinalVisible(string response);
    IReadOnlyList<SmartToolCall> ParseCalls(string visible);
    void Add(string response, IReadOnlyList<string> results);
    void AddUser(string text);
}

/// <summary>
/// The text/tool subset of the chat template embedded in the pinned Granite 4.2 GGUF, rendered byte for byte
/// (see tests/fixtures/granite for goldens rendered by the template itself).
/// </summary>
internal sealed class SmartConversation : ISmartConversation
{
    /// <summary>The native context size (smart.cpp <c>context_tokens</c>).</summary>
    public const int ContextTokens = 16384;
    public const string LowEffortMarker = "\n\n{reasoning effort: low}";
    public const string FinalInstruction = "Using the observations above, return the final answer for this task in exactly the format the system message requires. " +
        "Do not call tools or reason further. Task:\n";

    private readonly string _system;
    private readonly string _input;
    private readonly object[]? _tools;
    private readonly List<Message> _messages = [];
    private bool _finished;

    public SmartConversation(string system, string input, SmartReasoning reasoning, object[]? tools = null)
    { _system = system; _input = input; Reasoning = reasoning; _tools = tools is { Length: > 0 } ? tools : null; }
    public static ISmartConversation Create(SmartModel model, string system, string input, SmartReasoning reasoning, object[]? tools = null) =>
        new SmartConversation(system, input, reasoning, tools);

    public SmartReasoning Reasoning { get; }
    public string AssistantPrefix => Reasoning == SmartReasoning.Off ? "<think></think>" : "<think>\n";
    public static string Escape(string value) => value.Replace("<|", "< |", StringComparison.Ordinal);

    private abstract record Message;
    private sealed record UserMessage(string Text) : Message;
    private sealed record ToolMessage(IReadOnlyList<string> Results) : Message;
    /// <summary>Content is the full turn (reasoning prefix included) without its tool calls, as the template sees it.</summary>
    private sealed record AssistantMessage(string Content, IReadOnlyList<SmartToolCall> Calls) : Message;

    public void Add(string response, IReadOnlyList<string> results)
    {
        var full = AssistantPrefix + response;
        IReadOnlyList<SmartToolCall> calls = [];
        try { calls = Parse(Visible(response)); } catch (InvalidDataException) { }
        var content = full;
        if (calls.Count > 0)
        {
            // Calls in the answer are rendered canonically by the template; calls quoted inside reasoning stay put.
            int close = Masked(full).LastIndexOf("</think>", StringComparison.Ordinal);
            int split = close < 0 ? 0 : close + "</think>".Length;
            content = full[..split] + Regex.Replace(full[split..], @"<tool_call>[\s\S]*?</tool_call>", "");
        }
        _messages.Add(new AssistantMessage(content, calls));
        if (results.Count > 0) _messages.Add(new ToolMessage(results));
    }

    public void AddUser(string text) => _messages.Add(new UserMessage(text));

    /// <summary>
    /// The prompt, ending in the assistant generation prompt. <paramref name="final"/> asks for the answer without
    /// reasoning or tools: after an exploration it adds the final-answer user turn once.
    /// </summary>
    public string Render(ISmartRuntime runtime, int reserve, bool final = false)
    {
        if (final && !_finished)
        {
            _finished = true;
            if (_messages.Count > 0) AddUser(FinalInstruction + _input);
        }
        var start = 0;
        var prompt = RenderCore(start, final);
        // Drop the oldest complete exchanges (an assistant turn and its tool results) until the prompt fits.
        while (runtime.CountTokens(prompt) + reserve > ContextTokens && NextExchange(start) is { } next)
        {
            start = next;
            prompt = RenderCore(start, final);
        }
        if (runtime.CountTokens(prompt) + reserve > ContextTokens)
            throw new InvalidDataException("Smart input exceeds the 16K context budget.");
        return prompt;
    }

    private int? NextExchange(int start)
    {
        // Keep at least the latest exchange and any trailing user turns.
        int lastAssistant = _messages.FindLastIndex(m => m is AssistantMessage);
        if (start >= lastAssistant) return null;
        int next = start + 1;
        while (next < _messages.Count && _messages[next] is ToolMessage) next++;
        return next <= lastAssistant ? next : null;
    }

    private string RenderCore(int start, bool final)
    {
        var low = !final && Reasoning == SmartReasoning.Low;
        var builder = new StringBuilder("<|im_start|>system\n").Append(_system);
        if (_tools is not null)
        {
            if (_system.Length > 0) builder.Append("\n\n");
            builder.Append("# Tools\n\nYou have access to the following functions:\n\n<tools>");
            foreach (var tool in _tools) builder.Append('\n').Append(ToolJson(tool));
            builder.Append("\n</tools>").Append(CallInstructions);
        }
        builder.Append("<|im_end|>\n");
        var messages = new List<Message> { new UserMessage(_input) };
        messages.AddRange(_messages.Skip(start));
        int lastUser = messages.FindLastIndex(m => m is UserMessage);
        for (int i = 0; i < messages.Count; i++)
        {
            switch (messages[i])
            {
                case UserMessage user:
                    builder.Append("<|im_start|>user\n").Append(Escape(user.Text)).Append(low && i == lastUser ? LowEffortMarker : "").Append("<|im_end|>\n");
                    break;
                case ToolMessage tool:
                    builder.Append("<|im_start|>user\n");
                    foreach (var result in tool.Results) builder.Append("<tool_response>\n").Append(Escape(result)).Append("\n</tool_response>\n");
                    builder.Append("<|im_end|>\n");
                    break;
                case AssistantMessage assistant:
                    RenderAssistant(builder, assistant, truncate: i < lastUser);
                    break;
            }
        }
        return builder.Append("<|im_start|>assistant\n").Append(final ? "<think></think>" : AssistantPrefix).ToString();
    }

    /// <summary>The template's assistant branch, including <c>truncate_history_thinking</c> for turns before the last user message.</summary>
    private static void RenderAssistant(StringBuilder builder, AssistantMessage message, bool truncate)
    {
        var content = message.Content;
        if (!content.Contains("<think>", StringComparison.Ordinal) && !content.Contains("</think>", StringComparison.Ordinal))
            content = "<think></think>" + content;
        builder.Append("<|im_start|>assistant\n");
        if (message.Calls.Count > 0)
        {
            if (content.Trim().Length > 0)
            {
                if (!truncate) builder.Append(content.Trim()).Append('\n');
                else
                {
                    var c = content;
                    if (c.Contains("</think>", StringComparison.Ordinal)) c = c[(c.LastIndexOf("</think>", StringComparison.Ordinal) + "</think>".Length)..];
                    else if (c.Contains("<think>", StringComparison.Ordinal)) c = c[..c.IndexOf("<think>", StringComparison.Ordinal)];
                    builder.Append("<think></think>").Append(c.Trim()).Append('\n');
                }
            }
            else builder.Append("<think></think>");
            foreach (var call in message.Calls)
            {
                builder.Append("<tool_call>\n<function=").Append(call.Name).Append(">\n");
                foreach (var (key, value) in call.Arguments) builder.Append("<parameter=").Append(key).Append(">\n").Append(Escape(value)).Append("\n</parameter>\n");
                builder.Append("</function>\n</tool_call>\n");
            }
            builder.Append("<|im_end|>\n");
            return;
        }
        if (!truncate) { builder.Append(content.Trim()).Append("<|im_end|>\n"); return; }
        if (content.Contains("<think>", StringComparison.Ordinal) && content.Contains("</think>", StringComparison.Ordinal))
            content = "<think></think>" + content[(content.LastIndexOf("</think>", StringComparison.Ordinal) + "</think>".Length)..];
        builder.Append(content.Trim()).Append("<|im_end|>\n");
    }

    private const string CallInstructions = "\n\nIf you choose to call a function ONLY reply in the following format with NO suffix:\n\n" +
        "<tool_call>\n<function=example_function_name>\n<parameter=example_parameter_1>\nvalue_1\n</parameter>\n" +
        "<parameter=example_parameter_2>\nThis is the value for the second parameter\nthat can span\nmultiple lines\n</parameter>\n" +
        "</function>\n</tool_call>\n\n<IMPORTANT>\nReminder:\n" +
        "- Function calls MUST follow the specified format: an inner <function=...></function> block must be nested within <tool_call></tool_call> XML tags\n" +
        "- Required parameters MUST be specified\n" +
        "- You may provide optional reasoning for your function call in natural language BEFORE the function call, but NOT after\n" +
        "- If there is no function call available, answer the question like normal with your current knowledge and do not tell the user about function calls\n" +
        "</IMPORTANT>";

    /// <summary>The template's <c>tool_to_json</c>: Python <c>json.dumps</c> separators, <c>ensure_ascii=False</c>, keys in order.</summary>
    internal static string ToolJson(object tool)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tool));
        var root = document.RootElement;
        if (root.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object) root = function;
        var builder = new StringBuilder("{");
        bool first = true;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is "defer_loading" or "strict") continue;
            if (!first) builder.Append(", ");
            first = false;
            builder.Append('"').Append(property.Name).Append("\": ");
            PythonJson(builder, property.Value);
        }
        return builder.Append('}').ToString();
    }

    internal static void PythonJson(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                bool first = true;
                foreach (var property in value.EnumerateObject())
                {
                    if (!first) builder.Append(", ");
                    first = false;
                    PythonString(builder, property.Name); builder.Append(": "); PythonJson(builder, property.Value);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                bool firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem) builder.Append(", ");
                    firstItem = false;
                    PythonJson(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String: PythonString(builder, value.GetString()!); break;
            case JsonValueKind.True: builder.Append("true"); break;
            case JsonValueKind.False: builder.Append("false"); break;
            case JsonValueKind.Null: builder.Append("null"); break;
            default: builder.Append(value.GetRawText()); break;
        }
    }

    private static void PythonString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }

    public string FinalVisible(string response) => WithoutReasoning("<think></think>" + response);
    public IReadOnlyList<SmartToolCall> ParseCalls(string visible) => Parse(visible);
    public string Visible(string response) => WithoutReasoning(AssistantPrefix + response);

    private static string Masked(string text) => Regex.Replace(text, @"<tool_call>[\s\S]*?</tool_call>", m => new string(' ', m.Length));
    internal static string WithoutReasoning(string response)
    {
        // The model can emit reasoning followed by a closing tag even with thinking disabled.
        // Ignore such reasoning, including call examples within it. A tag inside an actual
        // tool parameter is literal data and must not consume the surrounding call.
        var visible = SmartExperience.Clean(response);
        int close = Masked(visible).LastIndexOf("</think>", StringComparison.Ordinal);
        return close < 0 ? visible : visible[(close + "</think>".Length)..].Trim();
    }

    public static IReadOnlyList<SmartToolCall> Parse(string visible)
    {
        var calls = new List<SmartToolCall>();
        // The template's format is XML; JSON calls ({"name":..., "arguments":{...}}) are accepted as a fallback.
        var jsonBlocks = Regex.Matches(visible, @"<tool_call>\s*(\{[\s\S]*?\})\s*</tool_call>");
        foreach (Match block in jsonBlocks)
        {
            try
            {
                using var doc = JsonDocument.Parse(block.Groups[1].Value);
                var root = doc.RootElement;
                var name = root.GetProperty("name").GetString() ?? "";
                var args = new Dictionary<string, string>(StringComparer.Ordinal);
                if (root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object)
                    foreach (var p in a.EnumerateObject())
                        args[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                calls.Add(new(name, args));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            { throw new InvalidDataException("Malformed JSON tool call; expected {\"name\":..., \"arguments\":{...}}."); }
        }
        var blocks = Regex.Matches(visible, @"<tool_call>\s*<function=([a-z_]+)>\s*(.*?)\s*</function>\s*</tool_call>", RegexOptions.Singleline);
        foreach (Match block in blocks)
        {
            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            var body = block.Groups[2].Value;
            var parameters = Regex.Matches(body, @"<parameter=([a-z_]+)>\s*([\s\S]*?)\s*</parameter>");
            foreach (Match parameter in parameters)
                if (!args.TryAdd(parameter.Groups[1].Value, parameter.Groups[2].Value.Trim()))
                    throw new InvalidDataException("Duplicate tool argument.");
            if (!string.IsNullOrWhiteSpace(Regex.Replace(body, @"<parameter=([a-z_]+)>\s*([\s\S]*?)\s*</parameter>", "")))
                throw new InvalidDataException("Malformed tool arguments.");
            calls.Add(new(block.Groups[1].Value, args));
        }
        if (Regex.Matches(visible, "<tool_call>").Count != calls.Count || visible.Contains("<function=") && calls.Count == 0)
            throw new InvalidDataException("Incomplete or malformed tool call.");
        return calls;
    }

    public static string Result(object value) => JsonSerializer.Serialize(value);
    public static string Clip(string text, int bytes = SmartTools.OutputBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= bytes) return text;
        const string suffix = "\n[truncated]";
        var encoder = Encoding.UTF8.GetEncoder();
        var buffer = new byte[Math.Max(0, bytes - Encoding.UTF8.GetByteCount(suffix))];
        encoder.Convert(text.AsSpan(), buffer, false, out var used, out _, out _);
        return text[..used] + suffix;
    }
}
