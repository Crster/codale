using Codale.Agents.History;
using Codale.Core.Features;
using Codale.Core.Helper;
using Codale.Core.Projects;
using Codale.Search;
using Codale.Storage;

namespace Codale.App.Services;

/// <summary>
/// One repository's session history and shared knowledge. Each entry keeps its own log and provider
/// mapping; project context is retrieved independently of those session boundaries. Owns the project's
/// <see cref="FeatureStore"/>; every call is safe from any thread, and indexing and summaries
/// run in the background so the chat never waits for them.
/// </summary>
public sealed class FeatureService : IDisposable
{
    private readonly string _projectPath;
    private readonly IHelperModel _helper;
    private readonly FeatureStore _store;
    private readonly ProjectKnowledgeIndex _knowledge;
    public ProjectKnowledgeIndex Knowledge => _knowledge;
    private readonly FileSystemWatcher? _databaseWatcher;
    private readonly Timer _historyRefresh;

    /// <summary>Shared with every other window on the project: open chats, running summaries, the create gate.</summary>
    private readonly FeatureActivity _activity;

    public FeatureService(string projectPath, IHelperModel helper)
    {
        _projectPath = projectPath;
        _helper = helper;
        _store = OpenStore(projectPath);
        _knowledge = new ProjectKnowledgeIndex(_store, () => SemanticModel.Current);
        _knowledge.Warm(projectPath);
        if (AppSettings.ProjectKnowledge)
        {
            // Fetched once, then loaded from disk; the index embeds with it on the next refresh.
            _ = SemanticModel.EnsureLoadedAsync().ContinueWith(t =>
            {
                if (t.Result is { } model)
                {
                    CrashLog.Trace($"Semantic model ready: {model.Model}");
                    _knowledge.Warm(projectPath);
                }
            }, TaskScheduler.Default);
        }
        _activity = FeatureActivity.For(projectPath);
        _activity.HistoryDeleted += OnHistoryDeleted;
        _historyRefresh = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
        try
        {
            _databaseWatcher = new FileSystemWatcher(ProjectPaths.KnowledgeDirectory(projectPath), "project.db*");
            _databaseWatcher.Changed += (_, _) => ScheduleHistoryRefresh();
            _databaseWatcher.Created += (_, _) => ScheduleHistoryRefresh();
            _databaseWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Warn("history", $"Shared history notifications unavailable: {ex.Message}");
        }
    }

    private void ScheduleHistoryRefresh()
    {
        try { _historyRefresh.Change(TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
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

    private void OnHistoryDeleted(object? sender, EventArgs e) => Changed?.Invoke(this, e);

    public IReadOnlyList<FeatureInfo> List() => _store.List();

    public FeatureInfo? Get(string id) => _store.Get(id);

    public int FileCount(string id) => _store.FileCount(id);

    /// <summary>Reevaluates history, compacts its logs, refreshes titles and rebuilds search indexes.</summary>
    public async Task RebuildAndOptimizeAsync(string? featureId = null)
    {
        if (!_helper.IsAvailable)
        {
            throw new HelperModelException("A background model must be available to rebuild history and generate its title.");
        }

        await Task.Run(async () =>
        {
            var features = featureId is null ? _store.List() : _store.List().Where(f => f.Id == featureId).ToList();
            foreach (var candidate in features)
            {
                var gate = _activity.SummaryGate(candidate.Id);
                await gate.WaitAsync();
                try
                {
                    if (_store.Get(candidate.Id) is not { } feature)
                    {
                        continue;
                    }

                    var log = LogOf(feature.Id);
                    var (entries, length) = log.ReadAllSnapshot();
                    using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    var (summary, title) = await FeatureHistoryRebuilder.RebuildAsync(
                        _helper, feature, entries, _activity.RunningOn(feature.Id), budget.Token);
                    // Commit only after both model results succeed; retain concurrent appends as progress.
                    if (length > 0)
                    {
                        log.ArchiveUpTo(length);
                    }
                    _store.SetSummary(feature.Id, summary, logOffset: 0, logBytes: log.Length);
                    SaveSummaryKnowledge(feature.Id, summary);
                    _store.Rename(feature.Id, title);
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                finally
                {
                    gate.Release();
                }
            }

            _store.RebuildAndOptimize();
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<FeatureInfo> Search(string text, int limit) => _store.Search(text, limit);

    public FeatureInfo Create(string name, string description)
    {
        var session = _store.Create(name, description);
        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    /// <summary>Records where a chat is and what it is doing, so the other chats on its feature can be told.</summary>
    public void SetChatActivity(FeatureChat chat) => _activity.SetChat(chat);

    /// <summary>The chat left its feature, or closed.</summary>
    public void ClearChatActivity(string chatId) => _activity.RemoveChat(chatId);

    /// <summary>The other chats open on a feature right now, in any window on the project.</summary>
    public IReadOnlyList<FeatureChat> OtherChatsOn(string featureId, string? chatId) => _activity.OthersOn(featureId, chatId);

    public void Rename(string id, string name)
    {
        _store.Rename(id, name);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        _store.Delete(id);
        LogOf(id).Delete();
        _activity.NotifyHistoryDeleted(id);
    }

    /// <summary>A feature's log; <paramref name="chatId"/> is the chat its appends are written as (null for a reader).</summary>
    public FeatureLog LogOf(string id, string? chatId = null) => new(ProjectPaths.FeatureDirectory(_projectPath, id), chatId);

    /// <summary>A prompt was added to a feature's log: its size and what it asked are what the feature is found by.</summary>
    public void RecordProgress(string id, FeatureLog log, string? prompt)
    {
        _store.RecordProgress(id, log.Length, prompt);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ProviderSession? ProviderSession(string id) => _store.GetProviderSession(id);

    public IReadOnlyList<string> DeliveredContext(string nativeId) => _store.DeliveredContext(nativeId);
    public void MarkContextDelivered(string nativeId, IReadOnlyList<string> keys) => _store.MarkContextDelivered(nativeId, keys);
    public void ClearDeliveredContext(string nativeId) => _store.ClearDeliveredContext(nativeId);

    public void BindProviderSession(string id, string provider, string nativeId, string worktree, string endpoint)
    {
        _store.SetProviderSession(new ProviderSession(id, provider, nativeId, ProjectPaths.Normalize(worktree), endpoint));
    }

    public Task<RetrievalResult> RetrieveAsync(string worktree, string prompt, string recent, IReadOnlySet<string> delivered, string? currentSessionId) =>
        Task.Run(() => _knowledge.Retrieve(worktree, prompt, recent, delivered, currentSessionId));

    public void RecordRetrieval(string retrievalId, string sessionId, RetrievalResult result) => _store.RecordRetrieval(retrievalId, sessionId, result);
    public void RecordExploration(string retrievalId) => _store.RecordExploration(retrievalId);
    public void RecordUsage(string retrievalId, int inputTokens, int outputTokens) => _store.RecordUsage(retrievalId, inputTokens, outputTokens);

    public void RecordFinding(string sessionId, string provider, string worktree, string text, IReadOnlyList<SourceReference> references)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            _store.PutKnowledge(new KnowledgeHit(Guid.NewGuid().ToString("N"), sessionId, provider,
                ProjectPaths.Normalize(worktree), ProjectKnowledgeIndex.Revision(worktree), text.Length > 2400 ? text[..2400] : text, references));
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            CrashLog.Warn("knowledge", $"Finding not saved: {ex.Message}");
        }
    }

    public void IndexFile(string sessionId, string path)
    {
        var root = _store.GetProviderSession(sessionId)?.Worktree ?? _projectPath;
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (ProjectPaths.IsInside(root, path) && ProjectKnowledgeIndex.Eligible(relative)) _store.RecordSessionFile(sessionId, relative);
        _knowledge.Warm(root);
    }

    private void SaveSummaryKnowledge(string sessionId, string summary)
    {
        if (_store.GetProviderSession(sessionId) is not { } mapping) return;
        _store.PutKnowledge(new KnowledgeHit("summary:" + sessionId, sessionId, mapping.Provider, mapping.Worktree,
            ProjectKnowledgeIndex.Revision(mapping.Worktree), summary.Length > 2400 ? summary[..2400] : summary, _store.SessionReferences(sessionId)));
    }

    /// <summary>
    /// Brings a feature's summary up to date in the background when enough has happened since the
    /// last one (or <paramref name="force"/>), and archives the log once it has grown past 1 MB.
    /// A failed model call keeps the old summary. One summary per feature runs at a time across
    /// every window on the project; an ask that arrives meanwhile runs once that one is done.
    /// </summary>
    public void Summarize(string featureId, bool force = false)
    {
        if (!_helper.IsAvailable || !_activity.TryBeginSummary(featureId, force))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var gate = _activity.SummaryGate(featureId);
            await gate.WaitAsync();
            try
            {
                if (_store.Get(featureId) is not { } feature)
                {
                    return;
                }

                var log = LogOf(featureId);
                var length = log.Length;
                var start = Math.Min(feature.SummaryOffset, length);
                var (entries, turns) = log.ReadSince(start);
                var compact = length >= FeatureSummarizer.CompactBytes;

                if (!entries.Any(e => e.Role != HandoffRole.TurnEnd) ||
                    (!force && !compact && !FeatureSummarizer.IsDue(length - start, turns)))
                {
                    return;
                }

                var summary = await FeatureSummarizer.UpdateAsync(_helper, feature.Name, feature.Summary, entries, _activity.RunningOn(featureId));
                if (summary.Length == 0)
                {
                    return;
                }

                if (compact)
                {
                    // Only what this summary read is archived: other chats' lines written meanwhile start the new log.
                    log.ArchiveUpTo(length);
                    _store.SetSummary(featureId, summary, logOffset: 0, logBytes: log.Length);
                }
                else
                {
                    _store.SetSummary(featureId, summary, length);
                }

                SaveSummaryKnowledge(featureId, summary);
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is HelperModelException or IOException or UnauthorizedAccessException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
            {
                CrashLog.Trace($"Summary of feature {featureId} skipped: {ex.Message}");
            }
            finally
            {
                gate.Release();
                // A chat's turn that ended while this one was being written asked for its own: run it now.
                if (_activity.EndSummary(featureId) is { Again: true } again)
                {
                    Summarize(featureId, again.Force);
                }
            }
        });
    }

    public void Dispose()
    {
        _databaseWatcher?.Dispose();
        _historyRefresh.Dispose();
        _activity.HistoryDeleted -= OnHistoryDeleted;
        _knowledge.Dispose();
        _store.Dispose();
    }
}

/// <summary>
/// A feature's seed for a fresh session, and how it was made: the background model's brief and
/// the files it explored, or (<see cref="IsFallback"/>) the plain seed with its progress trimmed.
/// <see cref="FullChars"/> is what the untrimmed plain seed would have carried.
/// </summary>
public sealed record FeatureBriefResult(string Seed, string Brief, IReadOnlyList<ExploredFile> Files, int FullChars, bool IsFallback);

/// <summary>
/// Other chats' work for a chat already in a session: the <see cref="Note"/> its next message carries,
/// the <see cref="Body"/> the transcript shows, and how big the raw history was.
/// </summary>
public sealed record FeatureCatchUpResult(string Note, string Body, int FullChars, bool IsCondensed);
