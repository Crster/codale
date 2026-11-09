using System.Collections.Concurrent;

using Codale.Core.Projects;

namespace Codale.Search;

public sealed record SourceIndexOptions
{
    /// <summary>The most paths one scan lists; a repository past this is indexed in part.</summary>
    public int MaxFiles { get; init; } = 60_000;

    /// <summary>Text files larger than this are listed but not indexed - generated data, not source.</summary>
    public long MaxFileBytes { get; init; } = 1_000_000;

    /// <summary>How much text stays in memory; files past it are re-read from disk when needed.</summary>
    public long MaxRetainedChars { get; init; } = 48_000_000;

    /// <summary>Without a file watcher, how old a snapshot may get before a search refreshes it.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a search waits on a refresh before settling for the previous snapshot.</summary>
    public TimeSpan MaxRefreshWait { get; init; } = TimeSpan.FromSeconds(2);

    public bool Watch { get; init; } = true;

    /// <summary>Where the workspace model is saved between runs; null keeps it in memory for this session only.</summary>
    public string? ModelPath { get; init; }
}

/// <summary>
/// The project's source, scanned once and kept current: the whole tree is listed with
/// the workspace listing (so .gitignore holds), every text file is read and tokenised in parallel, and a
/// file watcher marks the snapshot stale so the next search re-reads only what changed.
/// </summary>
/// <remarks>
/// A search then costs dictionary lookups and a few in-memory regex passes instead of a
/// file scan per keyword. The first scan runs in the background as soon as a project
/// opens (<see cref="Warm"/>); a search that arrives before it finishes awaits it.
/// </remarks>
public sealed class SourceIndex : IDisposable
{
    private const int MaxShared = 6;

    private static readonly ConcurrentDictionary<string, (SourceIndex Index, long Used)> Shared =
        new(StringComparer.OrdinalIgnoreCase);

    private static long _useClock;

    private readonly Lock _gate = new();
    private readonly SourceIndexOptions _options;
    private readonly WorkspaceModel _model;

    /// <summary>Files found binary, by size and write time, so a refresh does not read them again.</summary>
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written)> _binary = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private SourceSnapshot? _current;
    private Task<SourceSnapshot>? _refresh;
    private long _version;
    private int _reloadAll;
    private bool _disposed;

    public SourceIndex(string root, SourceIndexOptions? options = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _options = options ?? new SourceIndexOptions();
        _model = new WorkspaceModel(_options.ModelPath);
    }

    /// <summary>
    /// The index for a project root, shared by the title-bar search, the explore tool and
    /// the editor so they scan once between them. The least recently used is let go past
    /// a handful of projects.
    /// </summary>
    public static SourceIndex For(string root)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var used = Interlocked.Increment(ref _useClock);
        var entry = Shared.AddOrUpdate(key, k => (new SourceIndex(k, new SourceIndexOptions { ModelPath = ModelPathFor(k) }), used), (_, e) => (e.Index, used));

        if (Shared.Count > MaxShared)
        {
            foreach (var stale in Shared.OrderBy(e => e.Value.Used).Take(Shared.Count - MaxShared).ToList())
            {
                if (stale.Key != key && Shared.TryRemove(stale.Key, out var removed))
                {
                    removed.Index.Dispose();
                }
            }
        }

        return entry.Index;
    }

    public string Root { get; }

    /// <summary>The model file for a project, beside its knowledge database.</summary>
    private static string ModelPathFor(string root) => Path.Combine(ProjectPaths.KnowledgeDirectory(root), "workspace-model.json");

    /// <summary>The latest finished snapshot, or null before the first scan completes.</summary>
    public SourceSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>The workspace model: learned from this project's files and kept current as they change.</summary>
    public WorkspaceModel Model => _model;

    /// <summary>
    /// The names to offer at the cursor: the model's suggestions for <paramref name="prefix"/>, ranked with the
    /// word before the cursor. Starts a scan when one is due; empty until the first scan has loaded the model.
    /// </summary>
    public IReadOnlyList<WorkspaceSuggestion> Suggest(string prefix, string? previousWord = null)
    {
        Warm();
        return _model.Suggest(prefix, previousWord);
    }

    /// <summary>Starts the scan now if one is due, without waiting for it. Cheap to call often.</summary>
    public void Warm()
    {
        if (IsFresh(Current))
        {
            return;
        }

        _ = StartRefresh().ContinueWith(
            t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>Marks the snapshot stale, e.g. after the app itself wrote files.</summary>
    public void Invalidate(bool reloadAll = false)
    {
        if (reloadAll) Interlocked.Exchange(ref _reloadAll, 1);
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// A current snapshot: the last one when nothing has changed since, else a refresh -
    /// waited for up to <see cref="SourceIndexOptions.MaxRefreshWait"/> when an older
    /// snapshot can stand in, in full when there is none yet.
    /// </summary>
    public async Task<SourceSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var current = Current;
        if (IsFresh(current))
        {
            return current!;
        }

        var refresh = StartRefresh();
        if (current is null)
        {
            return await refresh.WaitAsync(ct).ConfigureAwait(false);
        }

        var finished = await Task.WhenAny(refresh, Task.Delay(_options.MaxRefreshWait, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        return finished == refresh && refresh.IsCompletedSuccessfully ? refresh.Result : current;
    }

    private bool IsFresh(SourceSnapshot? snapshot) =>
        snapshot is not null &&
        snapshot.Version == Interlocked.Read(ref _version) &&
        (_watcher is not null || DateTime.UtcNow - snapshot.BuiltAt < _options.MaxAge);

    /// <summary>One scan at a time; callers arriving meanwhile share it.</summary>
    private Task<SourceSnapshot> StartRefresh()
    {
        lock (_gate)
        {
            if (_refresh is { IsCompleted: false } running)
            {
                return running;
            }

            // A scan belongs to the index, not to whichever caller asked first: one
            // caller cancelling must not cancel it for the others.
            _refresh = Task.Run(BuildAsync);
            return _refresh;
        }
    }

    private async Task<SourceSnapshot> BuildAsync()
    {
        EnsureWatcher();
        _model.Load();

        var version = Interlocked.Read(ref _version);
        var reloadAll = Interlocked.Exchange(ref _reloadAll, 0) == 1;
        var previous = reloadAll ? null : Current;
        if (reloadAll) _binary.Clear();

        var listed = await ListAsync().ConfigureAwait(false);
        var paths = listed
            .Where(p => !CodeDiscovery.IsNoisePath(p) && !SensitivePaths.IsSensitive(p) &&
                        !CodeDiscovery.ListedOut.Contains(Path.GetExtension(p)))
            .ToList();

        var files = new SourceFile?[paths.Count];
        var read = 0;
        long retained = previous?.Files.Where(f => f.IsRetained).Sum(f => (long)f.Length) ?? 0;

        Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var relative = paths[i];
            var full = Path.Combine(Root, relative);
            FileInfo info;

            try
            {
                info = new FileInfo(full);
                if (!info.Exists || info.Length > _options.MaxFileBytes)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return;
            }

            // Unchanged since the last scan: the same object, nothing re-read.
            if (previous?.Find(relative) is { } known && known.Length == info.Length && known.LastWriteUtc == info.LastWriteTimeUtc)
            {
                files[i] = known;
                return;
            }

            if (_binary.TryGetValue(relative, out var binary) && binary == (info.Length, info.LastWriteTimeUtc))
            {
                return;
            }

            // Stored by the workspace model and unchanged since: its statistics stand, nothing is read.
            if (!reloadAll && _model.Rehydrate(relative, full, info.Length, info.LastWriteTimeUtc) is { } stored)
            {
                files[i] = stored;
                return;
            }

            if (Load(relative, full, info, ref retained) is { } file)
            {
                files[i] = file;
                Interlocked.Increment(ref read);
            }
        });

        var indexed = files.OfType<SourceFile>().OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var snapshot = new SourceSnapshot(listed, indexed, version, read);

        Volatile.Write(ref _current, snapshot);

        // Only what changed is folded into the model: each file replaces its own contribution.
        foreach (var file in indexed)
        {
            _model.Put(file);
        }

        var kept = new HashSet<string>(indexed.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _model.Paths().Where(p => !kept.Contains(p)))
        {
            _model.Remove(gone);
        }

        _model.Save();
        return snapshot;
    }

    private SourceFile? Load(string relative, string full, FileInfo info, ref long retained)
    {
        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (SourceText.LooksBinary(text))
        {
            _binary[relative] = (info.Length, info.LastWriteTimeUtc);
            return null;
        }

        var (keys, counts, total) = SourceTokens.Count(text);
        var symbols = SourceSymbols.Extract(text);
        var transitions = SourceTokens.Transitions(text);
        var keep = Interlocked.Add(ref retained, text.Length) <= _options.MaxRetainedChars;
        if (!keep)
        {
            Interlocked.Add(ref retained, -text.Length);
        }

        return new SourceFile(relative, full, info.Length, info.LastWriteTimeUtc, keep ? text : null, keys, counts, total, symbols, transitions);
    }

    /// <summary>The workspace listing, .gitignore applied.</summary>
    private Task<IReadOnlyList<string>> ListAsync() =>
        new WorkspaceSearch(Root).ListFilesAsync(CodeDiscovery.NoiseGlobs, _options.MaxFiles);

    private void EnsureWatcher()
    {
        if (!_options.Watch || _watcher is not null || _disposed)
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;

            // Lost events: assume anything changed.
            watcher.Error += (_, _) => Invalidate();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // No watcher: snapshots expire by age instead.
        }
    }

    /// <summary>Builds churn bin/obj and git churns .git; neither changes what a search can find.</summary>
    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var relative = e.Name ?? "";
        if (relative.StartsWith(".git", StringComparison.OrdinalIgnoreCase) && (relative.Length == 4 || relative[4] is '\\' or '/'))
        {
            return;
        }

        if (CodeDiscovery.IsNoisePath(relative) || CodeDiscovery.IsNoisePath(Path.DirectorySeparatorChar + relative))
        {
            return;
        }

        Invalidate();
    }

    /// <summary>
    /// Stops watching the tree. An index let go of while something still holds it keeps
    /// answering - its snapshots then expire by age instead of by change.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
    }
}
