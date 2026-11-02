using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Codale.Core.Features;
using Codale.Core.Projects;
using Codale.Storage;

namespace Codale.Search;

/// <summary>Local project retrieval. Indexing is asynchronous; retrieval uses only committed rows and fresh source.</summary>
public sealed class ProjectKnowledgeIndex(FeatureStore store) : IDisposable
{
    // UTF-8 bytes are a conservative upper bound on byte-tokenizer tokens, including the briefing envelope.
    public const int MaxContextBytes = 3600;
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
                if (_snapshots.GetValueOrDefault(worktree) == snapshot.BuiltAt) return;
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
                lock (_gate) _snapshots[worktree] = snapshot.BuiltAt;
            });
        }
    }

    public void Warm(string worktree)
    {
        _ = RefreshAsync(worktree).ContinueWith(t => System.Diagnostics.Trace.TraceWarning($"Project indexing failed: {t.Exception}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public RetrievalResult Retrieve(string worktree, string prompt, string recent, IReadOnlySet<string> delivered, string? currentSessionId = null)
    {
        var timer = Stopwatch.StartNew();
        worktree = ProjectPaths.Normalize(worktree);
        Warm(worktree);
        var text = new StringBuilder();
        var keys = new List<string>();
        var bytes = 0;
        void Add(string key, string block)
        {
            if (delivered.Contains(key) || keys.Contains(key)) return;
            var rendered = block + Environment.NewLine + Environment.NewLine;
            var size = Encoding.UTF8.GetByteCount(rendered);
            if (bytes + size > MaxContextBytes) return;
            text.Append(rendered);
            bytes += size;
            keys.Add(key);
        }

        // Latest prompt leads the ranking; conversational context fills the remaining candidates.
        var queries = string.IsNullOrWhiteSpace(recent) ? new[] { prompt } : new[] { prompt, recent };
        foreach (var query in queries)
        {
            foreach (var hit in store.SearchKnowledge(query, 4, currentSessionId))
            {
                if (hit.References.Any(r => ReadSource(worktree, r.Path) is not { } content || Hash(content) != r.Hash)) continue;
                var refs = string.Join(", ", hit.References.Select(r => r.Path));
                Add("knowledge:" + hit.Id + ":" + Hash(hit.Text),
                    $"Historical finding (session {hit.SessionId}, provider {hit.Provider}, worktree {hit.Worktree}, revision {hit.Revision}; references: {(refs.Length > 0 ? refs : "unverified; check source before relying on this")}):\n{hit.Text}");
            }
            foreach (var hit in store.SearchSource(query, worktree))
            {
                if (ReadSource(worktree, hit.Path) is not { } current || Hash(current) != hit.Hash) continue;
                Add($"source:{hit.Path}:{hit.Hash}:{hit.StartLine}", $"Source {hit.Path}:{hit.StartLine} (worktree {worktree}, hash {hit.Hash}):\n{hit.Text}");
            }
        }
        return new RetrievalResult(text.ToString().TrimEnd(), keys, bytes, timer.ElapsedMilliseconds);
    }

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
