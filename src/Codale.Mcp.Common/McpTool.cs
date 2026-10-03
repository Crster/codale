using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codale.Mcp;

/// <summary>One piece of tool output: text, or a base64 image.</summary>
public sealed record McpContent(string Type, string? Text = null, string? Data = null, string? MimeType = null)
{
    public static McpContent FromText(string text) => new("text", Text: text);

    public static McpContent FromImage(byte[] bytes, string mimeType = "image/png") =>
        new("image", Data: Convert.ToBase64String(bytes), MimeType: mimeType);

    internal JsonObject ToJson()
    {
        var node = new JsonObject { ["type"] = Type };
        if (Text is not null) node["text"] = Text;
        if (Data is not null) node["data"] = Data;
        if (MimeType is not null) node["mimeType"] = MimeType;
        return node;
    }
}

/// <summary>Tool arguments with forgiving typed accessors, so handlers stay short.</summary>
public sealed class McpArgs(JsonElement? raw)
{
    private readonly JsonElement? _raw = raw is { ValueKind: JsonValueKind.Object } ? raw : null;

    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        return _raw is { } obj && obj.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    public string? String(string name) =>
        TryGet(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    public string RequiredString(string name) =>
        String(name) is { Length: > 0 } s ? s : throw new McpToolException($"Missing required argument '{name}'.");

    /// <summary>A required choice argument ("action", "mode"), trimmed and lower-cased for matching.</summary>
    public string RequiredKeyword(string name) => RequiredString(name).Trim().ToLowerInvariant();

    public int? Int(string name) =>
        TryGet(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (int)Math.Round(d) : null;

    public int RequiredInt(string name) =>
        Int(name) ?? throw new McpToolException($"Missing required argument '{name}'.");

    /// <summary>An array argument's elements, or null when absent or not an array.</summary>
    public IReadOnlyList<JsonElement>? Array(string name) =>
        TryGet(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray()] : null;

    public IReadOnlyList<JsonElement> RequiredArray(string name) =>
        Array(name) ?? throw new McpToolException($"Missing required argument '{name}'.");

    public bool? Bool(string name) =>
        TryGet(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
}

/// <summary>Thrown by a handler for a user-correctable failure; reported as an isError result.</summary>
public sealed class McpToolException(string message) : Exception(message);

public sealed record McpTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<McpArgs, CancellationToken, Task<IReadOnlyList<McpContent>>> Handler)
{
    /// <summary>Builds an object schema from (name, type, description) triples; names in <c>required</c> are mandatory.</summary>
    public static JsonObject Schema(
        IEnumerable<(string Name, string Type, string Description)>? props = null,
        params string[] required)
    {
        var properties = new JsonObject();
        foreach (var (name, type, description) in props ?? [])
        {
            properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        }

        return schema;
    }

    public static Task<IReadOnlyList<McpContent>> Text(string text) =>
        Task.FromResult<IReadOnlyList<McpContent>>([McpContent.FromText(text)]);
}
