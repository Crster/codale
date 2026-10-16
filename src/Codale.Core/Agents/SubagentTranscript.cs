using System.Text.Json;

namespace Codale.Core.Agents;

/// <summary>One step a background subagent took, read back from its transcript.</summary>
public abstract record SubagentStep;

public sealed record SubagentToolUse(string Id, string Name, JsonElement Input) : SubagentStep;

public sealed record SubagentToolResult(string Id, string? Text, bool IsError) : SubagentStep;

public sealed record SubagentText(string MessageId, string Text, bool IsThinking) : SubagentStep;

/// <summary>
/// A background subagent (Agent with run_in_background) streams nothing to the parent's
/// stdout: its work lands only in its own transcript,
/// <c>&lt;config&gt;/projects/&lt;project&gt;/&lt;session&gt;/subagents/agent-&lt;id&gt;.jsonl</c>.
/// Reading that back is the only way to show what it is doing while it runs. Pure, so the
/// format it depends on is covered by unit tests rather than trusted.
/// </summary>
public static class SubagentTranscript
{
    /// <summary>The transcript of a session's subagent, or null when the CLI has not written one (yet).</summary>
    public static string? Find(string configHome, string sessionId, string agentId)
    {
        if (agentId.Length == 0 || sessionId.Length == 0
            || agentId.IndexOfAny(['/', '\\', '.']) >= 0 || sessionId.IndexOfAny(['/', '\\', '.']) >= 0)
        {
            return null;
        }

        try
        {
            var projects = Path.Combine(configHome, "projects");
            if (!Directory.Exists(projects))
            {
                return null;
            }

            var name = $"agent-{agentId}.jsonl";
            foreach (var project in Directory.EnumerateDirectories(projects))
            {
                var path = Path.Combine(project, sessionId, "subagents", name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>The steps one transcript line records; nothing for the prompt, attachments and bookkeeping.</summary>
    public static IReadOnlyList<SubagentStep> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (type is not ("assistant" or "user")
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var messageId = message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()!
                : root.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String ? uuid.GetString()! : "";

            var steps = new List<SubagentStep>();
            foreach (var block in content.EnumerateArray())
            {
                switch (Str(block, "type"))
                {
                    case "text" when type == "assistant" && Str(block, "text") is { Length: > 0 } text:
                        steps.Add(new SubagentText(messageId, text, false));
                        break;
                    case "thinking" when Str(block, "thinking") is { Length: > 0 } thought:
                        steps.Add(new SubagentText(messageId, thought, true));
                        break;
                    case "tool_use" when Str(block, "id") is { } useId && Str(block, "name") is { } name:
                        var input = block.TryGetProperty("input", out var i) ? i.Clone() : default;
                        steps.Add(new SubagentToolUse(useId, name, input));
                        break;
                    case "tool_result" when Str(block, "tool_use_id") is { } resultId:
                        var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                        steps.Add(new SubagentToolResult(resultId, ResultText(block), isError));
                        break;
                }
            }

            return steps;
        }
        catch (JsonException)
        {
            // A line caught half-written: the reader only hands over complete lines, but stay safe.
            return [];
        }
    }

    private static string? ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = content.EnumerateArray()
            .Where(p => Str(p, "type") == "text")
            .Select(p => Str(p, "text"))
            .OfType<string>()
            .ToList();
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
