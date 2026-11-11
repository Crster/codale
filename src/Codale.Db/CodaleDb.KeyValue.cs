using System.Text;

namespace Codale.Db;

public sealed partial class CodaleDb
{
    private const int CacheLimit = 1024;

    internal string? ReadText(string path)
    {
        _lock.EnterReadLock();
        try
        {
            var value = FindNode(path, create: false)?.Value;
            if (value is null) return null;
            if (value.Flags != 0) throw new InvalidOperationException("This value is protected or a blob; use ReadProtect()/ReadBlob().");
            return Encoding.UTF8.GetString(ReadRaw(value));
        }
        finally { _lock.ExitReadLock(); }
    }

    internal bool Exists(string path)
    {
        _lock.EnterReadLock();
        try { return FindNode(path, create: false)?.Value is not null; }
        finally { _lock.ExitReadLock(); }
    }

    internal void WriteText(string path, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteBytes(path, Encoding.UTF8.GetBytes(value), 0, value);
    }

    private void WriteBytes(string path, ReadOnlySpan<byte> value, byte flags, string? eventValue)
    {
        _lock.EnterWriteLock();
        try
        {
            ApplyPut(path, AppendPut(path, value, flags));
            Emit(path, DbEventKind.Written, eventValue);
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal bool Remove(string path)
    {
        _lock.EnterWriteLock();
        try
        {
            if (FindNode(path, create: false) is null) return false;
            var append = _log.Append(RecordType.Remove, 0, Encoding.UTF8.GetBytes(path));
            ApplyRemove(path, append.Size);
            Emit(path, DbEventKind.Removed, null);
            Committed();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal IReadOnlyList<string> Children(string path)
    {
        _lock.EnterReadLock();
        try
        {
            var children = FindNode(path, create: false)?.Children;
            return children is null ? [] : children.Keys.Order(StringComparer.Ordinal).ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    internal int ChildCount(string path)
    {
        _lock.EnterReadLock();
        try { return FindNode(path, create: false)?.Children?.Count ?? 0; }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Every node below <paramref name="path"/> that holds a value.</summary>
    internal IReadOnlyList<DbNode> Descendants(string path, bool sorted)
    {
        _lock.EnterReadLock();
        try
        {
            var start = FindNode(path, create: false);
            if (start is null) return [];
            var found = new List<DbNode>();
            Walk(start);
            return found;

            void Walk(TNode node)
            {
                if (node.Children is null) return;
                IEnumerable<TNode> children = node.Children.Values;
                if (sorted) children = children.OrderBy(c => c.Name, StringComparer.Ordinal);
                foreach (var child in children)
                {
                    if (child.Value is not null) found.Add(new DbNode(this, child.PathString()));
                    Walk(child);
                }
            }
        }
        finally { _lock.ExitReadLock(); }
    }
}
