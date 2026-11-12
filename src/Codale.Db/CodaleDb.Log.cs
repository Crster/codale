using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Codale.Db;

/// <summary>The plumbing between the log on disk and the in-memory tree: appending records, replaying them, applying them.</summary>
public sealed partial class CodaleDb
{
    /// <summary>Where a batch that was never committed begins, found while replaying; the log is cut back to it.</summary>
    private (int Segment, long Offset)? _unfinishedBatch;
    private bool _batchOpen;

    /// <summary>
    /// Every write goes through here. Inside <see cref="Batch"/> the first record is preceded by a Begin marker and
    /// the batch ends with a Commit; replay drops a Begin that has no Commit, so a batch is all or nothing.
    /// </summary>
    private (int Segment, long Offset, int Size) Append(RecordType type, byte flags, ReadOnlySpan<byte> part1, ReadOnlySpan<byte> part2 = default)
    {
        if (_batchDepth == 0) CommitBatch(); // a commit that failed to write earlier (disk full) is written before anything else
        else if (!_batchOpen)
        {
            _dead += _log.Append(RecordType.Begin, 0, default).Size;
            _batchOpen = true;
        }
        return _log.Append(type, flags, part1, part2);
    }

    private void CommitBatch()
    {
        if (!_batchOpen) return;
        _dead += _log.Append(RecordType.Commit, 0, default).Size;
        _batchOpen = false;
    }

    private void AppendPut(TNode node, string path, ReadOnlySpan<byte> value, byte flags)
    {
        var keyLength = Encoding.UTF8.GetByteCount(path);
        var head = ArrayPool<byte>.Shared.Rent(4 + keyLength);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(head, keyLength);
            Encoding.UTF8.GetBytes(path, head.AsSpan(4));
            var append = Append(RecordType.Put, flags, head.AsSpan(0, 4 + keyLength), value);
            if (node.HasValue) _dead += node.ValueRecordSize;
            node.SetValue(append.Segment, append.Offset, append.Size, value.Length, flags);
            node.Cache = flags == 0 && value.Length <= CacheLimit ? value.ToArray() : null;
        }
        finally { ArrayPool<byte>.Shared.Return(head); }
    }

    private void AppendVector(TNode node, string path, byte[] vector)
    {
        var size = AppendVectorRecord(path, _embedder.Model, vector);
        SetVector(node, _embedder.Model, vector, size);
    }

    private int AppendVectorRecord(string path, string model, byte[] vector, bool compacting = false)
    {
        var keyLength = Encoding.UTF8.GetByteCount(path);
        var modelLength = Encoding.UTF8.GetByteCount(model);
        var length = 4 + keyLength + 2 + modelLength;
        var head = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(head, keyLength);
            Encoding.UTF8.GetBytes(path, head.AsSpan(4));
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4 + keyLength), (ushort)modelLength);
            Encoding.UTF8.GetBytes(model, head.AsSpan(6 + keyLength));
            return compacting
                ? _log.Append(RecordType.Vector, 0, head.AsSpan(0, length), vector).Size
                : Append(RecordType.Vector, 0, head.AsSpan(0, length), vector).Size;
        }
        finally { ArrayPool<byte>.Shared.Return(head); }
    }

    private static byte[] EncodeStrings(string first, string second, ReadOnlySpan<char> third)
    {
        var encoding = Encoding.UTF8;
        var body = new byte[12 + encoding.GetByteCount(first) + encoding.GetByteCount(second) + encoding.GetByteCount(third)];
        var at = 0;
        Write(first, body);
        Write(second, body);
        Write(third, body);
        return body;

        void Write(ReadOnlySpan<char> value, byte[] into)
        {
            var written = encoding.GetBytes(value, into.AsSpan(at + 4));
            BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), written);
            at += 4 + written;
        }
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

    /// <summary>The node's value bytes: the cached copy of a tiny value, or a read from the log.</summary>
    private byte[] ReadRaw(TNode node)
    {
        if (node.Cache is not null) return node.Cache;
        var bytes = new byte[node.ValueLength];
        _log.Read(node.ValueSegment, node.ValueOffset, bytes);
        return bytes;
    }

    /// <summary>The node's value as text, decoded without an intermediate array for anything that fits a pooled buffer.</summary>
    private string ReadString(TNode node)
    {
        if (node.Cache is not null) return Encoding.UTF8.GetString(node.Cache);
        var buffer = ArrayPool<byte>.Shared.Rent(node.ValueLength);
        try
        {
            var bytes = buffer.AsSpan(0, node.ValueLength);
            _log.Read(node.ValueSegment, node.ValueOffset, bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void Replay(int segment, long offset, int size, RecordType type, byte flags, ReadOnlySpan<byte> body)
    {
        switch (type)
        {
            case RecordType.Put:
            {
                var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body);
                var node = FindNode(Encoding.UTF8.GetString(body.Slice(4, keyLength)), create: true)!;
                var value = body[(4 + keyLength)..];
                if (node.HasValue) _dead += node.ValueRecordSize;
                node.SetValue(segment, offset, size, value.Length, flags);
                node.Cache = flags == 0 && value.Length <= CacheLimit ? value.ToArray() : null;
                break;
            }
            case RecordType.Remove:
                ApplyRemove(Encoding.UTF8.GetString(body), size);
                break;
            case RecordType.Vector:
            {
                var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body);
                var node = FindNode(Encoding.UTF8.GetString(body.Slice(4, keyLength)), create: true)!;
                var modelLength = BinaryPrimitives.ReadUInt16LittleEndian(body[(4 + keyLength)..]);
                var model = ModelName(body.Slice(6 + keyLength, modelLength));
                SetVector(node, model, body[(6 + keyLength + modelLength)..].ToArray(), size);
                break;
            }
            case RecordType.Index:
            {
                var parts = DecodeStrings(body, 3);
                ApplyIndex(FindNode(parts[0], create: true)!, parts[1], parts[2], segment, offset, size);
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
            case RecordType.Begin:
                _dead += size;
                _unfinishedBatch ??= (segment, offset);
                break;
            case RecordType.Commit:
                _dead += size;
                _unfinishedBatch = null;
                break;
        }
    }

    /// <summary>Forgets everything replayed so far (the log was cut back and is replayed again).</summary>
    private void ResetState()
    {
        _root.Children = null;
        _keywords.Clear();
        _models.Clear();
        _dead = 0;
        _unfinishedBatch = null;
        _vectorVersion++;
    }

    // Derived data (a vector, keyword documents) belongs to the node, not to one value: rewriting the value keeps
    // it, and the caller embeds or indexes again when the text changed.
    private void SetVector(TNode node, string model, byte[] vector, int recordSize)
    {
        if (node.Vector is not null) _dead += node.VectorRecordSize;
        node.Vector = vector;
        node.VectorModel = model;
        node.VectorRecordSize = recordSize;
        _vectorVersion++;
    }

    private void ApplyIndex(TNode node, string field, ReadOnlySpan<char> text, int segment, long offset, int size)
    {
        var index = Keywords(field);
        var replaced = index.Remove(node);
        if (replaced > 0) _dead += replaced;
        else node.Documents++;
        index.Add(node, text, segment, offset, size);
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
            if (removed.HasValue) _dead += removed.ValueRecordSize;
            if (removed.Vector is not null)
            {
                _dead += removed.VectorRecordSize;
                _vectorVersion++;
            }
            if (removed.Documents > 0)
                foreach (var index in _keywords.Values) _dead += index.Remove(removed);
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
