namespace Codale.Db;

public sealed partial class CodaleDb
{
    private long _dead;
    private int _batchDepth;

    /// <summary>
    /// Runs <paramref name="action"/> as one unit: no other thread reads or writes in between, and the log is
    /// synced and compacted once at the end. Writes are visible in order but a crash can interrupt the batch halfway.
    /// </summary>
    public void Batch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _lock.EnterWriteLock();
        try
        {
            _batchDepth++;
            try { action(); }
            finally { _batchDepth--; }
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Forces everything written so far to stable storage.</summary>
    public void Flush()
    {
        if (_disposed) return;
        _lock.EnterReadLock();
        try { _log.Sync(); }
        catch (ObjectDisposedException) { }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Rewrites the log with only live data, reclaiming overwritten and removed records.</summary>
    public void Compact()
    {
        _lock.EnterWriteLock();
        try { CompactLocked(); }
        finally { _lock.ExitWriteLock(); }
    }

    private void Committed()
    {
        if (_batchDepth > 0) return;
        foreach (var index in _keywords.Values) index.SweepIfNeeded();
        if (_options.Flush == FlushMode.Always) _log.Sync();
        MaybeCompact();
    }

    private void MaybeCompact()
    {
        if (_options.AutoCompact && _dead >= _options.CompactMinDeadBytes && _dead * 2 >= _log.TotalBytes) CompactLocked();
    }

    private void CompactLocked()
    {
        var first = _log.BeginCompaction();
        var nodes = new List<TNode>();
        Collect(_root, nodes, _ => true);
        foreach (var node in nodes)
        {
            if (node.Parent is null) continue;
            var path = node.PathString();
            if (node.Value is { } value)
            {
                var moved = AppendPut(path, ReadRaw(value), value.Flags);
                moved.Cache = value.Cache;
                node.Value = moved;
            }
            if (node.Vector is { } vector)
                node.Vector = new VectorDoc(vector.Model, vector.Data, AppendVector(path, vector.Model, vector.Data));
            if (node.Docs is not null)
                foreach (var doc in node.Docs.Values)
                {
                    var (segment, offset, size) = AppendIndex(path, doc.Field, ReadIndexText(doc));
                    doc.Segment = segment;
                    doc.Offset = offset;
                    doc.RecordSize = size;
                }
            if (node.Links is not null)
                foreach (var (rel, target) in node.Links) _log.Append(RecordType.Link, 0, EncodeStrings(path, rel, target));
        }
        _log.Sync();
        _log.DeleteSegmentsBefore(first);
        _dead = 0;
    }
}
