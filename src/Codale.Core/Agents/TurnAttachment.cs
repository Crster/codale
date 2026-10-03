namespace Codale.Core.Agents;

/// <summary>What kind of payload an attachment carries, so each CLI can map it to its own wire format.</summary>
public enum TurnAttachmentKind
{
    Image,
    Pdf,
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
    public static TurnAttachmentKind? KindOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => TurnAttachmentKind.Image,
        ".pdf" => TurnAttachmentKind.Pdf,
        _ => null,
    };
}
