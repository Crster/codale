namespace Codale.Db;

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

/// <summary>A node's embedding: a unit vector quantised to int8, and the model that made it.</summary>
internal sealed class VectorDoc
{
    public VectorDoc(string model, byte[] data, int recordSize)
    {
        Model = model;
        Data = data;
        RecordSize = recordSize;
    }

    public readonly string Model;
    public readonly byte[] Data;
    public int RecordSize;
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
    public VectorDoc? Vector;
    public Dictionary<string, KeywordDoc>? Docs;
    public List<(string Rel, string Target)>? Links;

    public bool IsEmpty =>
        Value is null && Vector is null && (Docs is null || Docs.Count == 0)
        && (Children is null || Children.Count == 0) && (Links is null || Links.Count == 0);

    public string PathString()
    {
        if (Parent is null) return "";
        var parent = Parent.PathString();
        return parent.Length == 0 ? Name : parent + CodaleDb.Sep + Name;
    }

    public bool IsUnder(TNode ancestor)
    {
        for (var node = this; node is not null; node = node.Parent)
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }
}
