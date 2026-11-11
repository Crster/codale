namespace Codale.Db;

/// <summary>
/// The graph: the path tree is the containment hierarchy, and named links connect any two nodes.
/// <c>Find</c> walks both.
/// </summary>
public sealed partial class CodaleDb
{
    internal void Link(string from, string rel, string to)
    {
        ArgumentException.ThrowIfNullOrEmpty(rel);
        _lock.EnterWriteLock();
        try
        {
            if (FindNode(from, create: false)?.Links?.Contains((rel, to)) == true) return;
            _log.Append(RecordType.Link, 0, EncodeStrings(from, rel, to));
            ApplyLink(from, rel, to);
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal bool Unlink(string from, string rel, string to)
    {
        _lock.EnterWriteLock();
        try
        {
            var node = FindNode(from, create: false);
            if (node?.Links is null || !node.Links.Contains((rel, to))) return false;
            var append = _log.Append(RecordType.Unlink, 0, EncodeStrings(from, rel, to));
            _dead += append.Size;
            ApplyUnlink(from, rel, to);
            Committed();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal IReadOnlyList<DbPath> Links(string from, string? rel)
    {
        _lock.EnterReadLock();
        try { return LinkTargets(FindNode(from, create: false), rel).Select(DbPath.FromRaw).ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    private IEnumerable<string> LinkTargets(TNode? node, string? rel)
    {
        if (node?.Links is null) yield break;
        foreach (var (linkRel, target) in node.Links)
        {
            if (rel is not null && linkRel != rel) continue;
            var targetNode = FindNode(target, create: false);
            if (targetNode is { IsEmpty: false }) yield return target;
        }
    }

    /// <summary>Every descendant that holds a value, plus the nodes this one links to, optionally filtered by a case-insensitive substring of the relative path.</summary>
    internal IReadOnlyList<DbPath> Find(string path, string? filter)
    {
        _lock.EnterReadLock();
        try
        {
            var start = FindNode(path, create: false);
            if (start is null) return [];
            var found = new List<string>();
            var prefixLength = path.Length == 0 ? 0 : path.Length + 1;
            Walk(start);
            foreach (var target in LinkTargets(start, null))
                if (Matches(target, filter) && !found.Contains(target)) found.Add(target);
            return found.Select(DbPath.FromRaw).ToList();

            void Walk(TNode node)
            {
                if (node.Children is null) return;
                foreach (var child in node.Children.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    var childPath = child.PathString();
                    if (child.Value is not null && Matches(childPath[prefixLength..], filter)) found.Add(childPath);
                    Walk(child);
                }
            }
        }
        finally { _lock.ExitReadLock(); }

        static bool Matches(string text, string? filter) =>
            string.IsNullOrEmpty(filter) || text.Replace(Sep, '.').Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}
