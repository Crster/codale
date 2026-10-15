using System.Text.Json;

namespace Codale.Agents.OpenAi;

/// <summary>
/// Parses model lists from OpenAI-compatible and Anthropic-compatible /models endpoints,
/// extracting model IDs and context length metadata when available.
/// </summary>
public static class OpenAiModelParser
{
    private static readonly string[] ArrayProperties = ["data", "models", "result"];
    private static readonly string[] IdProperties = ["id", "slug", "model", "name"];
    private static readonly string[] ContextProperties =
    [
        "context_length",
        "context_window",
        "max_tokens",
        "max_input_tokens",
        "max_completion_tokens",
        "max_output_tokens",
    ];

    /// <summary>
    /// Models from an OpenAI-style list, including context metadata when the endpoint publishes it.
    /// Supports envelopes (data, models, result, data.data) or a bare array.
    /// </summary>
    public static List<(string Id, int? ContextWindow)> ReadModels(JsonElement root)
    {
        static JsonElement? FindArray(JsonElement e, int depth = 0)
        {
            if (e.ValueKind == JsonValueKind.Array)
            {
                return e;
            }

            if (e.ValueKind != JsonValueKind.Object || depth > 2)
            {
                return null;
            }

            foreach (var name in ArrayProperties)
            {
                if (e.TryGetProperty(name, out var inner) && FindArray(inner, depth + 1) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        if (FindArray(root) is not { } array)
        {
            return [];
        }

        static (string Id, int? ContextWindow) Model(JsonElement model)
        {
            if (model.ValueKind == JsonValueKind.String)
            {
                var str = model.GetString()?.Trim() ?? "";
                return (str, null);
            }

            string id = "";
            foreach (var key in IdProperties)
            {
                if (model.ValueKind == JsonValueKind.Object &&
                    model.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    id = value.GetString()?.Trim() ?? "";
                    if (id.Length > 0)
                    {
                        break;
                    }
                }
            }

            int? window = null;
            foreach (var key in ContextProperties)
            {
                if (model.ValueKind == JsonValueKind.Object &&
                    model.TryGetProperty(key, out var value))
                {
                    if (value.ValueKind == JsonValueKind.Number &&
                        value.TryGetInt32(out var parsed) && parsed > 0)
                    {
                        window = parsed;
                        break;
                    }

                    if (value.ValueKind == JsonValueKind.String &&
                        int.TryParse(value.GetString(), out var strParsed) && strParsed > 0)
                    {
                        window = strParsed;
                        break;
                    }
                }
            }

            return (id, window);
        }

        return array
            .EnumerateArray()
            .Select(Model)
            .Where(model => model.Id.Length > 0)
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}