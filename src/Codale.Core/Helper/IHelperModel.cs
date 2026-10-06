using System.Text.Json;

using Codale.Core.Text;

namespace Codale.Core.Helper;

/// <summary>
/// The model behind Codale's small background jobs - commit messages, session titles,
/// message routing and code search - as opposed to a chat session. Backed by a one-shot
/// call to the Claude CLI or an API endpoint, so no model ships with the app.
/// </summary>
public interface IHelperModel
{
    /// <summary>True when the Claude CLI is installed (or an API endpoint is set) to answer with.</summary>
    bool IsAvailable { get; }

    /// <summary>Free-form generation: one system prompt, one prompt, the reply text.</summary>
    /// <exception cref="HelperModelException">No CLI is installed, or the CLI failed.</exception>
    Task<string> CompleteAsync(string systemPrompt, string prompt, CancellationToken ct = default);

    /// <summary>
    /// Free-form generation with room for a longer reply than the one-liner default. A model
    /// that has no cap to raise answers as <see cref="CompleteAsync(string, string, CancellationToken)"/>.
    /// </summary>
    Task<string> CompleteAsync(string systemPrompt, string prompt, int maxTokens, CancellationToken ct = default) =>
        CompleteAsync(systemPrompt, prompt, ct);

    /// <summary>
    /// Asks the model to answer with exactly one of <paramref name="tools"/>. Null means it
    /// produced nothing that parses as a call to one of them.
    /// </summary>
    /// <exception cref="HelperModelException">No CLI is installed, or the CLI failed.</exception>
    Task<ToolCall?> CallToolAsync(
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default);

    /// <summary>
    /// Lets the model make several calls in one reply, for loops where independent calls
    /// (a grep and the reads it points to) need not each cost a round trip. A model that
    /// can only make one answers as <see cref="CallToolAsync"/>. Empty means no usable call.
    /// </summary>
    async Task<IReadOnlyList<ToolCall>> CallToolsAsync(
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default) =>
        await CallToolAsync(systemPrompt, conversation, tools, ct).ConfigureAwait(false) is { } call ? [call] : [];

    /// <summary>
    /// A hint that a <see cref="CallToolAsync"/> with this prompt and these tools is coming,
    /// so a model that is slow to start can get ready. Safe to call often; never blocks.
    /// </summary>
    void Prewarm(string systemPrompt, IReadOnlyList<ToolDefinition> tools)
    {
    }
}

/// <summary>A helper-model request could not be answered: no CLI, a failed process or an empty reply.</summary>
public sealed class HelperModelException : Exception
{
    public HelperModelException(string message) : base(message) { }

    public HelperModelException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Turns a model's text reply into a <see cref="ToolCall"/>, so every backend shares one contract.</summary>
public static class HelperToolCalls
{
    /// <summary>The system prompt with the tool contract appended.</summary>
    public static string BuildSystemPrompt(string systemPrompt, IReadOnlyList<ToolDefinition> tools) =>
        systemPrompt +
        "\n\nTools:\n" +
        string.Join("\n", tools.Select(t => t.ToPromptLine())) +
        "\n\nReply format: exactly one JSON object calling one tool, and nothing else - no markdown fence, " +
        "no text before or after it:\n" +
        "{\"tool\": \"<tool name>\", \"arguments\": {\"<parameter>\": <value>}}\n" +
        "Where a parameter lists allowed values, use exactly one of them.";

    /// <summary>
    /// Reads the first JSON object in <paramref name="reply"/> as a call and checks it against
    /// the tools: an unknown tool, a missing required argument or a value outside the allowed
    /// list makes the call unusable (null) rather than something the caller must re-validate.
    /// </summary>
    public static ToolCall? Parse(string? reply, IReadOnlyList<ToolDefinition> tools)
    {
        // A small model's leaked <think> block may hold braces of its own: drop it before looking for the call.
        if (ExtractObject(ModelOutput.CleanCall(reply)) is not { } json)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tool", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var definition = tools.FirstOrDefault(t =>
                string.Equals(t.Name, nameElement.GetString(), StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                return null;
            }

            var arguments = root.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                ? args.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();

            foreach (var parameter in definition.Parameters)
            {
                var present = arguments.TryGetProperty(parameter.Name, out var value) && value.ValueKind != JsonValueKind.Null;
                if (!present)
                {
                    if (parameter.Required)
                    {
                        return null;
                    }

                    continue;
                }

                if (parameter.AllowedValues is { } allowed &&
                    !allowed.Contains(value.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return new ToolCall(definition.Name, arguments);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first balanced <c>{...}</c> in the text, ignoring braces inside strings.</summary>
    internal static string? ExtractObject(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        while (start >= 0)
        {
            var depth = 0;
            var inString = false;
            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}' && --depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }

            start = text.IndexOf('{', start + 1);
        }

        return null;
    }
}
