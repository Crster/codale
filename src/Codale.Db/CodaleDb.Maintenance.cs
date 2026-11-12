namespace Codale.Db;

public sealed partial class CodaleDb
{
    private long _dead;
    private int _batchDepth;

    /// <summary>
    /// Runs <paramref name="action"/> as one unit: no other thread reads or writes in between, and the log is
    /// synced and compacted once at the end. A crash part-way leaves none of the batch behind: its records are
    /// bracketed in the log, and an unfinished bracket is cut off the next time the database opens. (An exception
    /// thrown by <paramref name="action"/> does not undo the writes made before it.)
    /// </summary>
    public void Batch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _lock.EnterWriteLock();
        try
        {
            _batchDepth++;
            try { action(); }
            finally
            {
                if (--_batchDepth == 0) CommitBatch();
            }
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

    /// <summary>
    /// Copies every live record into fresh segments, syncs them, then deletes the old ones. Old data is only deleted
    /// once its replacement is on stable storage, so a crash at any point leaves a log that replays to the same state.
    /// </summary>
    private void CompactLocked()
    {
        CommitBatch(); // a batch in flight is committed up to here; what follows starts a new one
        var first = _log.BeginCompaction();
        var nodes = new List<TNode>();
        Collect(_root, nodes, node => node.Parent is not null && (node.HasValue || node.Vector is not null || node.Links is { Count: > 0 }));
        foreach (var node in nodes)
        {
            if (node.HasValue)
            {
                var moved = _log.Copy(node.ValueSegment, node.ValueRecordOffset, node.ValueRecordSize);
                node.ValueSegment = moved.Segment;
                node.ValueRecordOffset = moved.Offset;
            }
            if (node.Vector is null && node.Links is not { Count: > 0 }) continue;
            var path = node.PathString();
            if (node.Vector is not null)
                node.VectorRecordSize = AppendVectorRecord(path, node.VectorModel!, node.Vector, compacting: true);
            if (node.Links is not null)
                foreach (var (rel, target) in node.Links) _log.Append(RecordType.Link, 0, EncodeStrings(path, rel, target));
        }
        foreach (var index in _keywords.Values)
            index.Relocate((_, segment, offset, size) => _log.Copy(segment, offset, size));
        _log.Sync();
        _log.DeleteSegmentsBefore(first);
        _dead = 0;
    }
}
