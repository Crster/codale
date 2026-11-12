namespace Codale.Db;

/// <summary>A word the keyword search looks for; with <see cref="Prefix"/> it also matches every word that begins with it.</summary>
public readonly record struct KeywordTerm(string Text, bool Prefix = false);

/// <summary>One node's text in one keyword index (a "field"): the words it holds, as the consumer tokenised them.</summary>
internal sealed class KeywordDoc
{
    public KeywordDoc(TNode node, string field, long sequence)
    {
        Node = node;
        Field = field;
        Sequence = sequence;
    }

    public readonly TNode Node;
    public readonly string Field;
    public readonly long Sequence;
    public int Length;
    public int Terms;
    /// <summary>Where the record holding this document's words sits in the log; the words themselves are not kept in memory.</summary>
    public int Segment;
    public long Offset;
    public int RecordSize;
    public bool Dead;
}

internal readonly record struct Posting(KeywordDoc Doc, int Tf);

/// <summary>
/// Okapi BM25 over the documents of one field. Postings are appended as documents arrive and dead ones are skipped
/// at query time, then swept out in bulk, so indexing a file never rewrites a posting list. Statistics (document
/// count, average length, document frequency) are taken over the subtree being searched, which is what a separate
/// full-text table per corpus gave the old SQLite store.
/// </summary>
internal sealed class KeywordIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly Dictionary<string, List<Posting>> _postings = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _terms = new(StringComparer.Ordinal);
    private long _livePostings;
    private long _deadPostings;

    public static string[] Split(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Indexes the words of <paramref name="text"/>; the caller keeps the text where it is stored.</summary>
    public void Add(KeywordDoc doc, string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = 0;
        foreach (var token in Split(text))
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
            length++;
        }
        doc.Length = length;
        doc.Terms = counts.Count;
        foreach (var (term, tf) in counts)
        {
            if (!_postings.TryGetValue(term, out var list))
            {
                list = [];
                _postings[term] = list;
                _terms.Add(term);
            }
            list.Add(new Posting(doc, tf));
        }
        _livePostings += counts.Count;
    }

    public void Kill(KeywordDoc doc)
    {
        if (doc.Dead) return;
        doc.Dead = true;
        _livePostings -= doc.Terms;
        _deadPostings += doc.Terms;
    }

    /// <summary>Drops dead postings once they outnumber the live ones.</summary>
    public void SweepIfNeeded()
    {
        if (_deadPostings < 2048 || _deadPostings < _livePostings) return;
        var empty = new List<string>();
        foreach (var (term, list) in _postings)
        {
            list.RemoveAll(p => p.Doc.Dead);
            if (list.Count == 0) empty.Add(term);
        }
        foreach (var term in empty)
        {
            _postings.Remove(term);
            _terms.Remove(term);
        }
        _deadPostings = 0;
    }

    /// <param name="root">Only documents at or below this node are candidates.</param>
    /// <param name="documents">How many documents the field holds under <paramref name="root"/>.</param>
    /// <param name="averageLength">Their mean length in words.</param>
    public List<(KeywordDoc Doc, double Score)> Search(
        TNode root, IReadOnlyList<KeywordTerm> query, int k, int documents, double averageLength, Func<TNode, bool>? where)
    {
        var scores = new Dictionary<KeywordDoc, double>();
        foreach (var term in query.Distinct())
        {
            var tfs = new Dictionary<KeywordDoc, int>();
            foreach (var word in Expand(term))
            {
                foreach (var posting in _postings[word])
                {
                    if (posting.Doc.Dead || !posting.Doc.Node.IsUnder(root)) continue;
                    tfs[posting.Doc] = tfs.GetValueOrDefault(posting.Doc) + posting.Tf;
                }
            }
            if (tfs.Count == 0) continue;
            var idf = Math.Log(1 + (documents - tfs.Count + 0.5) / (tfs.Count + 0.5));
            foreach (var (doc, tf) in tfs)
            {
                var norm = tf + K1 * (1 - B + B * doc.Length / Math.Max(1, averageLength));
                scores[doc] = scores.GetValueOrDefault(doc) + idf * tf * (K1 + 1) / norm;
            }
        }

        // Min-heap on (score, earlier-indexed-is-better): the weakest candidate leaves first.
        var best = new PriorityQueue<KeywordDoc, (double Score, long Order)>(k + 1);
        foreach (var (doc, score) in scores)
        {
            if (where is not null && !where(doc.Node)) continue;
            best.Enqueue(doc, (score, -doc.Sequence));
            if (best.Count > k) best.Dequeue();
        }
        var result = new List<(KeywordDoc, double)>(best.Count);
        while (best.TryDequeue(out var doc, out var priority)) result.Add((doc, priority.Score));
        result.Reverse();
        return result;
    }

    private IEnumerable<string> Expand(KeywordTerm term)
    {
        if (!term.Prefix)
        {
            if (_postings.ContainsKey(term.Text)) yield return term.Text;
            yield break;
        }
        foreach (var word in _terms.GetViewBetween(term.Text, term.Text + '￿'))
            if (word.StartsWith(term.Text, StringComparison.Ordinal)) yield return word;
    }
}
