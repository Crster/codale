namespace Codale.Db;

/// <summary>A search result: the matching node and how well it matched (cosine similarity, or BM25 for keyword search).</summary>
public sealed record DbHit(DbNode Node, float Score)
{
    public DbPath Path => Node.Location;

    /// <summary>The node's name; for entries written with <see cref="DbNode.Write(IEnumerable{string})"/> that is the text itself.</summary>
    public string Text => Node.Name;
}

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

    /// <summary>Embeds text for <see cref="DbNode.Write(IEnumerable{string})"/>, <see cref="DbNode.Embed"/> and <see cref="DbNode.Query"/>.</summary>
    public IEmbedder? Embedder { get; init; }

    /// <summary>Rewrites the log when at least <see cref="CompactMinDeadBytes"/> and half of it is dead.</summary>
    public bool AutoCompact { get; init; } = true;

    public long CompactMinDeadBytes { get; init; } = 8L << 20;

    /// <summary>Brotli quality for blobs, 0 (fastest) to 11 (smallest).</summary>
    public int BlobQuality { get; init; } = 5;

    /// <summary>
    /// How long to wait for another process that has the database open. One process owns a database at a time;
    /// databases shared between processes are opened briefly and closed, so a short wait is enough.
    /// </summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.Zero;
}
