using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Codale.Core.Features;
using Codale.Db;

namespace Codale.Search;

/// <summary>
/// What the index keeps in the project's database, read and written straight through the <see cref="CodaleDb"/> path
/// API. Layout:
/// <list type="bullet">
/// <item><c>source.WORKTREE.file.PATH</c> = content hash; <c>source.WORKTREE.chunk.PATH.LINE.ID</c> = the chunk, with its
/// words in the <c>source</c> keyword field and its embedding; <c>source.WORKTREE.version</c> = chunker version.</item>
/// <item><c>knowledge.ID</c> = a finding (keyword field <c>knowledge</c>).</item>
/// <item><c>learned.WORKTREE.HASH</c> = a prompt-to-file association (keyword field <c>learned</c>).</item>
/// <item><c>retrieval.ID</c> = what one retrieval cost; <c>meta.sequence</c> = the id counter.</item>
/// </list>
/// Features, provider sessions and delivered context are in <c>ProjectKnowledgeIndex.Features.cs</c>.
/// </summary>
public sealed partial class ProjectKnowledgeIndex
{
    private const string SourceField = "source";
    private const string KnowledgeField = "knowledge";
    private const string LearnedField = "learned";
    private const int MaxLearnedPerProject = 20_000;
    private const int IdBlock = 1000;

    private sealed record ChunkRecord(long Id, string Hash, int StartLine, string Text, string Symbols);

    private sealed record KnowledgeRecord(string SessionId, string Provider, string Worktree, string Revision, string Text,
        IReadOnlyList<SourceReference> Refs, long Seq);

    private sealed record LearnedRecord(string SessionId, string Path, string Prompt, string Kind, long Seq);

    private sealed record RetrievalRecord(string SessionId, long ElapsedMs, int Tokens, IReadOnlyList<string> Refs,
        int ExplorationCalls, int InputTokens, int OutputTokens);

    /// <summary>
    /// Where each chunk id seen lately lives, so a ranking can work in ids and read the text back only for the winners.
    /// Bounded: past the cap it starts again, and an id that is gone simply yields no chunk (the next search re-finds it).
    /// </summary>
    private readonly ConcurrentDictionary<long, DbNode> _chunkNodes = new();
    private const int MaxRememberedChunks = 20_000;

    internal void RememberChunk(long id, DbNode node)
    {
        if (_chunkNodes.Count >= MaxRememberedChunks) _chunkNodes.Clear();
        _chunkNodes[id] = node;
    }

    private readonly object _idGate = new();
    private long _nextId = -1;
    private long _idLimit;

    /// <summary>The next id, unique for the life of the database. Reserved a block at a time, so a crash only skips numbers.</summary>
    private long NextId()
    {
        lock (_idGate)
        {
            if (_nextId < 0 || _nextId >= _idLimit)
            {
                var counter = db.Open("meta").Open("sequence");
                var start = _nextId < 0 ? (long.TryParse(counter.Read(), out var saved) ? saved : 0) : _idLimit;
                _nextId = start;
                _idLimit = start + IdBlock;
                counter.Write(_idLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return ++_nextId;
        }
    }

    private static IReadOnlyList<KeywordTerm> Terms(IReadOnlyList<(string Word, bool Prefix)> terms) =>
        [.. terms.Select(t => new KeywordTerm(t.Word, t.Prefix))];

    // ---- source index

    private DbNode SourceRoot(string worktree) => db.Open("source").Open(worktree);

    private DbNode ChunkRoot(string worktree) => SourceRoot(worktree).Open("chunk");

    private SourceChunk? ReadChunk(DbNode node)
    {
        if (node.ReadJson<ChunkRecord>() is not { } record) return null;
        RememberChunk(record.Id, node);
        var at = node.Location.Segments; // source, worktree, chunk, path, line.id
        return new SourceChunk(at[1], at[3], record.Hash, record.StartLine, record.Text, record.Id, record.Symbols);
    }

    private static long ChunkId(DbNode node) => long.Parse(node.Name[(node.Name.LastIndexOf('.') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    internal int SourceChunkVersion(string worktree) =>
        int.TryParse(SourceRoot(worktree).Open("version").Read(), out var version) ? version : 0;

    internal void SetSourceChunkVersion(string worktree, int version) =>
        SourceRoot(worktree).Open("version").Write(version.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Every indexed file of a worktree with the hash of the content it was indexed from.</summary>
    internal IReadOnlyDictionary<string, string> SourceHashes(string worktree)
    {
        var files = SourceRoot(worktree).Open("file");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in files.Children())
            if (files.Open(name).Read() is { } hash) hashes[name] = hash;
        return hashes;
    }

    /// <summary>Replaces what is indexed of one file; a null <paramref name="hash"/> forgets the file.</summary>
    internal void ReplaceSource(string worktree, string path, string? hash, IReadOnlyList<(int StartLine, string Text, string Symbols)> chunks)
    {
        var root = SourceRoot(worktree);
        var file = root.Open("file").Open(path);
        var chunkDirectory = root.Open("chunk").Open(path);
        db.Batch(() =>
        {
            chunkDirectory.Remove();
            file.Remove();
            if (hash is null) return;
            file.Write(hash);
            foreach (var (line, text, symbols) in chunks)
            {
                var id = NextId();
                var node = chunkDirectory.Open($"{line:D7}.{id}");
                node.WriteJson(new ChunkRecord(id, hash, line, text, symbols));
                node.Index(SourceField, SearchText.Tokens(path + " " + symbols + " " + text));
            }
        });
    }

    internal IReadOnlyList<SourceChunk> SearchSource(string query, string worktree, int limit = 12) =>
        SearchSourceMatch(SearchText.MatchQueryTerms(query), worktree, limit);

    /// <summary>Lexical leg of hybrid retrieval: filler-free, stemmed prefix terms ranked by BM25.</summary>
    internal IReadOnlyList<SourceChunk> SearchSourceTerms(string query, string worktree, int limit = 40) =>
        SearchSourceMatch(SearchText.RetrievalQueryTerms(query), worktree, limit);

    /// <summary>Find identifier prefixes such as CommitMessage in addition to their individual words.</summary>
    internal IReadOnlyList<SourceChunk> SearchSourceIdentifierPrefix(string identifier, string worktree, int limit = 20) =>
        identifier.Length == 0 || !identifier.All(char.IsLetterOrDigit) ? [] :
            SearchSourceMatch([(identifier.ToLowerInvariant(), true)], worktree, limit);

    private IReadOnlyList<SourceChunk> SearchSourceMatch(IReadOnlyList<(string Word, bool Prefix)> terms, string worktree, int limit)
    {
        if (terms.Count == 0) return [];
        return SourceRoot(worktree).Match(SourceField, Terms(terms), limit)
            .Select(hit => ReadChunk(hit.Node))
            .OfType<SourceChunk>()
            .ToList();
    }

    /// <summary>The chunks with these ids that are still indexed (an id from a search whose file has since been re-indexed is gone).</summary>
    internal IReadOnlyList<SourceChunk> SourceChunks(IReadOnlyCollection<long> ids) =>
        [.. ids.Select(id => _chunkNodes.TryGetValue(id, out var node) ? ReadChunk(node) : null).OfType<SourceChunk>()];

    internal IReadOnlyList<SourceChunk> SourceChunksForPath(string worktree, string path, int limit = 2)
    {
        var directory = ChunkRoot(worktree).Open(path);
        return [.. directory.Children().Take(limit).Select(name => ReadChunk(directory.Open(name))).OfType<SourceChunk>()];
    }

    // ---- vectors

    /// <summary>Gives every chunk of a worktree that lacks a current embedding one, a batch at a time so readers are held up briefly.</summary>
    private void EmbedChunks(string worktree)
    {
        var missing = ChunkRoot(worktree).Descendants().Where(node => !node.HasVector()).ToList();
        for (var start = 0; start < missing.Count && !_disposed; start += EmbedBatch)
        {
            var batch = missing.Skip(start).Take(EmbedBatch).ToList();
            db.Batch(() =>
            {
                foreach (var node in batch)
                {
                    if (node.ReadJson<ChunkRecord>() is { } chunk)
                        node.Embed(node.Location.Segments[3] + "\n" + chunk.Symbols + "\n" + chunk.Text);
                }
            });
        }
    }

    // ---- findings

    public void PutKnowledge(KnowledgeHit hit)
    {
        var node = db.Open("knowledge").Open(hit.Id);
        db.Batch(() =>
        {
            node.WriteJson(new KnowledgeRecord(hit.SessionId, hit.Provider, hit.Worktree, hit.Revision, hit.Text, hit.References, NextId()));
            node.Index(KnowledgeField, SearchText.Tokens(hit.Text));
        });
    }

    internal IReadOnlyList<KnowledgeHit> SearchKnowledge(string query, int limit = 8, string? excludeSessionId = null) =>
        SearchKnowledgeMatch(SearchText.MatchQueryTerms(query), limit, excludeSessionId);

    /// <summary>Findings for project retrieval: question filler dropped and stems prefix-matched, as for source.</summary>
    internal IReadOnlyList<KnowledgeHit> SearchKnowledgeTerms(string query, int limit = 3, string? excludeSessionId = null) =>
        SearchKnowledgeMatch(SearchText.RetrievalQueryTerms(query), limit, excludeSessionId);

    private IReadOnlyList<KnowledgeHit> SearchKnowledgeMatch(IReadOnlyList<(string Word, bool Prefix)> terms, int limit, string? excludeSessionId)
    {
        if (terms.Count == 0) return [];
        // The excluded session's findings are dropped after ranking, so ask for enough to still fill the limit.
        var ask = excludeSessionId is null ? limit : limit + 32;
        return db.Open("knowledge").Match(KnowledgeField, Terms(terms), ask)
            .Select(hit => (hit.Node, Record: hit.Node.ReadJson<KnowledgeRecord>()))
            .Where(x => x.Record is not null && x.Record.SessionId != excludeSessionId)
            .Take(limit)
            .Select(x => new KnowledgeHit(x.Node.Name, x.Record!.SessionId, x.Record.Provider, x.Record.Worktree, x.Record.Revision, x.Record.Text, x.Record.Refs))
            .ToList();
    }

    /// <summary>The files a session's findings cited, newest finding first.</summary>
    internal IReadOnlyList<SourceReference> SessionReferences(string sessionId) =>
        db.Open("knowledge").Descendants()
            .Select(node => node.ReadJson<KnowledgeRecord>())
            .Where(record => record is not null && record.SessionId == sessionId)
            .OrderByDescending(record => record!.Seq)
            .SelectMany(record => record!.Refs)
            .DistinctBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---- learned associations

    /// <summary>Associate an actual agent edit with the user request active during that turn.</summary>
    public void RecordLearnedEdit(string sessionId, string worktree, string path, string prompt) =>
        RecordLearned(sessionId, worktree, path, prompt, LearnedKind.Edit);

    /// <summary>
    /// Remembers that <paramref name="path"/> answered <paramref name="prompt"/>: the agent edited it, read it
    /// while working on it, or the user opened it from a search for it. A stronger kind replaces a weaker one
    /// recorded for the same file and prompt.
    /// </summary>
    public void RecordLearned(string sessionId, string worktree, string path, string prompt, string kind)
    {
        if (SearchText.RetrievalTerms(prompt).Count == 0) return;
        path = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var clipped = prompt.Length > 500 ? prompt[..500] : prompt;
        var bucket = db.Open("learned").Open(worktree);
        var node = bucket.Open(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "\n" + path + "\n" + clipped)))[..32]);
        db.Batch(() =>
        {
            if (node.ReadJson<LearnedRecord>() is { } existing)
            {
                if (LearnedKind.Strength(kind) > LearnedKind.Strength(existing.Kind)) node.WriteJson(existing with { Kind = kind });
                return;
            }
            node.WriteJson(new LearnedRecord(sessionId, path, clipped, kind, NextId()));
            node.Index(LearnedField, SearchText.Tokens(prompt));

            // Bounded memory: the oldest associations of a project go first, in slices so a full project does not rescan on every edit.
            if (bucket.ChildCount() <= MaxLearnedPerProject + 500) return;
            foreach (var old in bucket.Descendants()
                .Select(n => (Node: n, Record: n.ReadJson<LearnedRecord>()))
                .OrderByDescending(x => x.Record?.Seq ?? 0)
                .Skip(MaxLearnedPerProject))
                old.Node.Remove();
        });
    }

    internal IReadOnlyList<(string Path, string Prompt, string Kind)> SearchLearnedEdits(string worktree, string prompt)
    {
        var terms = SearchText.RetrievalQueryTerms(prompt);
        if (terms.Count == 0) return [];
        return db.Open("learned").Open(worktree).Match(LearnedField, Terms(terms), 80)
            .Select(hit => hit.Node.ReadJson<LearnedRecord>())
            .OfType<LearnedRecord>()
            .Select(record => (record.Path, record.Prompt, record.Kind))
            .ToList();
    }

    /// <summary>Every remembered association of a project, newest first: real usage, as labelled retrieval examples.</summary>
    internal IReadOnlyList<(string Path, string Prompt, string Kind)> LearnedAssociations(string worktree, int limit = 2000) =>
        db.Open("learned").Open(worktree).Descendants()
            .Select(node => node.ReadJson<LearnedRecord>())
            .OfType<LearnedRecord>()
            .OrderByDescending(record => record.Seq)
            .Take(limit)
            .Select(record => (record.Path, record.Prompt, record.Kind))
            .ToList();

    // ---- what each retrieval cost

    public void RecordRetrieval(string id, string sessionId, RetrievalResult result) =>
        db.Open("retrieval").Open(id).WriteJson(new RetrievalRecord(sessionId, result.ElapsedMilliseconds, result.Tokens, result.Keys, 0, 0, 0));

    public void RecordExploration(string retrievalId) =>
        UpdateRetrieval(retrievalId, record => record with { ExplorationCalls = record.ExplorationCalls + 1 });

    public void RecordUsage(string retrievalId, int inputTokens, int outputTokens) =>
        UpdateRetrieval(retrievalId, record => record with { InputTokens = record.InputTokens + inputTokens, OutputTokens = record.OutputTokens + outputTokens });

    private void UpdateRetrieval(string id, Func<RetrievalRecord, RetrievalRecord> change)
    {
        var node = db.Open("retrieval").Open(id);
        db.Batch(() =>
        {
            if (node.ReadJson<RetrievalRecord>() is { } record) node.WriteJson(change(record));
        });
    }
}
