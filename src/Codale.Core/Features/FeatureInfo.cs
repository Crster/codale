namespace Codale.Core.Features;

/// <summary>
/// One feature of a project - Login, Download, Dashboard - as history groups it: a big area
/// of the work. Fixes, improvements and refactors of the area all belong to the same feature.
/// </summary>
public sealed record FeatureInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>One line on what the feature is, taken from the prompt that created it.</summary>
    public string Description { get; init; } = "";

    /// <summary>The compact account of the feature's progress that every new session starts from.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Bytes of the current log already folded into <see cref="Summary"/>.</summary>
    public long SummaryOffset { get; init; }

    /// <summary>Size of the current log file.</summary>
    public long LogBytes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A stretch of a file the agent wrote, found by a search.</summary>
public sealed record FileHit(string FeatureId, string Path, int StartLine, string Text);