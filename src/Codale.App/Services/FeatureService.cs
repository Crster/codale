using Codale.Agents.History;
using Codale.Core.Features;
using Codale.Core.Helper;
using Codale.Core.Projects;
using Codale.Search;
using Codale.Storage;

namespace Codale.App.Services;

/// <summary>
/// One project's feature history: which features exist, the log each keeps, the summary a fresh
/// session starts from, and the index of the files the agent wrote for it. Owns the project's
/// <see cref="FeatureStore"/>; every call is safe from any thread, and indexing and summaries
/// run in the background so the chat never waits for them.
/// </summary>
public sealed class FeatureService : IDisposable
{
    /// <summary>Up to this many features the router is shown them all; past it, the best matches.</summary>
    private const int ListAllUpTo = 12;
    private const int Candidates = 8;
    private const int SeedFiles = 30;

    private readonly string _projectPath;
    private readonly IHelperModel _helper;
    private readonly FeatureStore _store;
    private readonly HashSet<string> _summarizing = [];

    public FeatureService(string projectPath, IHelperModel helper)
    {
        _projectPath = projectPath;
        _helper = helper;
        _store = OpenStore(projectPath);
    }

    /// <summary>The project's feature database; a throwaway one when it cannot be opened (a path too long, a locked file), so history never keeps the window from opening.</summary>
    private static FeatureStore OpenStore(string projectPath)
    {
        try
        {
            return new FeatureStore(ProjectPaths.FeatureDatabasePath(projectPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or PathTooLongException)
        {
            CrashLog.Warn("features", $"feature database unavailable, history will not outlive this window: {ex.Message}");
            return new FeatureStore(Path.Combine(Path.GetTempPath(), "codale-features-" + Guid.NewGuid().ToString("N"), "features.db"));
        }
    }

    /// <summary>The feature list changed: one was created, renamed, deleted or has a new summary.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<FeatureInfo> List() => _store.List();

    public FeatureInfo? Get(string id) => _store.Get(id);

    public int FileCount(string id) => _store.FileCount(id);

    public IReadOnlyList<FeatureInfo> Search(string text, int limit) => _store.Search(text, limit);

    /// <summary>The features the router is shown for a message: all of them while there are few, else the best matches plus the current one.</summary>
    public IReadOnlyList<FeatureInfo> CandidatesFor(string message, string? currentId)
    {
        var all = _store.List();
        if (all.Count <= ListAllUpTo)
        {
            return all;
        }

        var picked = _store.Search(message, Candidates).ToList();
        if (currentId is not null && picked.All(f => f.Id != currentId) && all.FirstOrDefault(f => f.Id == currentId) is { } current)
        {
            picked.Add(current);
        }

        return picked;
    }

    public FeatureInfo Create(string name, string description)
    {
        var feature = _store.Create(name, description);
        Changed?.Invoke(this, EventArgs.Empty);
        return feature;
    }

    public void Rename(string id, string name)
    {
        _store.Rename(id, name);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        _store.Delete(id);
        LogOf(id).Delete();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public FeatureLog LogOf(string id) => new(ProjectPaths.FeatureDirectory(_projectPath, id));

    /// <summary>A prompt was added to a feature's log: its size and what it asked are what the feature is found by.</summary>
    public void RecordProgress(string id, FeatureLog log, string? prompt)
    {
        _store.RecordProgress(id, log.Length, prompt);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// What a fresh session starts the feature from: its summary, the progress since, the files
    /// written for it, and the stretches of them that bear on <paramref name="message"/>.
    /// </summary>
    public string BuildSeed(FeatureInfo feature, string message)
    {
        var (progress, _) = LogOf(feature.Id).ReadSince(feature.SummaryOffset);
        var code = _store.SearchFiles(message, feature.Id, 6).ToList();
        if (code.Count < 3)
        {
            code.AddRange(_store.SearchFiles(message, null, 4)
                .Where(h => !code.Any(c => c.Path == h.Path && c.StartLine == h.StartLine)));
        }

        return FeatureContext.Build(feature, progress, _store.FilesOf(feature.Id, SeedFiles), code);
    }

    /// <summary>Indexes a file the agent just wrote, in the background: the feature's context can quote it later.</summary>
    public void IndexFile(string featureId, string path)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > FileChunker.MaxFileBytes || SensitivePaths.IsSensitive(path))
                {
                    return;
                }

                var content = File.ReadAllText(path);
                if (FileChunker.IsIndexable(content))
                {
                    _store.ReplaceFile(featureId, Path.GetRelativePath(_projectPath, path), FileChunker.Chunk(content));
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                CrashLog.Trace($"Indexing {path} skipped: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Brings a feature's summary up to date in the background when enough has happened since the
    /// last one (or <paramref name="force"/>), and archives the log once it has grown past 1 MB.
    /// A failed model call keeps the old summary.
    /// </summary>
    public void Summarize(string featureId, bool force = false)
    {
        if (!_helper.IsAvailable)
        {
            return;
        }

        lock (_summarizing)
        {
            if (!_summarizing.Add(featureId))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (_store.Get(featureId) is not { } feature)
                {
                    return;
                }

                var log = LogOf(featureId);
                var length = log.Length;
                var (entries, turns) = log.ReadSince(feature.SummaryOffset);
                var compact = length >= FeatureSummarizer.CompactBytes;

                if (entries.Count == 0 || (!force && !compact && !FeatureSummarizer.IsDue(length - feature.SummaryOffset, turns)))
                {
                    return;
                }

                var summary = await FeatureSummarizer.UpdateAsync(_helper, feature.Name, feature.Summary, entries);
                if (summary.Length == 0)
                {
                    return;
                }

                if (compact)
                {
                    log.Archive();
                    _store.SetSummary(featureId, summary, logOffset: 0, logBytes: 0);
                }
                else
                {
                    _store.SetSummary(featureId, summary, length);
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is HelperModelException or IOException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
            {
                CrashLog.Trace($"Summary of feature {featureId} skipped: {ex.Message}");
            }
            finally
            {
                lock (_summarizing)
                {
                    _summarizing.Remove(featureId);
                }
            }
        });
    }

    public void Dispose() => _store.Dispose();
}