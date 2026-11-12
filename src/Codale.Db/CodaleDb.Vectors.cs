using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

using Codale.Core.Features;

namespace Codale.Db;

/// <summary>Semantic search: nodes carry a quantised embedding, and a query is a cosine scan over a subtree.</summary>
/// <remarks>
/// Each vector exists once in memory, on its node. A query scans the nodes of the subtree directly; what is
/// remembered between queries is only which nodes those are.
/// </remarks>
public sealed partial class CodaleDb
{
    /// <summary>The embedded nodes under each path queried since the vectors last changed.</summary>
    private readonly Dictionary<string, TNode[]> _scans = [];
    private long _scansVersion = -1;
    private string? _scansModel;
    private long _vectorVersion;

    /// <summary>One string per model name, however many vectors carry it.</summary>
    private readonly List<string> _models = [];

    private string ModelName(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[128];
        if (utf8.Length <= chars.Length)
        {
            var name = chars[..Encoding.UTF8.GetChars(utf8, chars)];
            if (name.SequenceEqual(_embedder.Model)) return _embedder.Model;
            foreach (var known in _models)
                if (name.SequenceEqual(known)) return known;
        }
        var model = Encoding.UTF8.GetString(utf8);
        _models.Add(model);
        return model;
    }

    internal void WriteTexts(string path, IEnumerable<string> texts)
    {
        var items = texts
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .Select(t => (Path: path + Sep + DbNode.Validate(t), Text: t, Vector: VectorMath.Quantize(_embedder.Embed(t))))
            .ToList();
        if (items.Count == 0) return;

        Batch(() =>
        {
            foreach (var (itemPath, text, vector) in items)
            {
                var node = FindNode(itemPath, create: true)!;
                AppendPut(node, itemPath, Encoding.UTF8.GetBytes(text), 0);
                AppendVector(node, itemPath, vector);
                Emit(itemPath, DbEventKind.Written, text);
            }
        });
    }

    /// <summary>Embeds <paramref name="text"/> and attaches the vector to the node (replacing any earlier one).</summary>
    internal void Embed(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var vector = VectorMath.Quantize(_embedder.Embed(text));
        _lock.EnterWriteLock();
        try
        {
            AppendVector(FindNode(path, create: true)!, path, vector);
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>True when the node has a vector made by the current embedder.</summary>
    internal bool HasCurrentVector(string path)
    {
        _lock.EnterReadLock();
        try { return FindNode(path, create: false) is { Vector: not null } node && node.VectorModel == _embedder.Model; }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Drops every vector under <paramref name="path"/> that a different embedder made.</summary>
    internal void DropStaleVectors(string path)
    {
        _lock.EnterWriteLock();
        try
        {
            var start = FindNode(path, create: false);
            if (start is null) return;
            var stale = new List<TNode>();
            Collect(start, stale, node => node.Vector is not null && node.VectorModel != _embedder.Model);
            foreach (var node in stale)
            {
                _dead += node.VectorRecordSize;
                node.Vector = null;
                node.VectorModel = null;
                node.VectorRecordSize = 0;
                _vectorVersion++;
            }
            if (stale.Count > 0) Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal IReadOnlyList<DbHit> Query(string path, string text, int k)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (k <= 0) return [];
        var query = _embedder.Embed(text);

        for (var attempt = 0; ; attempt++)
        {
            _lock.EnterReadLock();
            try
            {
                var start = FindNode(path, create: false);
                if (start is null) return [];
                if (ScanSet(path, start, reembedStale: attempt == 0) is { } nodes) return Top(nodes, query, k);
            }
            finally { _lock.ExitReadLock(); }

            ReembedStale(path);
        }
    }

    /// <summary>The nodes under <paramref name="start"/> with a current-model vector; null when stale self-texts should be re-embedded first.</summary>
    private TNode[]? ScanSet(string path, TNode start, bool reembedStale)
    {
        var version = Volatile.Read(ref _vectorVersion);
        lock (_scans)
        {
            // Sets made before the vectors last changed would pin removed nodes (and their vectors): let them all go.
            if (_scansVersion != version || _scansModel != _embedder.Model)
            {
                _scans.Clear();
                _scansVersion = version;
                _scansModel = _embedder.Model;
            }
            if (_scans.TryGetValue(path, out var known)) return known;
        }

        var found = new List<TNode>();
        Collect(start, found, node => node.Vector is not null);
        if (reembedStale && found.Any(IsStaleSelfText)) return null;
        found.RemoveAll(node => node.VectorModel != _embedder.Model);
        var nodes = found.ToArray();
        lock (_scans)
            if (_scansVersion == version) _scans[path] = nodes;
        return nodes;
    }

    private List<DbHit> Top(TNode[] nodes, float[] query, int k)
    {
        // Min-heap: the weakest of the best k leaves first.
        var best = new PriorityQueue<TNode, float>(Math.Min(k, nodes.Length) + 1);
        foreach (var node in nodes)
        {
            if (node.Vector!.Length != query.Length) continue;
            var score = Dot(node.Vector, query);
            if (best.Count < k) best.Enqueue(node, score);
            else if (best.TryPeek(out _, out var weakest) && score > weakest) best.EnqueueDequeue(node, score);
        }
        var hits = new List<DbHit>(best.Count);
        while (best.TryDequeue(out var node, out var score))
            if (score > 0) hits.Add(new DbHit(new DbNode(this, node.PathString()), score / 127f));
        hits.Reverse();
        return hits;
    }

    /// <summary>Dot product of an int8 row with a float query, eight lanes at a time where the CPU allows.</summary>
    internal static float Dot(ReadOnlySpan<byte> row, ReadOnlySpan<float> query)
    {
        var length = Math.Min(row.Length, query.Length);
        ref var bytes = ref Unsafe.As<byte, sbyte>(ref MemoryMarshal.GetReference(row));
        ref var floats = ref MemoryMarshal.GetReference(query);
        var total = 0f;
        var i = 0;
        if (Vector256.IsHardwareAccelerated && length >= 8)
        {
            var sum = Vector256<float>.Zero;
            for (; i <= length - 8; i += 8)
            {
                var eight = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<long>(ref Unsafe.As<sbyte, byte>(ref Unsafe.Add(ref bytes, i)))).AsSByte();
                var wide = Vector256.WidenLower(Vector128.WidenLower(eight).ToVector256Unsafe());
                sum += Vector256.ConvertToSingle(wide) * Vector256.LoadUnsafe(ref floats, (nuint)i);
            }
            total = Vector256.Sum(sum);
        }
        for (; i < length; i++) total += Unsafe.Add(ref bytes, i) * Unsafe.Add(ref floats, i);
        return total;
    }

    /// <summary>An entry written by <c>Write(list)</c> is its own text (its name); only those can be re-embedded without the caller.</summary>
    private bool IsStaleSelfText(TNode node) =>
        node.VectorModel != _embedder.Model
        && node is { HasValue: true, ValueFlags: 0 }
        && ReadString(node) == node.Name;

    private void ReembedStale(string path)
    {
        _lock.EnterWriteLock();
        try
        {
            var start = FindNode(path, create: false);
            if (start is null) return;
            var stale = new List<TNode>();
            Collect(start, stale, node => node.Vector is not null && IsStaleSelfText(node));
            foreach (var node in stale)
                AppendVector(node, node.PathString(), VectorMath.Quantize(_embedder.Embed(node.Name)));
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }
}
