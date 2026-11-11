namespace Codale.Storage;

internal static class ValueFlags
{
    public const byte Protected = 1;
    public const byte Blob = 2;
}

/// <summary>Where a value lives in the log. Small plain values are also cached, so a read is a dictionary walk.</summary>
internal sealed class ValueRef
{
    public ValueRef(int segment, long offset, int length, byte flags, int recordSize)
    {
        Segment = segment;
        Offset = offset;
        Length = length;
        Flags = flags;
        RecordSize = recordSize;
    }

    public int Segment;
    public long Offset;
    public readonly int Length;
    public readonly byte Flags;
    public int RecordSize;
    public byte[]? Cache;
}

/// <summary>One node of the path tree. The tree is the hierarchy, the key index and the graph's containment edges at once.</summary>
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
    public ValueRef? Value;
    public byte[]? Vector;
    public string? VectorModel;
    public int VectorRecordSize;
    public List<(string Rel, string Target)>? Links;

    public bool IsEmpty => Value is null && Vector is null && (Children is null || Children.Count == 0) && (Links is null || Links.Count == 0);

    public string PathString()
    {
        if (Parent is null) return "";
        var parent = Parent.PathString();
        return parent.Length == 0 ? Name : parent + CodaleDb.Sep + Name;
    }
}
