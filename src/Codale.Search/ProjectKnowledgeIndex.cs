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
    private const double RrfK = 60;
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
                if (_snapshots.GetValueOrDefault(worktree) == snapshot.BuiltAt)
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
                    if (_indexedFiles.TryGetValue(identity, out var previous) && ReferenceEquals(previous, file) && old.ContainsKey(file.RelativePath)) continue;
                    // Re-read disk: a retained snapshot may predate a branch switch.
                    var text = ReadSource(worktree, file.RelativePath);
                    if (text is null) { present.Remove(file.RelativePath); continue; }
                    var hash = Hash(text);
                    if (old.GetValueOrDefault(file.RelativePath) != hash)
                        store.ReplaceSource(worktree, file.RelativePath, hash, FileChunker.Chunk(text));
                    _indexedFiles[identity] = file;
                }
                foreach (var removed in old.Keys.Where(p => !present.Contains(p))) store.ReplaceSource(worktree, removed, null, []);
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

        // 1. Hybrid ranking: BM25 over stemmed prefix terms and cosine over chunk vectors, fused by
        //    reciprocal rank. The latest prompt leads; the recent conversation only tops up.
        var scores = new Dictionary<long, double>();
        void Fuse(IEnumerable<long> ranked, double weight)
        {
            var rank = 0;
            foreach (var id in ranked) scores[id] = scores.GetValueOrDefault(id) + weight / (RrfK + ++rank);
        }
        Fuse(store.SearchSourceTerms(prompt, worktree).Select(c => c.Id), 1.0);
        var terms = SearchText.RetrievalTerms(prompt);
        var legs = new List<string> { "BM25" };
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
                var floor = similar.Count > 0 ? Math.Max(0.08f, similar[0].Score * 0.5f) : 1f;
                // The semantic model leads; the hashed one adds fragments and inflections at lower weight.
                Fuse(similar.Where(v => v.Score >= floor).Select(v => v.Id), embedder is HashedTextEmbedder ? 0.6 : 1.0);
                legs.Add(embedder is HashedTextEmbedder ? "n-gram vectors" : "semantic vectors");
            }
        }
        if (!string.IsNullOrWhiteSpace(recent)) Fuse(store.SearchSourceTerms(recent, worktree, 20).Select(c => c.Id), 0.35);

        var chunks = store.SourceChunks(scores.Keys.ToList()).ToDictionary(c => c.Id);
        // A path naming the topic ("db/connectors.ts" for "connector table") is the strongest locator signal.
        foreach (var chunk in chunks.Values)
        {
            var pathStems = SearchText.Tokens(chunk.Path).Select(SearchText.Stem).ToList();
            var pathHits = terms.Count(t => t.Length >= 3 && pathStems.Any(p => p.StartsWith(t, StringComparison.Ordinal)));
            if (pathHits > 0) scores[chunk.Id] += 0.02 * pathHits;
        }

        // 2. Verify every candidate against disk, then group by file.
        var disk = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Fresh(string path, string hash)
        {
            if (!disk.TryGetValue(path, out var content)) disk[path] = content = ReadSource(worktree, path);
            return content is not null && Hash(content) == hash ? content : null;
        }
        var topFiles = scores.Where(s => chunks.ContainsKey(s.Key)).OrderByDescending(s => s.Value).Select(s => chunks[s.Key])
            .Where(c => Fresh(c.Path, c.Hash) is not null)
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList();
        // Weak tail files cost tokens without helping: keep files scoring at least a third of the best one.
        if (topFiles.Count > 0)
        {
            var best = scores[topFiles[0].First().Id];
            topFiles = topFiles.Where(f => scores[f.First().Id] >= best * 0.34).ToList();
        }

        // 3. The file map: often enough on its own to answer "where is X?", and always enough to open
        //    the right file directly instead of searching the workspace for it.
        var map = new StringBuilder("Relevant files (path — matching definitions):\n");
        var mapKeys = new List<string>();
        var files = new List<string>();
        foreach (var file in topFiles)
        {
            var first = file.First();
            var key = $"map:{first.Path}:{first.Hash}";
            if (delivered.Contains(key)) continue;
            var path = first.Path.Replace('\\', '/');
            var symbols = Definitions(Fresh(first.Path, first.Hash)!, terms);
            map.Append("- ").Append(path);
            if (symbols.Count > 0) map.Append(" — ").Append(string.Join(", ", symbols));
            map.Append('\n');
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
                    $"{chunk.Path.Replace('\\', '/')}:{chunk.StartLine}-{endLine}\n```\n{Clip(chunk.Text, 1100)}\n```", MaxContextBytes))
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
