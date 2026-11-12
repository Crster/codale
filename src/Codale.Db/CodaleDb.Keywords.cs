namespace Codale.Db;

/// <summary>
/// Keyword search: a node can carry one document per named field (a list of words the caller tokenised), and a
/// search ranks the documents of one field below a node by BM25.
/// </summary>
public sealed partial class CodaleDb
{
    private readonly Dictionary<string, KeywordIndex> _keywords = new(StringComparer.Ordinal);

    private KeywordIndex Keywords(string field)
    {
        if (!_keywords.TryGetValue(field, out var index))
        {
            index = new KeywordIndex();
            _keywords[field] = index;
        }
        return index;
    }

    internal void Index(string path, string field, IEnumerable<string> words)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        var text = string.Join(' ', words.SelectMany(KeywordIndex.Split));
        _lock.EnterWriteLock();
        try
        {
            var (segment, offset, size) = AppendIndex(path, field, text);
            ApplyIndex(path, field, text, segment, offset, size);
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal IReadOnlyList<DbHit> Match(string path, string field, IReadOnlyList<KeywordTerm> terms, int k)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (k <= 0 || terms.Count == 0) return [];
        _lock.EnterReadLock();
        try
        {
            var start = FindNode(path, create: false);
            if (start is null || !_keywords.TryGetValue(field, out var index)) return [];

            var documents = 0;
            long words = 0;
            var scan = new List<TNode>();
            Collect(start, scan, node => node.Docs is not null && node.Docs.ContainsKey(field));
            foreach (var node in scan)
            {
                documents++;
                words += node.Docs![field].Length;
            }
            if (documents == 0) return [];

            return index.Search(start, terms, k, documents, (double)words / documents, null)
                .Select(hit => new DbHit(new DbNode(this, hit.Doc.Node.PathString()), (float)hit.Score))
                .ToList();
        }
        finally { _lock.ExitReadLock(); }
    }
}
