namespace Codale.Core.Agents;

public enum FileChangeKind
{
    Create,
    Update,
    Delete,
    Unknown,
}

/// <summary>
/// A file edit attributed to a tool call, reconstructed from the CLI's
/// <c>tool_use_result</c>. Feeds the "changes in this session" panel.
/// </summary>
public sealed record FileChange
{
    public required FileChangeKind Kind { get; init; }
    public required string FilePath { get; init; }
    public string? NewContent { get; init; }
    public string? OriginalContent { get; init; }
    public bool UserModified { get; init; }

    /// <summary>
    /// The edit as the CLI's own patch (<c>structuredPatch</c>): hunks with real line
    /// numbers, so the transcript's diff reads the same as the file does. Null when the
    /// CLI sent none - the transcript then diffs the tool input instead.
    /// </summary>
    public IReadOnlyList<FilePatchHunk>? Patch { get; init; }
}

/// <summary>
/// One hunk of a file edit, provider-neutral: the start lines on each side and the
/// body lines verbatim, each still carrying its unified-diff prefix ('+', '-' or ' ').
/// </summary>
public sealed record FilePatchHunk
{
    public required int OldStart { get; init; }
    public required int NewStart { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
}
