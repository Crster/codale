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
/// <paramref name="semantic"/> supplies the semantic model once it is loaded (null until then, and in tests);
/// the hashed embedder always runs beside it for identifier fragments and inflections.
/// </remarks>
public sealed class ProjectKnowledgeIndex(FeatureStore store, Func<ITextEmbedder?>? semantic = null) : IDisposable
{
    // UTF-8 bytes are a conservative upper bound on byte-tokenizer tokens, including the briefing envelope.
    public const int MaxContextBytes = 3600;
    private const int MapBudget = 700;
    private const int FindingBudget = 1300;
    private const int MaxFiles = 6;
    /// <summary>Within the caller's two-second retrieval budget, leaving room for the search itself.</summary>
    private static readonly TimeSpan StaleRefreshWait = TimeSpan.FromMilliseconds(900);
    private const double RrfK = 60;
    private const double DocWeight = 0.5;
    private static readonly string[] DocOrTestTerms = ["doc", "readme", "markdown", "md", "test", "spec", "changelog", "licen"];
    private static readonly System.Text.RegularExpressions.Regex LicensePattern = new(
        @"(^|[\\/])(licen[cs]e|copying|notice|ofl|third[-_]party[-_]notices?)(\.[a-z]+)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    /// <summary>Instruction files every CLI already loads into the conversation: repeating them is waste.</summary>
    private static readonly System.Text.RegularExpressions.Regex AgentInstructions = new(
        @"(^|[\\/])(CLAUDE|AGENTS|GEMINI)(\.local)?\.md$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    // Keyed by worktree + "\n" + model: one packed int8 matrix per pair.
    private readonly ConcurrentDictionary<string, QuantizedMatrix> _vectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _embedded = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _refreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SourceFile> _indexedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _revisions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public Task RefreshAsync(string worktree)
    {
        worktree = ProjectPaths.Normalize(worktree);
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_refreshes.TryGetValue(worktree, out var running) && !running.IsCompleted) return running;
            return _refreshes[worktree] = Task.Run(async () =>
            {
                var index = SourceIndex.For(worktree);
                // HEAD changes and lost watcher events must not leave a branch's snapshot reusable forever.
                var revision = Revision(worktree);
                index.Invalidate(!_revisions.TryGetValue(worktree, out var previousRevision) || previousRevision != revision);
                _revisions[worktree] = revision;
                var snapshot = await index.GetSnapshotAsync().ConfigureAwait(false);
                var reChunk = store.SourceChunkVersion(worktree) != FileChunker.Version;
                if (!reChunk && _snapshots.GetValueOrDefault(worktree) == snapshot.BuiltAt)
                {
                    // A model that became ready after the last refresh (or an index built before vectors
                    // existed) is embedded once, without re-chunking.
                    EmbedPending(worktree);
                    return;
                }
                var old = store.SourceHashes(worktree);
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in snapshot.Files)
                {
                    if (!Eligible(file.RelativePath) || file.Length > FileChunker.MaxFileBytes) continue;
                    var identity = worktree + "\n" + file.RelativePath;
                    present.Add(file.RelativePath);
                    if (!reChunk && _indexedFiles.TryGetValue(identity, out var previous) && ReferenceEquals(previous, file) && old.ContainsKey(file.RelativePath)) continue;
                    // Re-read disk: a retained snapshot may predate a branch switch.
                    var text = ReadSource(worktree, file.RelativePath);
                    if (text is null) { present.Remove(file.RelativePath); continue; }
                    var hash = Hash(text);
                    if (reChunk || old.GetValueOrDefault(file.RelativePath) != hash)
                        store.ReplaceSource(worktree, file.RelativePath, hash, FileChunker.Chunk(text));
                    _indexedFiles[identity] = file;
                }
                foreach (var removed in old.Keys.Where(p => !present.Contains(p))) store.ReplaceSource(worktree, removed, null, []);
                store.SetSourceChunkVersion(worktree, FileChunker.Version);
                foreach (var embedder in Embedders()) _embedded.TryRemove(worktree + "\n" + embedder.Model, out _);
                EmbedPending(worktree);
                lock (_gate) _snapshots[worktree] = snapshot.BuiltAt;
            });
        }
    }

    public void Warm(string worktree)
    {
        _ = RefreshAsync(worktree).ContinueWith(t => System.Diagnostics.Trace.TraceWarning($"Project indexing failed: {t.Exception}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>Embeds chunks that have no vector from the current model yet, in batches so the database lock is held briefly.</summary>
    private void EmbedPending(string worktree)
    {
        var embedders = Embedders();
        foreach (var embedder in embedders)
        {
            var key = worktree + "\n" + embedder.Model;
            if (_embedded.ContainsKey(key)) continue;
            while (!_disposed)
            {
                var pending = store.ChunksMissingVectors(worktree, embedder.Model, 256);
                if (pending.Count == 0) break;
                store.PutVectors(embedder.Model, pending.Select(c => (c.Id, embedder.Embed(c.Path + "\n" + c.Text))).ToList());
                _vectors.TryRemove(key, out _);
            }
            _vectors.TryRemove(key, out _);
            _embedded[key] = true;
        }
        // Once the semantic model is in use, vectors of retired models are dead weight.
        if (embedders.Count > 1) store.DeleteVectorsExcept(embedders.Select(e => e.Model).ToList());
    }

    private IReadOnlyList<ITextEmbedder> Embedders() =>
        semantic?.Invoke() is { } model ? [model, HashedTextEmbedder.Instance] : [HashedTextEmbedder.Instance];

    private QuantizedMatrix Vectors(string worktree, ITextEmbedder embedder) =>
        _vectors.GetOrAdd(worktree + "\n" + embedder.Model, _ => store.SourceVectors(worktree, embedder.Model));

    /// <summary>Shared hybrid retrieval for discovery and prompt enrichment; only fresh source is returned.</summary>
    public IReadOnlyList<SourceChunk> Search(string worktree, string prompt, string recent = "", int maxFiles = 12, int maxChunksPerFile = 1) =>
        Search(worktree, prompt, recent, maxFiles, maxChunksPerFile, out _);

    /// <param name="stale">True when a candidate was dropped because its file changed after indexing.</param>
    private IReadOnlyList<SourceChunk> Search(string worktree, string prompt, string recent, int maxFiles, int maxChunksPerFile, out bool stale)
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
            foreach (var embedder in Embedders())
            {
                var vectors = Vectors(worktree, embedder);
                if (vectors.Count == 0) continue;
                var similar = vectors.Top(embedder.Embed(topic), 40);
                // Relative cut: keep near-best neighbours only, so unrelated files never ride in on noise.
                // Relative similarity alone always supplies neighbours, even for an absent topic.
                // Lexical evidence can support weaker semantic matches; otherwise require confidence.
                var minimum = embedder is HashedTextEmbedder ? 0.25f : 0.4f;
                var floor = similar.Count > 0 ? Math.Max(0.08f, similar[0].Score * 0.5f) : 1f;
                // The semantic model leads; the hashed one adds fragments and inflections at lower weight.
                Fuse(similar.Where(v => v.Score >= floor && (lexicalIds.Contains(v.Id) || v.Score >= minimum)).Select(v => v.Id),
                    embedder is HashedTextEmbedder ? 0.6 : 1.0);

            }
        }
        if (!string.IsNullOrWhiteSpace(recent)) Fuse(store.SearchSourceTerms(recent, worktree, 20).Select(c => c.Id), 0.35);

        // A completed edit is direct evidence that its file answered that turn's request. Reuse
        // the association for a similar later request, even when the current source lacks its words.
        var learned = store.SearchLearnedEdits(worktree, prompt)
            .Select(hit => (hit.Path, Terms: SearchText.RetrievalTerms(hit.Prompt)))
            .Where(hit =>
            {
                var overlap = terms.Count(t => hit.Terms.Contains(t));
                return overlap >= (terms.Count <= 2 ? terms.Count : 2) && overlap * 2 >= terms.Count;
            })
            .GroupBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase).Take(8);
        foreach (var file in learned)
        {
            foreach (var chunk in store.SourceChunksForPath(worktree, file.Key))
                scores[chunk.Id] = scores.GetValueOrDefault(chunk.Id) + 0.12;
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
    public RetrievalResult Retrieve(string worktree, string prompt, string recent, IReadOnlySet<string> delivered, string? currentSessionId = null)
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
        var legs = new List<string> { "BM25", semantic?.Invoke() is null ? "n-gram vectors" : "semantic vectors" };
        var ranked = Search(worktree, prompt, recent, MaxFiles + 2, 2, out var stale);
        // Mid-turn the agent is editing the very files a steer is about. Their rows are stale until the
        // index catches up, and stale files are dropped, so wait briefly for that rather than lose them.
        if ((stale || ranked.Count == 0) && CaughtUp(worktree))
            ranked = Search(worktree, prompt, recent, MaxFiles + 2, 2, out _);
        // The CLI already loaded its instruction files into the conversation.
        var topFiles = ranked.Where(c => !AgentInstructions.IsMatch(c.Path))
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList();
        string? Fresh(string path, string hash) => ReadSource(worktree, path) is { } content && Hash(content) == hash ? content : null;

        // 3. The file map: often enough on its own to answer "where is X?", and always enough to open
        //    the right file directly instead of searching the workspace for it.
        var map = new StringBuilder("Relevant files (path — matching definitions):\n");
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
                    $"Earlier finding (provider {hit.Provider}, session {hit.SessionId}; cites {(refs.Length > 0 ? refs : "no files - verify before relying on it")}):\n{Clip(Compact(hit.Text), 900)}",
                    findingLimit))
                findings++;
        }

        // 5. Source excerpts, best file first, at most two per file so one file cannot crowd out the rest.
        var snippets = 0;
        foreach (var chunk in topFiles.SelectMany(f => f.Take(2).Select((c, i) => (c, i))).OrderBy(x => x.i).Select(x => x.c))
        {
            var endLine = chunk.StartLine + chunk.Text.Count(c => c == '\n');
            if (Add([$"source:{chunk.Path}:{chunk.Hash}:{chunk.StartLine}"],
                    $"{chunk.Path.Replace('\\', '/')}:{chunk.StartLine}-{endLine}\n```\n{chunk.Text}\n```", MaxContextBytes))
                snippets++;
        }
        return new RetrievalResult(text.ToString().TrimEnd(), keys, bytes, timer.ElapsedMilliseconds)
        {
            Files = files,
            Snippets = snippets,
            Findings = findings,
            Method = string.Join(" + ", legs),
        };
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
            var m = DefinitionPattern.Match(lines[i]);
            if (!m.Success) m = GoMethodPattern.Match(lines[i]);
            // SQL usually sits inside a string (db.exec(`CREATE TABLE ...`)), so tables match anywhere on the line.
            if (!m.Success) m = TablePattern.Match(lines[i]);
            if (!m.Success) m = MethodPattern.Match(lines[i]);
            if (!m.Success) continue;
            var stems = SearchText.Tokens(m.Groups["name"].Value).Select(SearchText.Stem).ToList();
            found.Add((m.Groups["name"].Value, i + 1, terms.Any(t => t.Length >= 3 && stems.Any(s => s.StartsWith(t, StringComparison.Ordinal)))));
        }
        return found.OrderByDescending(f => f.Match).ThenBy(f => f.Line).Take(4)
            .OrderBy(f => f.Line).Select(f => $"{f.Name} (L{f.Line})").ToList();
    }

    private static readonly System.Text.RegularExpressions.Regex DefinitionPattern = new(
        @"^\s*(?:export\s+)?(?:(?:public|private|protected|internal|static|async|sealed|partial|abstract|default|readonly|override|virtual|pub|declare)\s+)*" +
        @"(?:class|interface|record|struct|enum|function|def|fn|func|type|const|let|var|namespace)\s+" +
        @"(?<name>[A-Za-z_$][\w$]*)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex GoMethodPattern = new(
        @"^\s*func\s*\([^)]*\)\s*(?<name>[A-Za-z_]\w*)\s*\(",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // C#/Java members: at least one modifier, a return type, a name and an opening parenthesis.
    private static readonly System.Text.RegularExpressions.Regex MethodPattern = new(
        @"^\s*(?:(?:public|private|protected|internal|static|async|sealed|override|virtual|abstract|partial|final|synchronized)\s+)+" +
        @"[\w<>\[\],.?]+\s+(?<name>[A-Za-z_]\w*)\s*[(<]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex TablePattern = new(
        @"\bcreate\s+(?:virtual\s+)?table\s+(?:if\s+not\s+exists\s+)?[`""\[]?(?<name>[A-Za-z_][\w]*)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

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
        lock (_gate) { _disposed = true; running = _refreshes.Values.ToArray(); }
        try { Task.WhenAll(running).GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Project indexing stopped: {ex.Message}"); }
    }
}
