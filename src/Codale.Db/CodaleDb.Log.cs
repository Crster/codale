using System.Buffers.Binary;
using System.Text;

namespace Codale.Db;

/// <summary>The plumbing between the log on disk and the in-memory tree: appending records, replaying them, applying them.</summary>
public sealed partial class CodaleDb
{
    private long _documentSequence;

    private ValueRef AppendPut(string path, ReadOnlySpan<byte> value, byte flags)
    {
        var keyLength = Encoding.UTF8.GetByteCount(path);
        var head = new byte[4 + keyLength];
        BinaryPrimitives.WriteInt32LittleEndian(head, keyLength);
        Encoding.UTF8.GetBytes(path, head.AsSpan(4));
        var append = _log.Append(RecordType.Put, flags, head, value);
        var reference = new ValueRef(append.Segment, append.Offset + SegmentLog.HeaderSize + head.Length, value.Length, flags, append.Size);
        if (flags == 0 && value.Length <= CacheLimit) reference.Cache = value.ToArray();
        return reference;
    }

    private int AppendVector(string path, string model, byte[] vector)
    {
        var keyLength = Encoding.UTF8.GetByteCount(path);
        var modelLength = Encoding.UTF8.GetByteCount(model);
        var head = new byte[4 + keyLength + 2 + modelLength];
        BinaryPrimitives.WriteInt32LittleEndian(head, keyLength);
        Encoding.UTF8.GetBytes(path, head.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4 + keyLength), (ushort)modelLength);
        Encoding.UTF8.GetBytes(model, head.AsSpan(6 + keyLength));
        return _log.Append(RecordType.Vector, 0, head, vector).Size;
    }

    private (int Segment, long Offset, int Size) AppendIndex(string path, string field, string text) =>
        _log.Append(RecordType.Index, 0, EncodeStrings(path, field, text));

    /// <summary>The words of a document, read back from its record: they are not kept in memory.</summary>
    private string ReadIndexText(KeywordDoc doc)
    {
        var body = new byte[doc.RecordSize - SegmentLog.HeaderSize];
        _log.Read(doc.Segment, doc.Offset + SegmentLog.HeaderSize, body);
        return DecodeStrings(body, 3)[2];
    }

    private static byte[] EncodeStrings(params string[] values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    private static string[] DecodeStrings(ReadOnlySpan<byte> body, int count)
    {
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(body);
            result[i] = Encoding.UTF8.GetString(body.Slice(4, length));
            body = body[(4 + length)..];
        }
        return result;
    }

    private byte[] ReadRaw(ValueRef value)
    {
        if (value.Cache is not null) return value.Cache;
        var bytes = new byte[value.Length];
        _log.Read(value.Segment, value.Offset, bytes);
        return bytes;
    }

    private void Replay(int segment, long offset, int size, RecordType type, byte flags, ReadOnlySpan<byte> body)
    {
        switch (type)
        {
            case RecordType.Put:
            {
                var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body);
                var path = Encoding.UTF8.GetString(body.Slice(4, keyLength));
                var value = body[(4 + keyLength)..];
                var reference = new ValueRef(segment, offset + SegmentLog.HeaderSize + 4 + keyLength, value.Length, flags, size);
                if (flags == 0 && value.Length <= CacheLimit) reference.Cache = value.ToArray();
                ApplyPut(path, reference);
                break;
            }
            case RecordType.Remove:
                ApplyRemove(Encoding.UTF8.GetString(body), size);
                break;
            case RecordType.Vector:
            {
                var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body);
                var path = Encoding.UTF8.GetString(body.Slice(4, keyLength));
                var modelLength = BinaryPrimitives.ReadUInt16LittleEndian(body[(4 + keyLength)..]);
                var model = Encoding.UTF8.GetString(body.Slice(6 + keyLength, modelLength));
                ApplyVector(path, model, body[(6 + keyLength + modelLength)..].ToArray(), size);
                break;
            }
            case RecordType.Index:
            {
                var parts = DecodeStrings(body, 3);
                ApplyIndex(parts[0], parts[1], parts[2], segment, offset, size);
                break;
            }
            case RecordType.Link:
            {
                var parts = DecodeStrings(body, 3);
                ApplyLink(parts[0], parts[1], parts[2]);
                break;
            }
            case RecordType.Unlink:
            {
                var parts = DecodeStrings(body, 3);
                _dead += size;
                ApplyUnlink(parts[0], parts[1], parts[2]);
                break;
            }
        }
    }

    // Derived data (a vector, keyword documents) belongs to the node, not to one value: rewriting the value keeps
    // it, and the caller embeds or indexes again when the text changed.
    private void ApplyPut(string path, ValueRef reference)
    {
        var node = FindNode(path, create: true)!;
        if (node.Value is not null) _dead += node.Value.RecordSize;
        node.Value = reference;
    }

    private void ApplyVector(string path, string model, byte[] vector, int recordSize)
    {
        var node = FindNode(path, create: true)!;
        if (node.Vector is not null) _dead += node.Vector.RecordSize;
        node.Vector = new VectorDoc(model, vector, recordSize);
        _vectorVersion++;
    }

    private void ApplyIndex(string path, string field, string text, int segment, long offset, int size)
    {
        var node = FindNode(path, create: true)!;
        node.Docs ??= [];
        if (node.Docs.Remove(field, out var old))
        {
            _dead += old.RecordSize;
            Keywords(field).Kill(old);
        }
        var doc = new KeywordDoc(node, field, ++_documentSequence) { Segment = segment, Offset = offset, RecordSize = size };
        Keywords(field).Add(doc, text);
        node.Docs[field] = doc;
    }

    private void ApplyRemove(string path, int removeRecordSize)
    {
        _dead += removeRecordSize;
        var node = FindNode(path, create: false);
        if (node is null || node.Parent is null) return;

        var all = new List<TNode>();
        Collect(node, all, _ => true);
        foreach (var removed in all)
        {
            _dead += removed.Value?.RecordSize ?? 0;
            if (removed.Vector is not null)
            {
                _dead += removed.Vector.RecordSize;
                _vectorVersion++;
            }
            if (removed.Docs is not null)
            {
                foreach (var doc in removed.Docs.Values)
                {
                    _dead += doc.RecordSize;
                    Keywords(doc.Field).Kill(doc);
                }
            }
        }

        node.Parent.Children!.Remove(node.Name);
        for (var parent = node.Parent; parent is { Parent: not null } && parent.IsEmpty; parent = parent.Parent)
            parent.Parent.Children!.Remove(parent.Name);
    }

    private void ApplyLink(string from, string rel, string to)
    {
        var node = FindNode(from, create: true)!;
        node.Links ??= [];
        if (!node.Links.Contains((rel, to))) node.Links.Add((rel, to));
    }

    private void ApplyUnlink(string from, string rel, string to)
    {
        var node = FindNode(from, create: false);
        node?.Links?.Remove((rel, to));
    }

    private TNode? FindNode(string path, bool create)
    {
        var node = _root;
        if (path.Length == 0) return node;
        var rest = path.AsSpan();
        while (true)
        {
            var cut = rest.IndexOf(Sep);
            var part = cut < 0 ? rest : rest[..cut];

            TNode? next = null;
            node.Children?.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(part, out next);
            if (next is null)
            {
                if (!create) return null;
                next = new TNode(part.ToString(), node);
                (node.Children ??= new Dictionary<string, TNode>(StringComparer.Ordinal))[next.Name] = next;
            }
            node = next;
            if (cut < 0) return node;
            rest = rest[(cut + 1)..];
        }
    }

    private static void Collect(TNode node, List<TNode> into, Func<TNode, bool> keep)
    {
        if (keep(node)) into.Add(node);
        if (node.Children is null) return;
        foreach (var child in node.Children.Values) Collect(child, into, keep);
    }
}
