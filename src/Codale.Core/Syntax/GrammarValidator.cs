using System.Text.Json;

namespace Codale.Core.Syntax;

/// <summary>What a grammar says about itself; used to describe an imported or downloaded language.</summary>
public sealed record GrammarMeta(string ScopeName, string? Name, string[] FileTypes, string? FirstLineMatch)
{
    public string[] Filenames { get; init; } = [];
    public string[] FilenamePatterns { get; init; } = [];
}

/// <summary>
/// Checks a TextMate grammar before it is stored. Grammars come from the network or a model,
/// so they are treated as untrusted data: size-capped, structurally checked, and trial-tokenized
/// under a time limit. Nothing in a grammar is executed beyond its regular expressions.
/// </summary>
public static class GrammarValidator
{
    /// <summary>Returns null when the grammar is usable, otherwise a message fit to show the user.</summary>
    public static string? Validate(string json, out GrammarMeta meta, string? sample = null)
    {
        var error = ReadMetadata(json, out meta);
        return error ?? GrammarTokenizer.TrialTokenize(json, meta.ScopeName, sample ?? "// sample 123 \"text\"\nfoo(bar);");
    }

    internal static string? ReadMetadata(string json, out GrammarMeta meta)
    {
        meta = new GrammarMeta("", null, [], null);
        if (string.IsNullOrWhiteSpace(json))
        {
            return "The grammar is empty.";
        }

        if (json.Length > SyntaxStore.MaxGrammarBytes)
        {
            return "The grammar is larger than 1 MB.";
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "A grammar must be a JSON object.";
            }

            if (!root.TryGetProperty("scopeName", out var scope) || scope.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(scope.GetString()))
            {
                return "The grammar has no \"scopeName\" (for example \"source.mylang\").";
            }

            var hasPatterns = root.TryGetProperty("patterns", out var patterns) && patterns.ValueKind == JsonValueKind.Array;
            var hasRepository = root.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object;
            if (!hasPatterns && !hasRepository)
            {
                return "The grammar has no \"patterns\" or \"repository\".";
            }

            var fileTypes = root.TryGetProperty("fileTypes", out var ft) && ft.ValueKind == JsonValueKind.Array
                ? ft.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
                : [];
            meta = new GrammarMeta(
                scope.GetString()!,
                root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                fileTypes,
                root.TryGetProperty("firstLineMatch", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null)
            {
                Filenames = Strings(root, "filenames"),
                FilenamePatterns = Strings(root, "filenamePatterns"),
            };
        }
        catch (JsonException ex)
        {
            return $"The grammar is not valid JSON: {ex.Message}";
        }

        return null;
    }

    private static string[] Strings(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
}
