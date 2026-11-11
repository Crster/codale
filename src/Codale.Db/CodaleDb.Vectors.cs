using System.Text;

using Codale.Core.Features;

namespace Codale.Db;

/// <summary>Semantic search: nodes carry a quantised embedding, and a query is a cosine scan over a subtree.</summary>
public sealed partial class CodaleDb
{
    private readonly Dictionary<string, MatrixCache> _matrices = [];
    private long _vectorVersion;

    private sealed record MatrixCache(long Version, TNode[] Nodes, QuantizedMatrix Matrix);

    internal void WriteTexts(string path, IEnumerable<string> texts)
    {
        var items = texts
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .Select(t => (Path: path + Sep + DbNode.Validate(t), Text: t, Vector: VectorMath.Quantize(_embedder.Embed(t))))
            .ToList();
        if (items.Count == 0) return;

        _lock.EnterWriteLock();
        try
        {
            foreach (var (itemPath, text, vector) in items)
            {
                ApplyPut(itemPath, AppendPut(itemPath, Encoding.UTF8.GetBytes(text), 0));
                ApplyVector(itemPath, _embedder.Model, vector, AppendVector(itemPath, _embedder.Model, vector));
                Emit(itemPath, DbEventKind.Written, text);
            }
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Embeds <paramref name="text"/> and attaches the vector to the node (replacing any earlier one).</summary>
    internal void Embed(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var vector = VectorMath.Quantize(_embedder.Embed(text));
        _lock.EnterWriteLock();
        try
        {
            ApplyVector(path, _embedder.Model, vector, AppendVector(path, _embedder.Model, vector));
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>True when the node has a vector made by the current embedder.</summary>
    internal bool HasCurrentVector(string path)
    {
        _lock.EnterReadLock();
        try { return FindNode(path, create: false)?.Vector?.Model == _embedder.Model; }
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
            Collect(start, stale, node => node.Vector is not null && node.Vector.Model != _embedder.Model);
            foreach (var node in stale)
            {
                _dead += node.Vector!.RecordSize;
                node.Vector = null;
                _vectorVersion++;
            }
            if (stale.Count > 0) MaybeCompact();
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
            var reembed = false;
            _lock.EnterReadLock();
            try
            {
                var start = FindNode(path, create: false);
                if (start is null) return [];

                MatrixCache? cache;
                lock (_matrices) _matrices.TryGetValue(path, out cache);
                if (cache is null || cache.Version != Volatile.Read(ref _vectorVersion))
                {
                    var nodes = new List<TNode>();
                    Collect(start, nodes, node => node.Vector is not null);
                    if (attempt == 0 && nodes.Any(IsStaleSelfText))
                    {
                        reembed = true;
                    }
                    else
                    {
                        nodes = nodes.Where(n => n.Vector!.Model == _embedder.Model).ToList();
                        var rows = nodes.Select((n, i) => ((long)i, n.Vector!.Data)).ToList();
                        cache = new MatrixCache(Volatile.Read(ref _vectorVersion), [.. nodes], QuantizedMatrix.From(rows));
                        lock (_matrices) _matrices[path] = cache;
                    }
                }

                if (!reembed)
                {
                    return cache!.Matrix.Top(query, k)
                        .Where(r => r.Score > 0)
                        .Select(r => new DbHit(new DbNode(this, cache.Nodes[r.Id].PathString()), r.Score))
                        .ToList();
                }
            }
            finally { _lock.ExitReadLock(); }

            ReembedStale(path);
        }
    }

    /// <summary>An entry written by <c>Write(list)</c> is its own text (its name); only those can be re-embedded without the caller.</summary>
    private bool IsStaleSelfText(TNode node) =>
        node.Vector!.Model != _embedder.Model
        && node.Value is { Flags: 0 } value
        && Encoding.UTF8.GetString(ReadRaw(value)) == node.Name;

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
            {
                var nodePath = node.PathString();
                var vector = VectorMath.Quantize(_embedder.Embed(node.Name));
                ApplyVector(nodePath, _embedder.Model, vector, AppendVector(nodePath, _embedder.Model, vector));
            }
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

}
