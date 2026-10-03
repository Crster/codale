using System.Text.Json;

using Codale.Core.Agents;
using Codale.Git;

namespace Codale.App.ViewModels;

/// <summary>
/// Turns a file tool call into the diff the transcript shows. Two sources, in order
/// of trust: the CLI's own result (a structured patch with real line numbers, or the
/// before/after contents), and - until that arrives, or when it never does - the
/// call's input, which already says exactly what is being swapped. The input diff is
/// what makes an edit approval readable before it is allowed.
/// </summary>
public static class ToolDiffs
{
    /// <summary>The diff a call's input implies, or null for calls that change no file.</summary>
    public static FileDiff? FromInput(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var path = Str(input, "file_path") ?? "";

        switch (toolName)
        {
            case "Edit" when Str(input, "old_string") is { } oldText && Str(input, "new_string") is { } newText:
                return TextDiff.Compute(path, oldText, newText, numbered: false);

            case "MultiEdit":
                var edits = input.TryGetProperty("edits", out var array) && array.ValueKind == JsonValueKind.Array
                    ? array.EnumerateArray().ToList()
                    : [];
                if (edits.Count == 0)
                {
                    return null;
                }

                return TextDiff.Concat(path, edits.Select((edit, i) => TextDiff.Compute(
                    path,
                    Str(edit, "old_string") ?? "",
                    Str(edit, "new_string") ?? "",
                    numbered: false,
                    header: edits.Count > 1 ? $"edit {i + 1} of {edits.Count}" : null)));

            case "Write":
                return Str(input, "content") is { } content
                    ? TextDiff.Compute(path, null, content)
                    : null;
        }

        return null;
    }

    /// <summary>
    /// The diff the CLI's result supports, falling back to <paramref name="preview"/>
    /// when the result carries nothing better (some tools report the edit as prose).
    /// </summary>
    public static FileDiff? FromChange(FileChange change, FileDiff? preview)
    {
        if (change.Patch is { Count: > 0 } patch)
        {
            return TextDiff.FromPatch(change.FilePath, patch, isNew: change.Kind == FileChangeKind.Create);
        }

        if (change.Kind == FileChangeKind.Create && change.NewContent is { } created)
        {
            return TextDiff.Compute(change.FilePath, null, created);
        }

        if (change.OriginalContent is { } before && change.NewContent is { } after)
        {
            return TextDiff.Compute(change.FilePath, before, after);
        }

        // A multi-file preview only stands in for the file it is about.
        var samePath = preview is not null &&
                       (preview.Path.Length == 0 ||
                        string.Equals(Path.GetFileName(preview.Path), Path.GetFileName(change.FilePath), StringComparison.OrdinalIgnoreCase));

        return samePath ? preview! with { Path = change.FilePath } : null;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
