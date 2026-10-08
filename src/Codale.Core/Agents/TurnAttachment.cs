namespace Codale.Core.Agents;

/// <summary>What kind of payload an attachment carries, so each CLI can map it to its own wire format.</summary>
public enum TurnAttachmentKind
{
    Image,
    Pdf,
    Text,
}

/// <summary>
/// A file the user attached to a turn. The path stays on disk - each driver decides
/// how it reaches the model (Claude takes base64 blocks) - and the bytes are only read at send time.
/// </summary>
public sealed record TurnAttachment
{
    public required TurnAttachmentKind Kind { get; init; }

    /// <summary>File name only, for chips and transcript rows.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path on disk.</summary>
    public required string Path { get; init; }

    /// <summary>Turns a picked file into an attachment, or null when its type is not supported.</summary>
    public static TurnAttachment? FromPath(string path)
    {
        var kind = KindOf(path);
        return kind is null ? null : new TurnAttachment { Kind = kind.Value, Name = System.IO.Path.GetFileName(path), Path = path };
    }

    /// <summary>The extension decides the kind; anything else is not attachable.</summary>
    public static TurnAttachmentKind? KindOf(string path)
    {
        var extension = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => TurnAttachmentKind.Image,
            ".pdf" => TurnAttachmentKind.Pdf,
            _ => IsTextExtension(extension) ? TurnAttachmentKind.Text : null,
        };
    }

    /// <summary>
    /// The text formats worth attaching as-is. Code and config files ride along too -
    /// the model reads them the same way - so the list is extension-of-record, not an
    /// editorial judgment about what counts as text.
    /// </summary>
    public static bool IsTextExtension(string extension) => TextExtensions.Contains(extension);

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    private static readonly HashSet<string> TextExtensions = new(StringComparer.Ordinal)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".log", ".json", ".jsonc",
        ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".cs", ".csx", ".vb", ".fs", ".fsx", ".cpp", ".cc", ".cxx", ".h", ".hpp",
        ".c", ".rs", ".go", ".py", ".pyw", ".rb", ".php", ".java", ".kt", ".kts",
        ".swift", ".m", ".mm", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
        ".vue", ".svelte", ".html", ".htm", ".css", ".scss", ".less", ".sql",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".psd1", ".bat", ".cmd",
        ".diff", ".patch", ".env", ".gitignore", ".gitattributes",
        ".editorconfig", ".dockerfile", ".r", ".dart", ".lua", ".pl", ".scala",
    };

    /// <summary>Extensions the attach button's picker offers, in kind order.</summary>
    public static string[] PickerExtensions { get; } = [.. ImageExtensions, ".pdf", .. TextExtensions];
}
