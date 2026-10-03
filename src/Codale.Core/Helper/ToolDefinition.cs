using System.Text.Json;

namespace Codale.Core.Helper;

public enum ToolParameterType
{
    String,
    Integer,
    Number,
    Boolean,
}

public sealed record ToolParameter
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public ToolParameterType Type { get; init; } = ToolParameterType.String;
    public bool Required { get; init; }

    /// <summary>When set, the value must be one of these literals.</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }
}

/// <summary>A tool the helper model may call, described once and rendered into the prompt.</summary>
public sealed record ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<ToolParameter> Parameters { get; init; } = [];

    /// <summary>Renders the tool for the system prompt, so the model knows what it means.</summary>
    public string ToPromptLine()
    {
        var parameters = Parameters.Count == 0
            ? "no arguments"
            : string.Join(", ", Parameters.Select(p =>
                $"{p.Name}{(p.Required ? "" : "?")}: {p.Type.ToString().ToLowerInvariant()} - {p.Description}"));

        return $"- {Name}: {Description} ({parameters})";
    }
}

/// <summary>One validated tool call produced by the model.</summary>
public sealed record ToolCall(string Tool, JsonElement Arguments)
{
    /// <summary>
    /// Reads <c>{"tool": ..., "arguments": {...}}</c> without checking it against any tool
    /// definition; null when it is not that shape. <see cref="HelperToolCalls.Parse"/> is the
    /// validating entry point for model output.
    /// </summary>
    public static ToolCall? FromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tool", out var tool) ||
                tool.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var arguments = root.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                ? args.Clone()
                : default;
            return new ToolCall(tool.GetString()!, arguments);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? GetString(string name) =>
        Arguments.ValueKind == JsonValueKind.Object &&
        Arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public int? GetInt(string name) =>
        Arguments.ValueKind == JsonValueKind.Object &&
        Arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
