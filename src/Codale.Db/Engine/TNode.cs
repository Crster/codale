namespace Codale.Db;

internal static class ValueFlags
{
    public const byte Protected = 1;
    public const byte Blob = 2;
}

/// <summary>
/// One node of the path tree. The tree is the hierarchy, the key index and the graph's containment edges at once.
/// Everything a node can carry is a field here rather than an object hanging off it: with tens of thousands of
/// nodes, each extra object (and its header) is what the database's memory is made of.
/// </summary>
internal sealed class TNode
{
    public TNode(string name, TNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    public readonly string Name;
    public readonly TNode? Parent;
    public Dictionary<string, TNode>? Children;

    // The value: where its Put record sits in the log. Segment ids start at 1, so 0 means "no value".
    public int ValueSegment;
    public int ValueRecordSize;
    public long ValueRecordOffset;
    public int ValueLength;
    public byte ValueFlags;

    /// <summary>How many keyword indexes hold a document for this node (the documents themselves live in the indexes).</summary>
    public ushort Documents;

    /// <summary>The bytes of a tiny plain value (settings, flags, ids), so reading one never touches the log.</summary>
    public byte[]? Cache;

    // The embedding: a unit vector quantised to int8, the model that made it and the size of its log record.
    public byte[]? Vector;
    public string? VectorModel;
    public int VectorRecordSize;

    public List<(string Rel, string Target)>? Links;

    public bool HasValue => ValueSegment != 0;

    /// <summary>The value's bytes are the tail of its record.</summary>
    public long ValueOffset => ValueRecordOffset + ValueRecordSize - ValueLength;

    public void SetValue(int segment, long recordOffset, int recordSize, int length, byte flags)
    {
        ValueSegment = segment;
        ValueRecordOffset = recordOffset;
        ValueRecordSize = recordSize;
        ValueLength = length;
        ValueFlags = flags;
    }

    public bool IsEmpty =>
        !HasValue && Vector is null && Documents == 0
        && (Children is null || Children.Count == 0) && (Links is null || Links.Count == 0);

    public string PathString()
    {
        if (Parent is null) return "";
        var length = -1;
        for (var node = this; node.Parent is not null; node = node.Parent) length += node.Name.Length + 1;
        return string.Create(length, this, static (span, leaf) =>
        {
            var end = span.Length;
            for (var node = leaf; node.Parent is not null; node = node.Parent)
            {
                end -= node.Name.Length;
                node.Name.CopyTo(span[end..]);
                if (end > 0) span[--end] = CodaleDb.Sep;
            }
        });
    }

    public bool IsUnder(TNode ancestor)
    {
        for (var node = this; node is not null; node = node.Parent)
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }
}
