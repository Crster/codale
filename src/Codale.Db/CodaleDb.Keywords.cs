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
            var (segment, offset, size) = Append(RecordType.Index, 0, EncodeStrings(path, field, text));
            ApplyIndex(FindNode(path, create: true)!, field, text, segment, offset, size);
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

            var hits = index.Search(start, terms, k);
            var result = new List<DbHit>(hits.Count);
            foreach (var (node, score) in hits) result.Add(new DbHit(new DbNode(this, node.PathString()), (float)score));
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }
}
