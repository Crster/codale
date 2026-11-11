using System.Text;

namespace Codale.Storage;

/// <summary>A node's address, rendered the way the database prints it: <c>setting."ai provider"."Claude API key"</c>.</summary>
public readonly struct DbPath : IEquatable<DbPath>
{
    private readonly string[]? _segments;

    internal DbPath(string[] segments) => _segments = segments;

    public IReadOnlyList<string> Segments => _segments ?? [];

    internal static DbPath FromRaw(string raw) => new(raw.Length == 0 ? [] : raw.Split(CodaleDb.Sep));

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var segment in Segments)
        {
            if (builder.Length > 0) builder.Append('.');
            if (IsPlain(segment)) builder.Append(segment);
            else builder.Append('"').Append(segment.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }
        return builder.ToString();
    }

    private static bool IsPlain(string segment) => segment.Length > 0 && segment.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    public bool Equals(DbPath other) => Segments.SequenceEqual(other.Segments);
    public override bool Equals(object? obj) => obj is DbPath other && Equals(other);
    public override int GetHashCode() => Segments.Aggregate(17, (hash, s) => hash * 31 + s.GetHashCode());
    public static bool operator ==(DbPath left, DbPath right) => left.Equals(right);
    public static bool operator !=(DbPath left, DbPath right) => !left.Equals(right);
}

/// <summary>A semantic-search result: the matching entry, its text and its cosine similarity to the query.</summary>
public sealed record DbHit(DbPath Path, string Text, float Score);

public enum DbEventKind
{
    Written,
    Removed,
}

/// <summary>A committed change. <see cref="Value"/> is null for removals, blobs and protected values (never leaked to watchers).</summary>
public sealed record DbEvent(DbPath Path, DbEventKind Kind, string? Value);

/// <summary>A pub/sub message and the node it was published on.</summary>
public sealed record DbMessage(DbPath Topic, string Payload);

public enum FlushMode
{
    /// <summary>Writes reach the OS cache immediately (survives a process crash, not a power cut). Fastest.</summary>
    Os,

    /// <summary>fsync after every write or batch.</summary>
    Always,

    /// <summary>fsync on a timer (<see cref="CodaleDbOptions.FlushInterval"/>).</summary>
    Interval,
}

public sealed class CodaleDbOptions
{
    public FlushMode Flush { get; init; } = FlushMode.Os;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The log rolls to a new file after this many bytes.</summary>
    public long SegmentBytes { get; init; } = 64L << 20;

    /// <summary>Embeds text for <see cref="DbNode.Write(IEnumerable{string})"/> and <see cref="DbNode.Query"/>.</summary>
    public IEmbedder? Embedder { get; init; }

    /// <summary>Rewrites the log when at least <see cref="CompactMinDeadBytes"/> and half of it is dead.</summary>
    public bool AutoCompact { get; init; } = true;

    public long CompactMinDeadBytes { get; init; } = 8L << 20;

    /// <summary>Brotli quality for blobs, 0 (fastest) to 11 (smallest).</summary>
    public int BlobQuality { get; init; } = 5;
}
