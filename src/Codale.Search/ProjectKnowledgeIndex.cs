using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using Codale.Core.Features;
using Codale.Core.Projects;
using Codale.Db;

namespace Codale.Search;

/// <summary>
/// Local project retrieval over a compact index: one profile per source file (path, declarations, comment subjects),
/// the topics of earlier Codale turns, and Git summaries loaded on demand. Ranking runs on the in-memory source
/// snapshot; excerpts are read from disk and verified by hash. Indexing is asynchronous.
/// </summary>
public sealed partial class ProjectKnowledgeIndex(CodaleDb db, ISourceIndexes? sources = null) : IDisposable
{
    private readonly ISourceIndexes _sources = sources ?? SharedSourceIndexes.Instance;

    /// <summary>The cosine a trained embedding model's neighbour needs without lexical support.</summary>
    private const float ModelMatchSimilarity = 0.3f;
    private const int WindowLines = 24;
    /// <summary>Largest file indexed; bigger ones are generated, minified or data.</summary>
    internal const int MaxFileBytes = 1_000_000;
    /// <summary>Average line length past which a large file is a minified bundle, not source.</summary>
    private const int MaxAverageLineChars = 300;
    private const double DocWeight = 0.5;
    private static readonly string[] DocOrTestTerms = ["doc", "readme", "markdown", "md", "test", "spec", "changelog", "licen"];
    /// <summary>Cancelled on dispose, so a closing window never keeps indexing.</summary>
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task> _refreshes = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly ConcurrentDictionary<string, DateTime> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SourceFile> _indexedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _revisions = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per worktree: files whose content changed while Codale watched, and when. Covers edits
    /// made through the shell (sed, scripts), which never arrive as a tool's file change.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _contentChanges = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>More changed files than this in one refresh is a checkout, pull or formatter run, not a session's edits.</summary>
    private const int MaxRecordedChanges = 40;
    private const int RefreshCooldownMs = 3_000;
    /// <summary>A scan this old is rebuilt even if the watcher saw nothing, in case it dropped events.</summary>
    private const int MaxTrustedWatcherMs = 30_000;
    private readonly ConcurrentDictionary<string, long> _lastRefresh = new(StringComparer.OrdinalIgnoreCase);
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
            // Every search, ghost-text lookup and chat turn asks for a refresh; one that just finished is still current,
            // and the file watcher marks the scan stale on a real change. Rescanning on each ask is CPU spent for nothing.
            return _lastRefresh.TryGetValue(worktree, out var finished) && Environment.TickCount64 - finished < RefreshCooldownMs
                && _sources.For(worktree).IsUpToDate
                && _revisions.TryGetValue(worktree, out var knownRevision) && knownRevision == Revision(worktree)
                ? Task.CompletedTask
                : (_refreshes[worktree] = Task.Run(async () =>
            {
                Interlocked.Increment(ref _indexing);
                // Indexing is background work: it yields to the UI and to the agents the user is waiting on.
                var thread = Thread.CurrentThread;
                var priority = thread.Priority;
                try { thread.Priority = ThreadPriority.BelowNormal; } catch (Exception) { }
                try
                {
                    IndexingChanged?.Invoke();
                    _stopping.Token.ThrowIfCancellationRequested();
                    var index = _sources.For(worktree);
                    // HEAD changes and lost watcher events must not leave a branch's snapshot reusable forever.
                    var revision = Revision(worktree);
                    var watched = _revisions.TryGetValue(worktree, out var previousRevision);
                    var stale = !watched || previousRevision != revision
                        || Environment.TickCount64 - _lastRefresh.GetValueOrDefault(worktree) > MaxTrustedWatcherMs;
                    // Otherwise the index's own watcher has already marked it stale if anything changed.
                    if (stale) index.Invalidate(true);
                    _revisions[worktree] = revision;
                    var snapshot = await index.GetSnapshotAsync().ConfigureAwait(false);
                    var changed = RefreshCompact(worktree, snapshot);
                    // The first refresh in this process compares against an index from an earlier run, and a
                    // branch switch rewrites whatever differs: neither is work done in a session.
                    if (watched && SameBranch(previousRevision!, revision) && changed.Count is > 0 and <= MaxRecordedChanges)
                    {
                        var now = DateTime.UtcNow;
                        var log = _contentChanges.GetOrAdd(worktree, _ => new(StringComparer.OrdinalIgnoreCase));
                        foreach (var path in changed) log[path] = now;
                    }
                    // Related source files can borrow a portion of this file's search score.
                    RefreshGraph(worktree, snapshot);
                }
                finally
                {
                    try { thread.Priority = priority; } catch (Exception) { }
                    _lastRefresh[worktree] = Environment.TickCount64;
                    Interlocked.Decrement(ref _indexing);
                    IndexingChanged?.Invoke();
                }
            }));
        }
    }

    /// <summary>Agent turns running per worktree; while any is, reads do not rescan it.</summary>
    private readonly ConcurrentDictionary<string, int> _agentTurns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An agent turn started editing <paramref name="worktree"/>. Each of its writes marks the index stale, and every
    /// search and ghost-text lookup would then re-index mid-turn; the index instead
    /// catches up once, at <see cref="EndAgentTurn"/>.
    /// </summary>
    public void BeginAgentTurn(string worktree) =>
        _agentTurns.AddOrUpdate(ProjectPaths.Normalize(worktree), 1, (_, count) => count + 1);

    /// <summary>The turn <see cref="BeginAgentTurn"/> marked is over; the last one to end refreshes the index.</summary>
    public void EndAgentTurn(string worktree)
    {
        worktree = ProjectPaths.Normalize(worktree);
        var left = _agentTurns.AddOrUpdate(worktree, 0, (_, count) => Math.Max(0, count - 1));
        if (left > 0) return;
        _agentTurns.TryRemove(new KeyValuePair<string, int>(worktree, 0));
        Warm(worktree);
    }

    public void Warm(string worktree)
    {
        worktree = ProjectPaths.Normalize(worktree);
        // Mid-turn, an index this process already built answers as it is; stale rows are dropped by hash, and a
        // steer that needs the agent's latest edits still waits for a refresh through CaughtUp.
        if (_agentTurns.GetValueOrDefault(worktree) > 0 && _lastRefresh.ContainsKey(worktree)) return;
        _ = RefreshAsync(worktree).ContinueWith(t => { _ = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>Ranked, hash-verified source excerpts for discovery; only fresh source is returned.</summary>
    /// <param name="recent">Accepted for callers that pass the conversation; ranking uses the prompt alone.</param>
    public IReadOnlyList<SourceChunk> Search(string worktree, string prompt, string recent = "", int maxFiles = 12, int maxChunksPerFile = 1)
    {
        worktree = ProjectPaths.Normalize(worktree);
        Warm(worktree);
        return SearchCompact(worktree, prompt, maxFiles, maxChunksPerFile, null, out _);
    }

    /// <summary><see cref="Search(string, string, string, int, int)"/> reusing the ranking the caller already made of the same prompt.</summary>
    internal IReadOnlyList<SourceChunk> Search(string worktree, string prompt, int maxFiles, int maxChunksPerFile, IReadOnlyList<SourceExplorer.Ranked> lexical)
    {
        worktree = ProjectPaths.Normalize(worktree);
        Warm(worktree);
        return SearchCompact(worktree, prompt, maxFiles, maxChunksPerFile, null, out _, lexical);
    }

    /// <summary><see cref="Search(string, string, string, int, int)"/> off the caller's thread.</summary>
    public Task<IReadOnlyList<SourceChunk>> SearchAsync(string worktree, string prompt, string recent = "", int maxFiles = 12, int maxChunksPerFile = 1,
        CancellationToken ct = default) =>
        Task.Run(() => Search(worktree, prompt, recent, maxFiles, maxChunksPerFile), ct);

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]{3,}", System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial Regex IdentifierPattern();

    /// <summary>Code-like names in a prompt (camelCase, PascalCase with an inner capital, snake_case), in order, at most four.</summary>
    public static IReadOnlyList<string> Identifiers(string prompt) =>
        [.. IdentifierPattern().Matches(prompt).Select(m => m.Value)
            .Where(v => v.Contains('_') || (v.Any(char.IsLower) && v.Skip(1).Any(char.IsUpper)))
            .Distinct(StringComparer.Ordinal).Take(4)];

    /// <summary>Blank-line runs collapsed and trailing spaces dropped: whitespace is tokens too.</summary>
    private static string Compact(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.ReplaceLineEndings("\n"), @"[ \t]*\n(?:[ \t]*\n)+", "\n").Trim();

    private static string Clip(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "\n…";

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
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            var content = File.ReadAllText(path);
            return IsIndexable(content) ? content : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>True for content worth indexing: text, not too large, not minified.</summary>
    private static bool IsIndexable(string content)
    {
        if (content.Length is 0 or > MaxFileBytes || content.Contains('\0')) return false;
        // Only a big file can be a bundle: a few long lines in a small one are just long lines.
        if (content.Length < 20_000) return true;
        var lineCount = 1;
        foreach (var c in content) if (c == '\n') lineCount++;
        return content.Length / lineCount <= MaxAverageLineChars;
    }

    public void Dispose()
    {
        Task[] running;
        lock (_gate) { _disposed = true; running = [.. _refreshes.Values, .. _compactVectorJobs]; }
        _stopping.Cancel();
        try { Task.WhenAll(running).GetAwaiter().GetResult(); }
        catch (Exception) { }
    }
}
