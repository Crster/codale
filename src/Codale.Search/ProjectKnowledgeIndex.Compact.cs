using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Codale.Core.Features;
using Codale.Core.Projects;
using Codale.Db;
using Codale.Db.Search;
using Codale.Search.Engine;

namespace Codale.Search;

public sealed partial class ProjectKnowledgeIndex
{
    public IEmbedder? CompactEmbedder { get; init; }
    public Func<bool> CompactEmbeddingsEnabled { get; init; } = () => false;
    public Func<string, string, CancellationToken, Task<IReadOnlyList<FileCommit>>>? ReadFileHistory { get; init; }

    public sealed record FileCommit(string Id, string Subject, int FilesChanged);
    public sealed record TurnFile(string Path, bool Edited);
    private sealed record CompactFile(long Id, string Path, string Hash, SourceSymbol[] Symbols, string[] Subjects, long Length = -1, long Written = 0);
    private sealed record ProfileVector(string Path, string Hash);
    private sealed record IndexedTurn(string Worktree, string Session, string Turn, string[] Terms, TurnFile[] Files, string? Reply = null);
    public sealed record SessionRecall(string Session, string Turn, string[] Terms, TurnFile[] Files, string? Reply);
    private sealed record GitSummary(string Hash, string Revision, FileCommit[] Commits);
    private readonly ConcurrentDictionary<(string Root, string Path), GitSummary> _gitSummaries = new();
    private readonly ConcurrentDictionary<(string Root, string Path), byte> _gitPending = new();
    private readonly ConcurrentDictionary<(string Root, string Model, string Query), string[]> _vectorResults = new();
    private readonly ConcurrentDictionary<string, byte> _vectorPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gitSlots = new(2);
    private readonly SemaphoreSlim _vectorSlots = new(1);
    private long _retryVectorsAfter;

    /// <summary>How long a search waits for a missing source snapshot; within the caller's retrieval budget.</summary>
    private static readonly TimeSpan SnapshotWait = TimeSpan.FromMilliseconds(900);

    private static string WorktreeKey(string root, string path) => Scope(root) + path;
    private static RecordKey<CompactFile> Profile(CodaleDatabase db, string root, string path) => db.Record<CompactFile>().Key(WorktreeKey(root, path));
    private static string Profiles(string root) => "profile-" + Hash(root);
    private SemanticIndex Vectors(CodaleDatabase db, string root) => db.Index("profile-vector-" + Hash(root), CompactEmbedder);
    private static string Turns(string root) => "turn-topics-" + Hash(root);
    private string EmbeddingModel(CodaleDatabase db) => CompactEmbedder?.Model ?? db.EmbeddingModel;

    internal int ProfileCount(string worktree) => Use(db => db.Record<CompactFile>().Keys(Scope(worktree)).Count);
    internal IReadOnlyList<(string Path, float Score)> MatchProfiles(string worktree, IEnumerable<KeywordTerm> terms, int limit)
    {
        var prefix = Scope(worktree);
        return Use<IReadOnlyList<(string Path, float Score)>>(db =>
            [.. db.Index(Profiles(worktree)).FindKeywords(terms, limit).Select(hit => (hit.Key[prefix.Length..], hit.Score))]);
    }

    private static readonly string[][] TopicAliases =
    [
        ["password", "hash", "credential", "storage"],
        ["authentication", "auth", "login", "signin"],
        ["authorization", "permission", "access", "privilege"],
        ["configuration", "config", "setting", "preference"],
        ["exception", "error", "failure"],
        ["cache", "memoization", "reuse"]
    ];

    private static string[] TopicTerms(string text)
    {
        var terms = SearchText.RetrievalTerms(text).ToHashSet(StringComparer.Ordinal);
        // Require both halves so an unrelated hash table or storage task stays out.
        if ((terms.Contains("password") || terms.Contains("credential")) &&
            (terms.Contains("hash") || terms.Contains("storage"))) terms.Add("topic0");
        for (var i = 1; i < TopicAliases.Length; i++)
            if (TopicAliases[i].Any(terms.Contains)) terms.Add("topic" + i);
        return [.. terms];
    }

    public void RecordTurnTopic(string session, string turn, string worktree, string topic, IEnumerable<TurnFile> evidence,
        CodaleDatabase.RecordBatch? batch = null, string? reply = null)
    {
        worktree = ProjectPaths.Normalize(worktree);
        // Normalize paths while preserving the action attached to each file.
        var normalized = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in evidence.Take(64))
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            string full;
            try { full = Path.GetFullPath(file.Path, worktree); }
            catch (ArgumentException) { continue; }
            if (!ProjectPaths.IsInside(worktree, full)) continue;
            var path = Path.GetRelativePath(worktree, full);
            if (Eligible(path)) normalized[path] = file.Edited || normalized.GetValueOrDefault(path);
        }
        var terms = TopicTerms(topic);
        if (terms.Length == 0 || (normalized.Count == 0 && reply is null)) return;
        var record = new IndexedTurn(worktree, session, turn, terms, [.. normalized.Select(f => new TurnFile(f.Key, f.Value))], reply);
        if (batch is null) Use(db => db.WriteBatch(write => StageTurn(write, record)));
        else StageTurn(batch, record);
    }

    private static void StageTurn(CodaleDatabase.RecordBatch batch, IndexedTurn turn) =>
        batch.Put(Scope(turn.Session) + Scope(turn.Worktree) + turn.Turn, turn, batch.Index(Turns(turn.Worktree))
            .Key(turn.Terms.Concat(turn.Files.SelectMany(f => SearchText.Tokens(f.Path)).Select(SearchText.Stem)).Distinct(), mode: SemanticMode.Bm25));

    public void RemoveTurnTopics(string session) => Use(db =>
    {
        var keys = db.Record<IndexedTurn>().Keys(Scope(session));
        db.WriteBatch(batch => { foreach (var key in keys) batch.Delete<IndexedTurn>(key); });
    });

    private IReadOnlyList<IndexedTurn> MatchTurns(string root, string query, string? excludeSession = null)
    {
        var terms = TopicTerms(query);
        return terms.Length == 0 ? [] : Use<IReadOnlyList<IndexedTurn>>(db =>
            [.. db.Index(Turns(root)).FindKeywords(terms.Select(t => new KeywordTerm(t)), 40)
                .Select(hit => hit.Record<IndexedTurn>()?.Get()).OfType<IndexedTurn>()
                .Where(t => t.Session != excludeSession && string.Equals(t.Worktree, root, StringComparison.OrdinalIgnoreCase))
                .Where(t => t.Terms.Intersect(terms).Any(term => term.StartsWith("topic", StringComparison.Ordinal))
                    || t.Terms.Intersect(terms).Count() >= Math.Min(2, terms.Length)
                    || t.Files.Any(f => SearchText.Tokens(f.Path).Select(SearchText.Stem).Intersect(terms).Count() >= 2))
                .Take(12)]);
    }

    /// <summary>The files earlier Codale turns on the same topic read or edited: evidence beside the source's own words.</summary>
    internal IEnumerable<string> TurnEvidence(string root, string prompt) =>
        MatchTurns(ProjectPaths.Normalize(root), prompt).SelectMany(t => t.Files.Take(12)).Select(f => f.Path);

    public IReadOnlyList<SessionRecall> RecallSessions(string worktree, string query, int limit) =>
        [.. MatchTurns(ProjectPaths.Normalize(worktree), query).Take(limit)
            .Select(t => new SessionRecall(t.Session, t.Turn, t.Terms, t.Files, t.Reply))];

    /// <summary>Brings the profiles in line with the snapshot; returns the files whose content changed since they were last profiled.</summary>
    private List<string> RefreshCompact(string root, SourceSnapshot snapshot)
    {
        var changed = new List<string>();
        if (_snapshots.GetValueOrDefault(root) == snapshot.BuiltAt)
        {
            QueueCompactVectors(root);
            return changed;
        }
        var prefix = Scope(root);
        var initial = _snapshots.ContainsKey(root) ? null :
            Use(db => db.Record<CompactFile>().FindPrefix(prefix).ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal));
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Profile writes are committed in groups: one batch per file takes the database's write lock and commits
        // once per file, which costs more than the profiling itself on a large first scan.
        var pending = new List<(CompactFile Profile, SourceFile File, bool ContentChanged)>();
        void Flush()
        {
            if (pending.Count == 0) return;
            Use(db => db.WriteBatch(batch =>
            {
                foreach (var (record, _, contentChanged) in pending)
                {
                    if (contentChanged)
                    {
                        StageProfile(batch, root, record);
                        batch.Delete<ProfileVector>(WorktreeKey(root, record.Path));
                    }
                    else batch.Put(WorktreeKey(root, record.Path), record);
                }
            }));
            foreach (var (_, file, _) in pending) _indexedFiles[root + "\n" + file.RelativePath] = file;
            pending.Clear();
        }
        try
        {
            foreach (var file in snapshot.Files)
            {
                _stopping.Token.ThrowIfCancellationRequested();
                var path = file.RelativePath;
                if (!Eligible(path) || file.Length > MaxFileBytes) continue;
                present.Add(path);
                var identity = root + "\n" + path;
                if (_indexedFiles.TryGetValue(identity, out var old) && ReferenceEquals(old, file)) continue;
                var key = WorktreeKey(root, path);
                var previous = initial is null ? Use(db => db.Record<CompactFile>().Key(key).Get()) : initial.GetValueOrDefault(key);
                if (!file.IsRetained && previous?.Length == file.Length && previous.Written == file.LastWriteUtc.Ticks)
                {
                    _indexedFiles[identity] = file;
                    continue;
                }
                var source = ReadSource(root, path, file);
                if (source is null) { present.Remove(path); continue; }
                var hash = Hash(source);
                var contentChanged = previous?.Hash != hash;
                if (contentChanged || !previous!.Symbols.SequenceEqual(file.SymbolArray))
                {
                    var subjects = Regex.Matches(source, """(?m)^[ \t]*(?:///|//|/\*+|\*|#+|"{3}|'{3})[ \t]*(?:<summary>[ \t]*)?([^<\r\n]+)""")
                        .Select(m => Clip(m.Groups[1].Value.Trim(), 140)).Where(s => s.Length > 0).Distinct().Take(12).ToArray();
                    pending.Add((new(previous?.Id ?? NextId(), path, hash, [.. file.Symbols], subjects, file.Length, file.LastWriteUtc.Ticks), file, true));
                    if (contentChanged && previous is not null) changed.Add(path);
                }
                else if (previous!.Length != file.Length || previous.Written != file.LastWriteUtc.Ticks)
                    pending.Add((previous with { Length = file.Length, Written = file.LastWriteUtc.Ticks }, file, false));
                else _indexedFiles[identity] = file;
                if (pending.Count >= ProfileBatch) Flush();
            }
        }
        finally
        {
            // Files already marked as profiled must reach the database even when the refresh is cancelled part-way.
            Flush();
        }
        var removed = Use(db =>
        {
            var keys = db.Record<CompactFile>().Keys(prefix).Where(key => !present.Contains(key[prefix.Length..])).ToArray();
            db.WriteBatch(batch =>
            {
                foreach (var key in keys)
                {
                    batch.Delete<CompactFile>(key);
                    batch.Delete<ProfileVector>(key);
                }
            });
            return keys;
        });
        foreach (var key in removed) _indexedFiles.TryRemove(root + "\n" + key[prefix.Length..], out _);
        _snapshots[root] = snapshot.BuiltAt;
        QueueCompactVectors(root);
        return changed;
    }

    /// <summary>Profile writes committed per database batch during a refresh.</summary>
    private const int ProfileBatch = 256;

    private static void StageProfile(CodaleDatabase.RecordBatch batch, string root, CompactFile record) =>
        batch.Put(WorktreeKey(root, record.Path), record,
            batch.Index(Profiles(root)).Key(SearchText.Tokens(ProfileText(record)).Select(SearchText.Stem).Distinct(), mode: SemanticMode.Bm25));

    private static string ProfileText(CompactFile file) =>
        file.Path + "\n" + string.Join(' ', file.Symbols.Select(s => s.Name)) + "\n" + string.Join(' ', file.Subjects);

    /// <param name="lexical">The prompt's ranking over the same snapshot, when the caller already made it.</param>
    private IReadOnlyList<SourceChunk> SearchCompact(string root, string prompt, int maxFiles, int perFile,
        IReadOnlyList<string>? workingSet, out bool stale, IReadOnlyList<SourceExplorer.Ranked>? lexical = null)
    {
        stale = false;
        if (maxFiles <= 0 || perFile <= 0) return [];
        var sources = _sources.For(root);
        var snapshot = sources.Current;
        if (snapshot is null)
        {
            // Not scanned yet, or let go because more projects are open than the shared cache keeps: ranking runs on
            // the snapshot, so wait briefly for the scan rather than answer with nothing.
            try
            {
                var scan = sources.GetSnapshotAsync(_stopping.Token);
                if (scan.Wait(SnapshotWait)) snapshot = scan.Result;
            }
            catch (AggregateException) { }
            if (snapshot is null) return [];
        }
        var terms = SearchText.RetrievalTerms(prompt);
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, double score) => scores[path] = scores.GetValueOrDefault(path) + score;
        var rank = 0;
        if (terms.Count > 0)
            foreach (var hit in MatchProfiles(root, Terms(SearchText.RetrievalQueryTerms(prompt)), 48))
                Add(hit.Path, 6.0 / (3 + rank++));

        var words = Regex.Matches(SearchText.WithoutAttachments(prompt), @"[\w./\\-]+").Select(m => m.Value.TrimEnd('.')).ToArray();
        for (var i = 0; i < words.Length; i++)
        {
            var name = words[i];
            if (snapshot.Find(name) is { } exact) Add(exact.RelativePath, 100);
            // A word names a declaration only when it is written as one: an identifier or the whole query. Every
            // large project declares Save, Run and Validate somewhere, and people capitalise the labels they see
            // ("the Auto Mode toggle", "No File is showing"), so prose words, capitalised or not, are left to the
            // lexical ranking; matching them against declarations buried the file the request was about.
            if (words.Length != 1 && !IsIdentifierWord(name)) continue;
            // Once per file: two locals named head must not outweigh the file that declares the check.
            foreach (var path in DefinitionCandidates(snapshot, name))
                Add(path, 30);
        }
        if (terms.Count > 0)
        {
            var ranked = lexical ?? SourceExplorer.Rank(snapshot,
                SourceExplorer.Resolve(snapshot, ProjectSearchFusion.PromptTerms(prompt, snapshot.Contains)), [], setup: false);
            rank = 0;
            foreach (var match in ranked.Take(48))
                Add(match.File.RelativePath, 24.0 / (3 + rank++));
        }
        var turnRank = 0;
        foreach (var turn in MatchTurns(root, prompt))
        {
            var weight = 1.0 / (1 + turnRank++ * 0.25);
            foreach (var file in turn.Files.Take(12))
                scores[file.Path] = Math.Max(scores.GetValueOrDefault(file.Path), (file.Edited ? 9 : 3) * weight);
        }
        foreach (var path in workingSet?.Take(6) ?? []) Add(path.Replace('/', Path.DirectorySeparatorChar), 2);
        foreach (var path in CompactVectorCandidates(root, prompt))
            if (scores.ContainsKey(path)) Add(path, 1);

        // Multiple query words in a file name help distinguish the named component from generic implementations.
        if (terms.Count > 1)
        {
            var queryTerms = terms.ToHashSet(StringComparer.Ordinal);
            foreach (var path in scores.Keys.ToArray())
            {
                var nameTerms = SearchText.Tokens(Path.GetFileNameWithoutExtension(path)).Select(SearchText.Stem);
                if (nameTerms.Where(queryTerms.Contains).Distinct().Skip(1).Any()) Add(path, 1);
            }
        }

        var revision = _revisions.GetValueOrDefault(root, "unknown");
        var wantsDocs = terms.Any(t => DocOrTestTerms.Contains(t));
        double Score(string path, double score) => !wantsDocs && CodeDiscovery.IsTestOrDoc(path) && score < 30 ? score * DocWeight : score;
        foreach (var path in scores.OrderByDescending(p => p.Value).Take(16).Select(p => p.Key).ToArray())
        {
            if (_gitSummaries.TryGetValue((root, path), out var summary) && summary.Revision == revision
                && Use(db => Profile(db, root, path).Get()?.Hash) == summary.Hash
                && summary.Commits.Any(c => SearchText.RetrievalTerms(c.Subject).Intersect(terms).Any()))
                Add(path, 0.5);
        }
        var results = new List<SourceChunk>();
        var found = 0;
        foreach (var score in scores.OrderByDescending(x => Score(x.Key, x.Value))
                     .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Take(Math.Max(48, maxFiles * 3)))
        {
            if (Use(db => Profile(db, root, score.Key).Get()) is not { } file) continue;
            var source = ReadSource(root, file.Path);
            if (source is null) { stale = true; continue; }
            // Changed since it was profiled (an agent mid-turn, before the index catches up): the profile still says
            // what the file is about, so it stays a candidate, but only its current text is returned and the old
            // declaration lines are not trusted to point into it.
            var hash = Hash(source);
            var current = hash == file.Hash;
            if (!current) stale = true;
            var lines = source.ReplaceLineEndings("\n").Split('\n');
            SourceSymbol[] symbols = !current ? [] : [.. file.Symbols.Select(s => (Symbol: s, Score: SymbolRelevance(s.Name, prompt, terms)))
                .Where(s => s.Score > 0).OrderByDescending(s => s.Score).Take(perFile).Select(s => s.Symbol)];
            var starts = symbols.Select(s => s.Line).Where(l => l > 0 && l <= lines.Length).Distinct().ToArray();
            if (starts.Length == 0)
            {
                var at = Array.FindIndex(lines, line => terms.Any(t => line.Contains(t, StringComparison.OrdinalIgnoreCase)));
                starts = [Math.Max(1, at + 1)];
            }
            foreach (var line in starts)
                results.Add(new SourceChunk(root, file.Path, hash, line,
                    string.Join("\n", lines.Skip(line - 1).Take(WindowLines)), file.Id * (MaxFileBytes + 1L) + line,
                    string.Join(' ', (symbols.Length > 0 ? symbols : file.Symbols.Take(4)).Select(s => s.Name))));
            if (found < 3 && current) QueueGitSummary(root, file, revision);
            if (++found == maxFiles) break;
        }
        return results;
    }

    private static bool IsIdentifierWord(string word) =>
        word.Skip(1).Any(char.IsUpper) || word.Contains('_') || (word.Any(char.IsDigit) && word.Any(char.IsLetter));

    private static IEnumerable<string> DefinitionCandidates(SourceSnapshot snapshot, string name) => snapshot.Definitions(name)
        .OrderByDescending(definition => definition.Symbol.Name.Equals(name, StringComparison.Ordinal))
        .ThenBy(definition => definition.Symbol.Kind == SourceSymbolKind.Variable)
        .Select(definition => definition.File.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(12);

    private static int SymbolRelevance(string name, string prompt, IReadOnlyList<string> terms) =>
        (prompt.Contains(name, StringComparison.OrdinalIgnoreCase) ? 20 : 0)
        + SearchText.Tokens(name).Select(SearchText.Stem).Distinct().Intersect(terms).Count();

    private void QueueGitSummary(string root, CompactFile file, string revision)
    {
        if (ReadFileHistory is not { } read || revision == "unknown") return;
        var key = (root, file.Path);
        if (_gitSummaries.TryGetValue(key, out var cached) && cached.Hash == file.Hash && cached.Revision == revision) return;
        if (!_gitPending.TryAdd(key, 0)) return;
        if (!_gitSlots.Wait(0)) { _gitPending.TryRemove(key, out _); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var commits = await read(root, file.Path, _stopping.Token).ConfigureAwait(false);
                if (_gitSummaries.Count >= 512) _gitSummaries.Clear();
                _gitSummaries[key] = new(file.Hash, revision, [.. commits.OrderBy(c => c.FilesChanged).Take(3)]);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { _gitPending.TryRemove(key, out _); _gitSlots.Release(); }
        });
    }

    private void QueueCompactVectors(string root)
    {
        if (!CompactEmbeddingsEnabled() || !_vectorPending.TryAdd(root, 0)) return;
        QueueVectorWork(() =>
        {
            try
            {
                var model = Use(EmbeddingModel);
                var count = 0;
                var keys = Use(db => db.Record<CompactFile>().Keys(Scope(root)));
                foreach (var key in keys)
                {
                    if (_stopping.IsCancellationRequested || !CompactEmbeddingsEnabled() || (CompactEmbedder is { } embedder && model != embedder.Model)) break;
                    var file = Use(db =>
                    {
                        var target = db.Record<ProfileVector>().Key(key);
                        return Vectors(db, root).HasVector(target) ? null : db.Record<CompactFile>().Key(key).Get();
                    });
                    if (file is null) continue;

                    var text = Clip(ProfileText(file), 1400);
                    // Model inference may call a provider. Keep the workspace lease available while it runs.
                    var embedding = CompactEmbedder?.Embed(text);
                    var indexed = Use(db =>
                    {
                        if (model != EmbeddingModel(db) || db.Record<CompactFile>().Key(key).Get()?.Hash != file.Hash)
                            return false;

                        db.WriteBatch(batch => batch.Put(key, new ProfileVector(file.Path, file.Hash),
                            Vectors(db, root).Key([text], embedding, SemanticMode.Vector)));
                        return true;
                    });
                    if (indexed && ++count >= 12) break;
                }
                if (count > 0)
                    foreach (var key in _vectorResults.Keys.Where(k => k.Root == root)) _vectorResults.TryRemove(key, out _);
            }
            finally { _vectorPending.TryRemove(root, out _); }
        }, () => _vectorPending.TryRemove(root, out _));
    }

    private IReadOnlyList<string> CompactVectorCandidates(string root, string prompt)
    {
        if (!CompactEmbeddingsEnabled() || !SearchText.HasTopic(prompt)) return [];
        var query = string.Join(' ', SearchText.RetrievalTerms(prompt));
        var key = (root, Use(EmbeddingModel), query);
        if (_vectorResults.TryGetValue(key, out var cached)) return cached;
        QueueVectorWork(() =>
        {
            var embedding = CompactEmbedder?.Embed(query);
            Use(db =>
            {
                if (key.Item2 != EmbeddingModel(db)) return;
                var index = Vectors(db, root);
                var results = embedding is null ? index.Find(query, 12) : index.Find(embedding, 12);
                if (_vectorResults.Count >= 96) _vectorResults.Clear();
                if (results.Count > 0 && key.Item2 == EmbeddingModel(db))
                    _vectorResults[key] = [.. results.Where(hit => hit.Score >= ModelMatchSimilarity)
                        .Select(hit => hit.Record<ProfileVector>()?.Get()).OfType<ProfileVector>()
                        .Where(hit => Profile(db, root, hit.Path).Get()?.Hash == hit.Hash)
                        .Select(hit => hit.Path)];
            });
        }, () => { });
        return [];
    }

    private readonly HashSet<Task> _compactVectorJobs = [];

    private void QueueVectorWork(Action work, Action skipped)
    {
        lock (_gate)
        {
            if (_disposed || Environment.TickCount64 < Volatile.Read(ref _retryVectorsAfter) || !_vectorSlots.Wait(0)) { skipped(); return; }
            _compactVectorJobs.RemoveWhere(t => t.IsCompleted);
            _compactVectorJobs.Add(Task.Run(() =>
            {
                try { work(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or System.Net.Http.HttpRequestException)
                {
                    Volatile.Write(ref _retryVectorsAfter, Environment.TickCount64 + 60_000);
                }
                finally { _vectorSlots.Release(); }
            }));
        }
    }

    internal IEnumerable<(string Key, string Text)> CompactProvenance(string root, string prompt, string? session,
        IReadOnlyList<SourceChunk> files)
    {
        root = ProjectPaths.Normalize(root);
        var paths = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var revision = _revisions.GetValueOrDefault(root, "unknown");
        foreach (var turn in MatchTurns(root, prompt, session).Take(4))
        {
            foreach (var file in turn.Files.OrderByDescending(f => f.Edited).Take(4))
            {
                var path = file.Path.Replace('\\', '/');
                if (paths.Contains(file.Path))
                    yield return ($"turn:{turn.Session}:{turn.Turn}:{path}",
                        $"{path} was {(file.Edited ? "edited" : "read")} in Codale history {turn.Session}, turn {turn.Turn}.");
                else if (_sources.For(root).Current?.Find(file.Path) is null && !File.Exists(Path.Combine(root, file.Path)))
                    yield return ($"historic:{turn.Session}:{turn.Turn}:{path}",
                        $"{path} appeared in Codale history {turn.Session}, turn {turn.Turn}. It has no current file location.");
            }
        }
        foreach (var file in files.Take(3))
        {
            if (_gitSummaries.TryGetValue((root, file.Path), out var summary) && summary.Hash == file.Hash && summary.Revision == revision
                && summary.Commits.FirstOrDefault() is { } commit)
                yield return ($"git:{file.Path}:{file.Hash}:{commit.Id}",
                    $"{file.Path.Replace('\\', '/')} — {file.Symbols} · {commit.Id[..Math.Min(8, commit.Id.Length)]}: {Clip(Compact(commit.Subject), 140)}");
        }
    }
}
