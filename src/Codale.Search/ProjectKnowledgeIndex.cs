using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Codale.Core.Features;
using Codale.Core.Projects;
using Codale.Storage;

namespace Codale.Search;

/// <summary>Local project retrieval. Indexing is asynchronous; retrieval uses only committed rows and fresh source.</summary>
/// <remarks>
/// Ranking fuses BM25 with the local hashed n-gram vectors (identifier fragments and inflections). There is no
/// remote embedding model: every signal is computed in-process, so retrieval never waits on a server.
/// </remarks>
public sealed partial class ProjectKnowledgeIndex(FeatureStore store) : IDisposable
{
    // UTF-8 bytes are a conservative upper bound on byte-tokenizer tokens, including the briefing envelope.
    public const int MaxContextBytes = 3600;
    private const int MapBudget = 700;
    private const int FindingBudget = 1300;
    private const int MatchBudget = 700;
    private const int MaxLocated = 8;
    private const int MaxWindows = 2;
    private const int WindowLines = 24;
    private const int MaxFiles = 6;
    /// <summary>Within the caller's two-second retrieval budget, leaving room for the search itself.</summary>
    private static readonly TimeSpan StaleRefreshWait = TimeSpan.FromMilliseconds(900);
    /// <summary>Chunks embedded per database round, so the database lock is held briefly.</summary>
    private const int EmbedBatch = 32;
    private const double RrfK = 60;
    private const double DocWeight = 0.5;
    private const double SymbolBonus = 0.03;
    private static readonly string[] DocOrTestTerms = ["doc", "readme", "markdown", "md", "test", "spec", "changelog", "licen"];
    private static readonly System.Text.RegularExpressions.Regex LicensePattern = new(
        @"(^|[\\/])(licen[cs]e|copying|notice|ofl|third[-_]party[-_]notices?)(\.[a-z]+)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    /// <summary>Instruction files every CLI already loads into the conversation: repeating them is waste.</summary>
    private static readonly System.Text.RegularExpressions.Regex AgentInstructions = new(
        @"(^|[\\/])(CLAUDE|AGENTS|GEMINI)(\.local)?\.md$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    // Keyed by worktree: one packed int8 matrix of the hashed vectors per project.
    private readonly ConcurrentDictionary<string, QuantizedMatrix> _vectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _embedded = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cancelled on dispose, so a closing window never keeps indexing.</summary>
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _refreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SourceFile> _indexedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _revisions = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per worktree: files whose content changed while Codale watched, and when. Covers edits
    /// made through the shell (sed, scripts), which never arrive as a tool's file change.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _contentChanges = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>More changed files than this in one refresh is a checkout, pull or formatter run, not a session's edits.</summary>
    private const int MaxRecordedChanges = 40;
    /// <summary>The session's most recently written files that get a ranking boost, newest strongest.</summary>
    private const int MaxWorkingSet = 8;
    private const double WorkingSetBoost = 0.06;
    private const double WorkingSetDecay = 0.75;
    private bool _disposed;
    private int _indexing;
    public bool IsIndexing => Volatile.Read(ref _indexing) > 0;
    public event Action? IndexingChanged;

    public Task RefreshAsync(string worktree)
    {
        worktree = ProjectPaths.Normalize(worktree);
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_refreshes.TryGetValue(worktree, out var running) && !running.IsCompleted) return running;
            return _refreshes[worktree] = Task.Run(async () =>
            {
                Interlocked.Increment(ref _indexing);
                try
                {
                    IndexingChanged?.Invoke();
                    _stopping.Token.ThrowIfCancellationRequested();
                    var index = SourceIndex.For(worktree);
                    // HEAD changes and lost watcher events must not leave a branch's snapshot reusable forever.
                    var revision = Revision(worktree);
                    var watched = _revisions.TryGetValue(worktree, out var previousRevision);
                    index.Invalidate(!watched || previousRevision != revision);
                    _revisions[worktree] = revision;
                    var snapshot = await index.GetSnapshotAsync().ConfigureAwait(false);
                    var reChunk = store.SourceChunkVersion(worktree) != FileChunker.Version;
                    if (!reChunk && _snapshots.GetValueOrDefault(worktree) == snapshot.BuiltAt)
                    {
                        // An index built before vectors existed is embedded once, without re-chunking.
                        EmbedPending(worktree);
                        WarmSummaries(worktree);
                        return;
                    }
                    var old = store.SourceHashes(worktree);
                    var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var changed = new List<string>();
                    foreach (var file in snapshot.Files)
                    {
                        _stopping.Token.ThrowIfCancellationRequested();
                        if (!Eligible(file.RelativePath) || file.Length > FileChunker.MaxFileBytes) continue;
                        var identity = worktree + "\n" + file.RelativePath;
                        present.Add(file.RelativePath);
                        if (!reChunk && _indexedFiles.TryGetValue(identity, out var previous) && ReferenceEquals(previous, file) && old.ContainsKey(file.RelativePath)) continue;
                        // Re-read disk: a retained snapshot may predate a branch switch.
                        var text = ReadSource(worktree, file.RelativePath);
                        if (text is null) { present.Remove(file.RelativePath); continue; }
                        var hash = Hash(text);
                        if (reChunk || old.GetValueOrDefault(file.RelativePath) != hash)
                        {
                            store.ReplaceSource(worktree, file.RelativePath, hash, FileChunker.Chunk(text, file.RelativePath));
                            if (!reChunk && old.Count > 0) changed.Add(file.RelativePath);
                        }
                        _indexedFiles[identity] = file;
                    }
                    var removedPaths = old.Keys.Where(p => !present.Contains(p)).ToArray();
                    foreach (var removed in removedPaths) store.ReplaceSource(worktree, removed, null, []);
                    // The first refresh in this process compares against an index from an earlier run, and a
                    // branch switch rewrites whatever differs: neither is work done in a session.
                    if (watched && SameBranch(previousRevision!, revision) && changed.Count is > 0 and <= MaxRecordedChanges)
                    {
                        var now = DateTime.UtcNow;
                        var log = _contentChanges.GetOrAdd(worktree, _ => new(StringComparer.OrdinalIgnoreCase));
                        foreach (var path in changed) log[path] = now;
                    }
                    store.SetSourceChunkVersion(worktree, FileChunker.Version);
                    if (reChunk || changed.Count > 0 || removedPaths.Length > 0 || old.Count == 0)
                        _embedded.TryRemove(worktree, out _);
                    EmbedPending(worktree);
                    lock (_gate) _snapshots[worktree] = snapshot.BuiltAt;
                    WarmSummaries(worktree);
                }
                finally
                {
                    Interlocked.Decrement(ref _indexing);
                    IndexingChanged?.Invoke();
                }
            });
        }
    }

    public void Warm(string worktree)
    {
        _ = RefreshAsync(worktree).ContinueWith(t => System.Diagnostics.Trace.TraceWarning($"Project indexing failed: {t.Exception}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>Embeds chunks that have no hashed vector yet, in batches so the database lock is held briefly.</summary>
    private void EmbedPending(string worktree)
    {
        var model = HashedTextEmbedder.Instance;
        if (_embedded.ContainsKey(worktree)) return;
        while (!_disposed)
        {
            var pending = store.ChunksMissingVectors(worktree, model.Model, EmbedBatch);
            if (pending.Count == 0) break;
            store.PutVectors(model.Model, pending.Select(c => (c.Id, model.Embed(c.Path + "\n" + c.Symbols + "\n" + c.Text))).ToList());
            _vectors.TryRemove(worktree, out _);
        }
        _vectors.TryRemove(worktree, out _);
        if (_disposed) return;
        _embedded[worktree] = true;
        // Vectors of any retired embedding model are dead weight.
        store.DeleteVectorsExcept([model.Model]);
    }

    private QuantizedMatrix Vectors(string worktree) =>
        _vectors.GetOrAdd(worktree, _ => store.SourceVectors(worktree, HashedTextEmbedder.Instance.Model));

    /// <summary>Shared hybrid retrieval for discovery and prompt enrichment; only fresh source is returned.</summary>
    public IReadOnlyList<SourceChunk> Search(string worktree, string prompt, string recent = "", int maxFiles = 12, int maxChunksPerFile = 1) =>
        Search(worktree, prompt, recent, maxFiles, maxChunksPerFile, null, out _);

    /// <summary><see cref="Search(string, string, string, int, int)"/> off the caller's thread.</summary>
    public Task<IReadOnlyList<SourceChunk>> SearchAsync(string worktree, string prompt, string recent = "", int maxFiles = 12, int maxChunksPerFile = 1,
        CancellationToken ct = default) =>
        Task.Run(() => Search(worktree, prompt, recent, maxFiles, maxChunksPerFile), ct);

    /// <param name="workingSet">Files the current session wrote, newest first; boosted with decay.</param>
    /// <param name="stale">True when a candidate was dropped because its file changed after indexing.</param>
    private IReadOnlyList<SourceChunk> Search(string worktree, string prompt, string recent, int maxFiles, int maxChunksPerFile,
        IReadOnlyList<string>? workingSet, out bool stale)
    {
        worktree = ProjectPaths.Normalize(worktree);
        Warm(worktree);
        // 1. Hybrid ranking: BM25 over stemmed prefix terms and cosine over chunk vectors, fused by
        //    reciprocal rank. The latest prompt leads; the recent conversation only tops up.
        var scores = new Dictionary<long, double>();
        void Fuse(IEnumerable<long> ranked, double weight)
        {
            var rank = 0;
            foreach (var id in ranked) scores[id] = scores.GetValueOrDefault(id) + weight / (RrfK + ++rank);
        }
        var lexical = store.SearchSourceTerms(prompt, worktree).Select(c => c.Id).ToList();
        var lexicalIds = lexical.ToHashSet();
        Fuse(lexical, 1.0);
        var terms = SearchText.RetrievalTerms(prompt);

        if (terms.Count > 0)
        {
            // Embed the topic, not the phrasing: "where does ... located" must not pull in prose that says "located".
            var topic = string.Join(' ', terms);
            var embedder = HashedTextEmbedder.Instance;
            var vectors = Vectors(worktree);
            if (vectors.Count > 0)
            {
                var similar = vectors.Top(embedder.Embed(topic), 40);
                // Relative cut: keep near-best neighbours only, so unrelated files never ride in on noise.
                // Relative similarity alone always supplies neighbours, even for an absent topic.
                // Lexical evidence can support weaker matches; otherwise require confidence.
                var minimum = embedder.MatchSimilarity;
                var floor = similar.Count > 0 ? Math.Max(0.08f, similar[0].Score * 0.5f) : 1f;
                // N-grams add fragments and inflections beside the exact-token ranking, at lower weight.
                Fuse(similar.Where(v => v.Score >= floor && (lexicalIds.Contains(v.Id) || v.Score >= minimum)).Select(v => v.Id), 0.6);
            }
        }
        if (!string.IsNullOrWhiteSpace(recent)) Fuse(store.SearchSourceTerms(recent, worktree, 20).Select(c => c.Id), 0.35);

        // What showed a file answered a similar earlier request is direct evidence for this one: an edit the agent
        // completed, a result the user opened from a search, a file the agent had to read. Reuse the association even
        // when the current source lacks the prompt's words; the kind sets how far it lifts the file.
        var learned = store.SearchLearnedEdits(worktree, prompt)
            .Select(hit => (hit.Path, hit.Kind, Terms: SearchText.RetrievalTerms(hit.Prompt)))
            .Where(hit =>
            {
                var overlap = terms.Count(t => hit.Terms.Contains(t));
                return overlap >= (terms.Count <= 2 ? terms.Count : 2) && overlap * 2 >= terms.Count;
            })
            .GroupBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase).Take(8);
        foreach (var file in learned)
        {
            var boost = file.Max(hit => LearnedKind.Boost(hit.Kind));
            foreach (var chunk in store.SourceChunksForPath(worktree, file.Key))
                scores[chunk.Id] = scores.GetValueOrDefault(chunk.Id) + boost;
        }

        // The session's working set: a follow-up ("highland villages take 20 more minutes") rarely repeats
        // the words of the request that edited the file, so neither the source nor a learned edit matches.
        // Rank by recency; the newest file is boosted most, and every file still has to clear the floor.
        var working = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in workingSet ?? [])
        {
            var key = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (working.Count == MaxWorkingSet) break;
            if (AgentInstructions.IsMatch(key) || !working.TryAdd(key, working.Count)) continue;
            foreach (var chunk in store.SourceChunksForPath(worktree, key)) scores.TryAdd(chunk.Id, 0);
        }

        var chunks = store.SourceChunks(scores.Keys.ToList()).ToDictionary(c => c.Id);
        // A specific word such as "minimap" should outweigh generic matches for "game" or
        // "unit". Count candidate files, not overlapping chunks, so long files get no bonus.
        var chunkTerms = chunks.Values.ToDictionary(c => c.Id,
            c => SearchText.Tokens(c.Path + "\n" + c.Text).Select(SearchText.Stem).ToHashSet());
        var fileCount = chunks.Values.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        bool Matches(long id, string term) => chunkTerms[id].Any(t => t.StartsWith(term, StringComparison.Ordinal));
        var specificity = terms.ToDictionary(t => t, t =>
        {
            var matches = chunks.Values.Where(c => Matches(c.Id, t)).Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return Math.Log(1.0 + (fileCount - matches + 0.5) / (matches + 0.5));
        });
        // A path naming the topic adds a locator bonus, weighted by the same specificity so
        // broad folder names such as "game" cannot outweigh a distinctive implementation term.
        foreach (var chunk in chunks.Values)
        {
            scores[chunk.Id] += 0.02 * terms.Where(t => Matches(chunk.Id, t)).Sum(t => specificity[t]);
            var pathStems = SearchText.Tokens(chunk.Path).Select(SearchText.Stem).ToList();
            scores[chunk.Id] += 0.02 * terms.Where(t => t.Length >= 3 && pathStems.Any(p => p.StartsWith(t, StringComparison.Ordinal)))
                .Sum(t => specificity[t]);
            // The symbols a chunk declares name what it is about: a prompt word inside one is stronger evidence
            // than the same word somewhere in the body.
            if (chunk.Symbols.Length > 0)
            {
                var symbolStems = SearchText.Tokens(chunk.Symbols).Select(SearchText.Stem).ToList();
                scores[chunk.Id] += SymbolBonus * terms.Where(t => t.Length >= 3 && symbolStems.Any(p => p.StartsWith(t, StringComparison.Ordinal)))
                    .Sum(t => specificity[t]);
            }
            if (working.TryGetValue(chunk.Path, out var recency))
                scores[chunk.Id] += WorkingSetBoost * Math.Pow(WorkingSetDecay, recency);
        }

        // Design notes and tests mention every subject in prose, so they outrank the code that
        // implements it; they lead only when the prompt asks for them ("update the docs", "the test").
        var wantsDocs = terms.Any(t => DocOrTestTerms.Any(d => t.StartsWith(d, StringComparison.Ordinal)));
        if (!wantsDocs)
            foreach (var chunk in chunks.Values)
                if (CodeDiscovery.IsTestOrDoc(chunk.Path) || LicensePattern.IsMatch(chunk.Path)) scores[chunk.Id] *= DocWeight;

        var fresh = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var floorScore = scores.Count == 0 ? 0 : scores.Values.Max() * 0.34;
        var result = scores.Where(s => s.Value >= floorScore).OrderByDescending(s => s.Value).Where(s => chunks.ContainsKey(s.Key))
            .Select(s => chunks[s.Key]).Where(c =>
            {
                if (!fresh.TryGetValue(c.Path, out var valid))
                    fresh[c.Path] = valid = ReadSource(worktree, c.Path) is { } content && Hash(content) == c.Hash;
                return valid;
            }).GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Take(Math.Max(0, maxFiles))
            .SelectMany(g => g.Take(Math.Max(0, maxChunksPerFile))).ToList();
        stale = fresh.ContainsValue(false);
        return result;
    }

    /// <summary>
    /// Builds the context a prompt is enriched with, within <see cref="MaxContextBytes"/>:
    /// a file map (paths and matching definitions), earlier findings whose cited files are unchanged,
    /// and hash-verified source excerpts. Anything in <paramref name="delivered"/> is already in the
    /// provider's conversation and is not repeated.
    /// </summary>
    /// <param name="workingSet">Files the current session wrote, newest first; empty on a new topic.</param>
    public RetrievalResult Retrieve(string worktree, string prompt, string recent, IReadOnlySet<string> delivered, string? currentSessionId = null,
        IReadOnlyList<string>? workingSet = null, IReadOnlyList<string>? preferredChunkIds = null)
    {
        var timer = Stopwatch.StartNew();
        worktree = ProjectPaths.Normalize(worktree);
        Warm(worktree);
        var text = new StringBuilder();
        var keys = new List<string>();
        var bytes = 0;
        bool Add(IReadOnlyList<string> blockKeys, string block, int limit)
        {
            if (blockKeys.All(k => delivered.Contains(k) || keys.Contains(k))) return false;
            var rendered = block + Environment.NewLine + Environment.NewLine;
            var size = Encoding.UTF8.GetByteCount(rendered);
            if (bytes + size > Math.Min(limit, MaxContextBytes)) return false;
            text.Append(rendered);
            bytes += size;
            keys.AddRange(blockKeys);
            return true;
        }

        var terms = SearchText.RetrievalTerms(prompt);
        var legs = new List<string> { "BM25", "n-gram vectors" };
        var ranked = Search(worktree, prompt, recent, MaxFiles + 2, 3, workingSet, out var stale);
        // Mid-turn the agent is editing the very files a steer is about. Their rows are stale until the
        // index catches up, and stale files are dropped, so wait briefly for that rather than lose them.
        if ((stale || ranked.Count == 0) && CaughtUp(worktree))
            ranked = Search(worktree, prompt, recent, MaxFiles + 2, 3, workingSet, out _);
        if (preferredChunkIds is not null)
        {
            var order = preferredChunkIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            ranked = ranked.OrderBy(c => order.GetValueOrDefault(c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), int.MaxValue)).ToList();
        }
        // The CLI already loaded its instruction files into the conversation.
        var topFiles = ranked.Where(c => !AgentInstructions.IsMatch(c.Path))
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList();
        string? Fresh(string path, string hash) => ReadSource(worktree, path) is { } content && Hash(content) == hash ? content : null;

        // 2b. Names the prompt spells out ("MaxFiles", "RankAsync") are exact: find their lines in the ranked files, so the
        //     agent is handed the very line it would otherwise grep for, and the code around it.
        var identifiers = Identifiers(prompt);
        var located = new List<(string Path, string Hash, int Line, string Text)>();
        var windows = new List<(string Path, string Hash, int Start, int End, string Text)>();
        if (identifiers.Count > 0)
        {
            foreach (var file in topFiles)
            {
                var first = file.First();
                if (Fresh(first.Path, first.Hash) is not { } source) continue;
                var lines = source.ReplaceLineEndings("\n").Split('\n');
                foreach (var name in identifiers)
                {
                    var at = LineOf(lines, name);
                    if (at < 0 || located.Count >= MaxLocated) continue;
                    located.Add((first.Path.Replace('\\', '/'), first.Hash, at + 1, Clip(lines[at].Trim(), 140)));
                    // A file whose content is already in the conversation needs no second excerpt, only its new line number.
                    var sent = delivered.Any(k => k.StartsWith($"source:{first.Path}:{first.Hash}:", StringComparison.Ordinal));
                    if (!sent && windows.Count < MaxWindows && !windows.Any(w => w.Path == first.Path && at + 1 >= w.Start && at + 1 <= w.End))
                    {
                        var start = Math.Max(0, at - 2);
                        var end = Math.Min(lines.Length - 1, at + WindowLines - 3);
                        windows.Add((first.Path, first.Hash, start + 1, end + 1, string.Join("\n", lines[start..(end + 1)])));
                    }
                }
            }
        }

        // 3. The file map: often enough on its own to answer "where is X?", and always enough to open
        //    the right file directly instead of searching the workspace for it.
        var map = new StringBuilder("Where (path — definitions):\n");
        var mapKeys = new List<string>();
        var files = new List<string>();
        foreach (var file in topFiles)
        {
            var first = file.First();
            var key = $"map:{first.Path}:{first.Hash}";
            var path = first.Path.Replace('\\', '/');
            var symbols = Definitions(Fresh(first.Path, first.Hash)!, terms);
            // Definitions depend on the prompt: another topic in an unchanged file needs its
            // own locator, even though previously delivered source excerpts stay deduplicated.
            key += ":" + Hash(string.Join(", ", symbols));
            if (delivered.Contains(key)) continue;
            var entry = "- " + path + (symbols.Count > 0 ? " — " + string.Join(", ", symbols) : "") + "\n";
            // Keep the highest-ranked entries that fit, rather than losing the entire map.
            if (Encoding.UTF8.GetByteCount(map.ToString() + entry + Environment.NewLine + Environment.NewLine) > MapBudget) continue;
            map.Append(entry);
            mapKeys.Add(key);
            files.Add(path);
        }
        if (!(mapKeys.Count > 0 && Add(mapKeys, map.ToString().TrimEnd(), MapBudget))) files.Clear();
        if (located.Count > 0)
        {
            var matches = "Matches (path:line):\n" + string.Join("\n", located.Select(m => $"- {m.Path}:{m.Line}  {m.Text}"));
            Add(["match:" + Hash(matches)], matches, MatchBudget);
        }

        // 4. Earlier findings from either provider, valid only while the files they cite are unchanged.
        var findings = 0;
        var findingLimit = bytes + FindingBudget;
        var hits = store.SearchKnowledgeTerms(prompt, 3, currentSessionId);
        if (!string.IsNullOrWhiteSpace(recent)) hits = [.. hits, .. store.SearchKnowledgeTerms(recent, 2, currentSessionId)];
        foreach (var hit in hits)
        {
            // Findings are long prose: one shared word ("secret" in a database URL) is not relevance. Require
            // most of the prompt's topic terms, so a finding is only spent on when it is about this question.
            var findingStems = SearchText.Tokens(hit.Text).Select(SearchText.Stem).ToHashSet();
            var covered = terms.Count(t => findingStems.Any(s => s.StartsWith(t, StringComparison.Ordinal)));
            if (terms.Count > 1 && covered * 2 < terms.Count + 1) continue;
            if (hit.References.Any(r => ReadSource(worktree, r.Path) is not { } content || Hash(content) != r.Hash)) continue;
            var refs = string.Join(", ", hit.References.Select(r => r.Path.Replace('\\', '/')));
            if (Add(["knowledge:" + hit.Id + ":" + Hash(hit.Text)],
                    $"Facts from earlier work (provider {hit.Provider}, session {hit.SessionId}; cites {(refs.Length > 0 ? refs : "no files - verify before relying on it")}):\n{Clip(Compact(hit.Text), 900)}",
                    findingLimit))
                findings++;
        }

        // 5. Source excerpts: the code around the named lines first, then the best-ranked chunks, at most two
        //    per file so one file cannot crowd out the rest. Chunks overlapping a window add nothing.
        var snippets = 0;
        var header = false;
        bool Excerpt(string rawPath, string hash, int start, int end, string code, int? keyStart = null)
        {
            var path = rawPath.Replace('\\', '/');
            // One label for all excerpts: they are already read, so the agent edits from them (read ranges only if its tools demand it).
            var label = header ? "" : "Already read (current):\n";
            if (!Add([$"source:{rawPath}:{hash}:{keyStart ?? start}"], $"{label}{path}:{start}-{end}\n```\n{code}\n```", MaxContextBytes)) return false;
            snippets++;
            header = true;
            return true;
        }
        // A window keeps the key of the ranked chunk it overlaps, so what the conversation already holds is still recognised.
        foreach (var w in windows)
        {
            var overlapped = topFiles.SelectMany(f => f).Where(c => c.Path == w.Path)
                .FirstOrDefault(c => c.StartLine <= w.End && w.Start <= c.StartLine + c.Text.Count(ch => ch == '\n'));
            Excerpt(w.Path, w.Hash, w.Start, w.End, w.Text, overlapped?.StartLine);
        }
        // The best file gets both its chunks first (the answer is usually one more chunk of the file that ranked first);
        // then every other file's best chunk, then their seconds.
        foreach (var chunk in topFiles.SelectMany((f, rank) => f.Take(rank == 0 ? 3 : 2).Select((c, i) => (c, order: rank == 0 ? i : i == 0 ? 1 + rank : 1 + MaxFiles + rank)))
                     .OrderBy(x => x.order).Select(x => x.c))
        {
            var endLine = chunk.StartLine + chunk.Text.Count(c => c == '\n');
            if (windows.Any(w => w.Path == chunk.Path && w.Start <= endLine && chunk.StartLine <= w.End)) continue;
            Excerpt(chunk.Path, chunk.Hash, chunk.StartLine, endLine, chunk.Text);
        }
        return new RetrievalResult(text.ToString().TrimEnd(), keys, bytes, timer.ElapsedMilliseconds)
        {
            Files = files,
            Snippets = snippets,
            Findings = findings,
            Method = string.Join(" + ", legs),
        };
    }


    private static readonly System.Text.RegularExpressions.Regex IdentifierPattern = new(@"[A-Za-z_][A-Za-z0-9_]{3,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Code-like names in a prompt (camelCase, PascalCase with an inner capital, snake_case), in order, at most four.</summary>
    public static IReadOnlyList<string> Identifiers(string prompt) =>
        IdentifierPattern.Matches(prompt).Select(m => m.Value)
            .Where(v => v.Contains('_') || (v.Any(char.IsLower) && v.Skip(1).Any(char.IsUpper)))
            .Distinct(StringComparer.Ordinal).Take(4).ToList();

    /// <summary>The line that defines <paramref name="name"/> if there is one, else the first that mentions it as a whole word; -1 when absent.</summary>
    private static int LineOf(string[] lines, string name)
    {
        var word = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_])" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?![A-Za-z0-9_])");
        var mention = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!word.IsMatch(lines[i])) continue;
            if (mention < 0) mention = i;
            if (SymbolScanner.Match(lines[i]) is { } declaration && declaration.Name == name) return i;
            if (lines[i].Contains(name + " =", StringComparison.Ordinal) || lines[i].Contains(name + "(", StringComparison.Ordinal) && lines[i].Contains('{')) return i;
        }
        return mention;
    }

    private bool CaughtUp(string worktree)
    {
        try { return RefreshAsync(worktree).Wait(StaleRefreshWait); }
        catch (AggregateException ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Project indexing failed: {ex.InnerException?.Message}");
            return false;
        }
    }

    /// <summary>Blank-line runs collapsed and trailing spaces dropped: whitespace is tokens too.</summary>
    private static string Compact(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.ReplaceLineEndings("\n"), @"[ \t]*\n(?:[ \t]*\n)+", "\n").Trim();

    private static string Clip(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "\n…";

    /// <summary>Definitions in a file, those whose names match the prompt's terms first, as "Name (L12)".</summary>
    public static IReadOnlyList<string> Definitions(string content, IReadOnlyList<string> terms)
    {
        var found = new List<(string Name, int Line, bool Match)>();
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length && found.Count < 300; i++)
        {
            if (SymbolScanner.Match(lines[i]) is not { } declaration) continue;
            var stems = SearchText.Tokens(declaration.Name).Select(SearchText.Stem).ToList();
            found.Add((declaration.Name, i + 1, terms.Any(t => t.Length >= 3 && stems.Any(s => s.StartsWith(t, StringComparison.Ordinal)))));
        }
        return found.OrderByDescending(f => f.Match).ThenBy(f => f.Line).Take(4)
            .OrderBy(f => f.Line).Select(f => $"{f.Name} (L{f.Line})").ToList();
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Project-relative files whose content changed at or after <paramref name="sinceUtc"/>, newest first.</summary>
    public IReadOnlyList<string> ChangedSince(string worktree, DateTime sinceUtc) =>
        _contentChanges.TryGetValue(ProjectPaths.Normalize(worktree), out var log)
            ? log.Where(e => e.Value >= sinceUtc).OrderByDescending(e => e.Value).Select(e => e.Key).ToList()
            : [];

    /// <summary>
    /// Same checked-out branch: a commit moves the branch's head without rewriting the tree, so the
    /// files changed around it are still the session's own edits. A switch or detached head is not.
    /// </summary>
    internal static bool SameBranch(string previous, string current)
    {
        if (previous == current) return true;
        static string? Branch(string revision) =>
            revision.StartsWith("ref: ", StringComparison.Ordinal) && revision.LastIndexOf(' ') is var space and > 4
                ? revision[..space] : null;
        return Branch(previous) is { } branch && branch == Branch(current);
    }

    public static string Revision(string root)
    {
        try
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                var git = Path.Combine(directory.FullName, ".git");
                if (File.Exists(git)) git = Path.GetFullPath(File.ReadAllText(git).Trim()[7..].Trim(), directory.FullName);
                if (!Directory.Exists(git)) continue;
                var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
                if (!head.StartsWith("ref: ")) return head;
                var common = ProjectPaths.RepositoryIdentity(root);
                var reference = Path.Combine(common, head[5..]);
                var packed = Path.Combine(common, "packed-refs");
                var commit = File.Exists(reference) ? File.ReadAllText(reference).Trim() : File.Exists(packed)
                    ? File.ReadLines(packed).FirstOrDefault(line => line.EndsWith(" " + head[5..], StringComparison.Ordinal)) ?? "unknown" : "unknown";
                return head + " " + commit;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return "unknown";
    }

    public static bool Eligible(string path) => !SensitivePaths.IsSensitive(path) && !CodeDiscovery.IsNoisePath(path) &&
        !CodeDiscovery.IsNoisePath(Path.DirectorySeparatorChar + path) &&
        !new[] { ".db", ".sqlite", ".sqlite3", ".db-wal", ".db-shm" }.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public static string? ReadSource(string root, string relative)
    {
        try
        {
            if (Path.IsPathRooted(relative) || !Eligible(relative)) return null;
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!ProjectPaths.IsInside(root, path)) return null;
            // Do not follow source links into files outside the checkout.
            for (var entry = new FileInfo(path) as FileSystemInfo; entry is not null && ProjectPaths.IsInside(root, entry.FullName);
                 entry = entry is FileInfo f ? f.Directory : (entry as DirectoryInfo)?.Parent)
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > FileChunker.MaxFileBytes) return null;
            var content = File.ReadAllText(path);
            return FileChunker.IsIndexable(content) ? content : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public void Dispose()
    {
        Task[] running;
        lock (_gate) { _disposed = true; running = _refreshes.Values.Concat(_summaryTasks.Values).ToArray(); }
        _stopping.Cancel();
        try { Task.WhenAll(running).GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Project indexing stopped: {ex.Message}"); }
    }
}
