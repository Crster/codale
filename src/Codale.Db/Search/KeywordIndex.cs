using System.Runtime.InteropServices;

namespace Codale.Db;

/// <summary>A word the keyword search looks for; with <see cref="Prefix"/> it also matches every word that begins with it.</summary>
public readonly record struct KeywordTerm(string Text, bool Prefix = false);

/// <summary>
/// Okapi BM25 over the documents of one field (one document per node: the words the consumer tokenised).
/// Statistics (document count, average length, document frequency) are taken over the subtree being searched,
/// which is what a separate full-text table per corpus would give.
/// </summary>
/// <remarks>
/// Built to be small. A document is a row in one array (no object), identified by its row number; rows are handed
/// out in arrival order, so every posting list is ascending and is stored as delta-encoded varints, one to three
/// bytes a posting. A word seen in a single document needs no list at all. Removing a document only blanks its row;
/// the postings are skipped at query time and swept out in bulk once they outnumber the live ones. The words
/// themselves stay in the log: the row remembers where, for compaction.
/// </remarks>
internal sealed class KeywordIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int SweepMinimum = 2048;

    private struct Doc
    {
        public TNode? Node; // null once removed
        public long Offset;
        public int Segment;
        public int RecordSize;
        public int Length;
        public int Terms;
    }

    /// <summary>
    /// One word's postings: <see cref="Used"/> bytes of <see cref="Data"/>, ending at document <see cref="Last"/>.
    /// A word with a single posting has no array: <see cref="Last"/> is the document and <see cref="Used"/> its count.
    /// </summary>
    private struct Postings
    {
        public byte[]? Data;
        public int Used;
        public int Last;

        public readonly bool IsEmpty => Data is null && Used == 0;
    }

    private sealed record Stats(TNode Root, long Version, int Documents, long Words);

    private readonly Dictionary<string, Postings> _terms = new(StringComparer.Ordinal);
    private readonly Dictionary<TNode, int> _byNode = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private Doc[] _docs = new Doc[16];
    private int _docCount;
    private int _liveDocs;
    private long _livePostings;
    private long _deadPostings;
    private long _version;
    private volatile Stats? _stats;

    // Prefix search: the words sorted, plus the few that arrived since the last sort.
    private string[] _sorted = [];
    private readonly List<string> _unsorted = [];

    public int LiveDocuments => _liveDocs;

    public static string[] Split(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Indexes the words of <paramref name="text"/> for <paramref name="node"/>; the text stays in the log at the given record.</summary>
    public void Add(TNode node, ReadOnlySpan<char> text, int segment, long offset, int recordSize)
    {
        var counts = _counts.GetAlternateLookup<ReadOnlySpan<char>>();
        var length = 0;
        foreach (var range in text.Split(' '))
        {
            var token = text[range];
            if (token.IsEmpty) continue;
            CollectionsMarshal.GetValueRefOrAddDefault(counts, token, out _)++;
            length++;
        }

        if (_docCount == _docs.Length) Array.Resize(ref _docs, _docCount * 2);
        var id = _docCount++;
        _docs[id] = new Doc { Node = node, Offset = offset, Segment = segment, RecordSize = recordSize, Length = length, Terms = _counts.Count };
        _byNode[node] = id;
        _liveDocs++;
        _livePostings += _counts.Count;
        _version++;

        foreach (var (term, tf) in _counts)
        {
            ref var postings = ref CollectionsMarshal.GetValueRefOrAddDefault(_terms, term, out var known);
            if (!known) _unsorted.Add(term);
            Append(ref postings, id, tf);
        }
        _counts.Clear();
        if (_unsorted.Count > Math.Max(1024, _sorted.Length / 4)) SortTerms();
    }

    /// <summary>Forgets the node's document. Returns the size of its log record (now dead), or 0 when it had none.</summary>
    public int Remove(TNode node)
    {
        if (!_byNode.Remove(node, out var id)) return 0;
        ref var doc = ref _docs[id];
        doc.Node = null;
        _liveDocs--;
        _livePostings -= doc.Terms;
        _deadPostings += doc.Terms;
        _version++;
        return doc.RecordSize;
    }

    /// <summary>Visits every live document, letting the visitor move its record (compaction).</summary>
    public void Relocate(Func<TNode, int, long, int, (int Segment, long Offset, int Size)> move)
    {
        for (var id = 0; id < _docCount; id++)
        {
            ref var doc = ref _docs[id];
            if (doc.Node is null) continue;
            (doc.Segment, doc.Offset, doc.RecordSize) = move(doc.Node, doc.Segment, doc.Offset, doc.RecordSize);
        }
    }

    /// <summary>Drops dead postings (and renumbers the documents) once they outnumber the live ones.</summary>
    public void SweepIfNeeded()
    {
        if (_deadPostings < SweepMinimum || _deadPostings < _livePostings) return;

        var remap = new int[_docCount];
        var docs = new Doc[Math.Max(16, _liveDocs)];
        var kept = 0;
        for (var id = 0; id < _docCount; id++)
        {
            if (_docs[id].Node is not { } node)
            {
                remap[id] = -1;
                continue;
            }
            remap[id] = kept;
            _byNode[node] = kept;
            docs[kept++] = _docs[id];
        }
        _docs = docs;
        _docCount = kept;

        var live = new List<string>(_terms.Count);
        foreach (var term in _terms.Keys.ToArray())
        {
            ref var postings = ref CollectionsMarshal.GetValueRefOrNullRef(_terms, term);
            var rebuilt = default(Postings);
            var reader = new Reader(postings);
            while (reader.Next(out var id, out var tf))
                if (remap[id] >= 0) Append(ref rebuilt, remap[id], tf);
            if (rebuilt.IsEmpty)
            {
                _terms.Remove(term);
                continue;
            }
            if (rebuilt.Data is not null && rebuilt.Data.Length != rebuilt.Used) Array.Resize(ref rebuilt.Data, rebuilt.Used);
            postings = rebuilt;
            live.Add(term);
        }
        _terms.TrimExcess();
        _byNode.TrimExcess();
        _unsorted.Clear();
        _sorted = [.. live];
        Array.Sort(_sorted, StringComparer.Ordinal);
        _deadPostings = 0;
        _version++;
    }

    /// <summary>The <paramref name="k"/> documents at or below <paramref name="root"/> that best match any of <paramref name="query"/>, best first.</summary>
    public List<(TNode Node, double Score)> Search(TNode root, IReadOnlyList<KeywordTerm> query, int k)
    {
        var stats = StatsFor(root);
        if (stats.Documents == 0) return [];
        var averageLength = Math.Max(1, (double)stats.Words / stats.Documents);
        // When every live document is under the root there is nothing to filter: skip the walk up the tree.
        var everything = stats.Documents == _liveDocs;

        var scores = new Dictionary<int, double>();
        var tfs = new Dictionary<int, int>();
        foreach (var term in query.Distinct())
        {
            tfs.Clear();
            if (!term.Prefix) Gather(term.Text);
            else foreach (var word in WordsStarting(term.Text)) Gather(word);
            if (tfs.Count == 0) continue;

            var idf = Math.Log(1 + (stats.Documents - tfs.Count + 0.5) / (tfs.Count + 0.5));
            foreach (var (id, tf) in tfs)
            {
                var norm = tf + K1 * (1 - B + B * _docs[id].Length / averageLength);
                CollectionsMarshal.GetValueRefOrAddDefault(scores, id, out _) += idf * tf * (K1 + 1) / norm;
            }
        }

        // Min-heap on (score, earlier-indexed-is-better): the weakest candidate leaves first.
        var best = new PriorityQueue<int, (double Score, int Order)>(Math.Min(k, scores.Count) + 1);
        foreach (var (id, score) in scores)
        {
            best.Enqueue(id, (score, -id));
            if (best.Count > k) best.Dequeue();
        }
        var result = new List<(TNode, double)>(best.Count);
        while (best.TryDequeue(out var id, out var priority)) result.Add((_docs[id].Node!, priority.Score));
        result.Reverse();
        return result;

        void Gather(string word)
        {
            if (!_terms.TryGetValue(word, out var postings)) return;
            var reader = new Reader(postings);
            while (reader.Next(out var id, out var tf))
            {
                var node = _docs[id].Node;
                if (node is null || (!everything && !node.IsUnder(root))) continue;
                CollectionsMarshal.GetValueRefOrAddDefault(tfs, id, out _) += tf;
            }
        }
    }

    /// <summary>Document count and total length under a root. Remembered for the last root asked, until the index changes.</summary>
    private Stats StatsFor(TNode root)
    {
        var stats = _stats;
        if (stats is not null && ReferenceEquals(stats.Root, root) && stats.Version == _version) return stats;
        var documents = 0;
        long words = 0;
        for (var id = 0; id < _docCount; id++)
        {
            if (_docs[id].Node is not { } node || !node.IsUnder(root)) continue;
            documents++;
            words += _docs[id].Length;
        }
        return _stats = new Stats(root, _version, documents, words);
    }

    private IEnumerable<string> WordsStarting(string prefix)
    {
        var low = 0;
        var high = _sorted.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (string.CompareOrdinal(_sorted[middle], prefix) < 0) low = middle + 1;
            else high = middle;
        }
        for (; low < _sorted.Length && _sorted[low].StartsWith(prefix, StringComparison.Ordinal); low++) yield return _sorted[low];
        foreach (var word in _unsorted)
            if (word.StartsWith(prefix, StringComparison.Ordinal)) yield return word;
    }

    private void SortTerms()
    {
        var all = new string[_sorted.Length + _unsorted.Count];
        _sorted.CopyTo(all, 0);
        _unsorted.CopyTo(all, _sorted.Length);
        Array.Sort(all, StringComparer.Ordinal);
        _sorted = all;
        _unsorted.Clear();
    }

    private static void Append(ref Postings postings, int id, int tf)
    {
        if (postings.IsEmpty)
        {
            postings.Last = id;
            postings.Used = tf;
            return;
        }
        if (postings.Data is null)
        {
            // The second posting: move the inline first one into a real list.
            var (firstId, firstTf) = (postings.Last, postings.Used);
            postings.Data = new byte[8];
            postings.Used = 0;
            Encode(ref postings, firstId + 1, firstTf);
        }
        Encode(ref postings, id - postings.Last, tf);
        postings.Last = id;
    }

    /// <summary>A posting is the gap to the previous document, doubled, with the low bit set when a count above 1 follows.</summary>
    private static void Encode(ref Postings postings, int gap, int tf)
    {
        var head = ((ulong)(uint)gap << 1) | (tf > 1 ? 1UL : 0UL);
        var need = VarintSize(head) + (tf > 1 ? VarintSize((uint)tf) : 0);
        // Grow by a quarter: there are tens of thousands of these lists, and doubling leaves half of each empty.
        if (postings.Used + need > postings.Data!.Length)
            Array.Resize(ref postings.Data, postings.Used + need + Math.Max(4, postings.Used / 4));
        WriteVarint(postings.Data, ref postings.Used, head);
        if (tf > 1) WriteVarint(postings.Data, ref postings.Used, (uint)tf);
    }

    private static int VarintSize(ulong value)
    {
        var size = 1;
        for (; value >= 0x80; value >>= 7) size++;
        return size;
    }

    private static void WriteVarint(byte[] data, ref int position, ulong value)
    {
        while (value >= 0x80)
        {
            data[position++] = (byte)(value | 0x80);
            value >>= 7;
        }
        data[position++] = (byte)value;
    }

    private struct Reader(Postings postings)
    {
        private readonly byte[]? _data = postings.Data;
        private readonly int _used = postings.Used;
        private int _position;
        private int _id = postings.Data is null ? postings.Last : -1;

        public bool Next(out int id, out int tf)
        {
            if (_data is null)
            {
                // Inline single posting (or nothing at all): hand it out once.
                id = _id;
                tf = _position++ == 0 ? _used : 0;
                return tf > 0;
            }
            if (_position >= _used)
            {
                id = tf = 0;
                return false;
            }
            var head = ReadVarint();
            _id += (int)(head >> 1);
            id = _id;
            tf = (head & 1) == 0 ? 1 : (int)ReadVarint();
            return true;
        }

        private ulong ReadVarint()
        {
            ulong value = 0;
            var shift = 0;
            while (true)
            {
                var b = _data![_position++];
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) return value;
                shift += 7;
            }
        }
    }
}
