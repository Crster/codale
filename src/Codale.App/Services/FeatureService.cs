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
    private readonly FeatureStore _store;
    private readonly ProjectKnowledgeIndex _knowledge;
    public ProjectKnowledgeIndex Knowledge => _knowledge;
    private readonly FileSystemWatcher? _databaseWatcher;
    private readonly Timer _historyRefresh;

    /// <summary>Shared with every other window on the project: open chats, running summaries, the create gate.</summary>
    private readonly FeatureActivity _activity;

    public FeatureService(string projectPath)
    {
        _projectPath = projectPath;
        _store = OpenStore(projectPath);
        _knowledge = new ProjectKnowledgeIndex(_store);
        _knowledge.Smart = SmartExperienceHost.Instance;
        _knowledge.SmartProjectRoot = projectPath;
        _knowledge.Summaries = new Codale.Smart.SummaryCache(Path.Combine(ProjectPaths.KnowledgeDirectory(projectPath), "smart-summaries"));
        _knowledge.SummariesEnabled = () => AppSettings.SmartSummaries;
        SmartExperienceHost.Instance.Changed += OnSmartChanged;
        _knowledge.Warm(projectPath);
        AppSettings.Changed += OnAppSettingsChanged;
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
            return new FeatureStore(Path.Combine(Path.GetTempPath(), "codale-features-" + Guid.NewGuid().ToString("N"), "features.db"));
        }
    }

    /// <summary>The feature list changed: one was created, renamed, deleted or has a new summary.</summary>
    public event EventHandler? Changed;

    private void OnHistoryDeleted(object? sender, EventArgs e) => Changed?.Invoke(this, e);

    public IReadOnlyList<FeatureInfo> List() => _store.List();

    public FeatureInfo? Get(string id) => _store.Get(id);

    public int FileCount(string id) => _store.FileCount(id);

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

    /// <param name="workingSessionId">The history whose recently written files get a ranking boost; null on a new topic.</param>
    public async Task<RetrievalResult> RetrieveAsync(string worktree, string prompt, string recent, IReadOnlySet<string> delivered, string? currentSessionId,
        string? workingSessionId = null, CancellationToken preparationToken = default, DateTimeOffset? preparationDeadline = null, string? chatId = null, Codale.Smart.SmartIntent? intent = null)
    {
        if (AppSettings.SmartExperience)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(preparationToken);
            var remaining = (preparationDeadline ?? DateTimeOffset.UtcNow.AddSeconds(120)) - DateTimeOffset.UtcNow;
            budget.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            return await _knowledge.RetrieveSmartAsync(worktree, prompt, recent, delivered, currentSessionId,
                workingSessionId is null ? null : _store.FilesOf(workingSessionId, WorkingSetFiles), budget.Token, preparationDeadline, chatId, intent).ConfigureAwait(false);
        }
        return await Task.Run(() => _knowledge.Retrieve(worktree, prompt, recent, delivered, currentSessionId,
            workingSessionId is null ? null : _store.FilesOf(workingSessionId, WorkingSetFiles))).ConfigureAwait(false);
    }

    private bool _smartThinking = AppSettings.SmartThinking;
    private bool _smartSummaries = AppSettings.SmartSummaries;

    private void OnSmartChanged(object? sender, EventArgs e)
    { if (SmartExperienceHost.Instance.Status.State == Codale.Smart.SmartState.Ready) _knowledge.Warm(_projectPath); }

    /// <summary>Switching Smart thinking re-summarises the project now, not on the next prompt.</summary>
    private void OnAppSettingsChanged(object? sender, EventArgs e)
    {
        if (_smartThinking != AppSettings.SmartThinking || _smartSummaries != AppSettings.SmartSummaries)
        {
            _smartThinking = AppSettings.SmartThinking;
            _smartSummaries = AppSettings.SmartSummaries;
            _knowledge.Warm(_projectPath);
        }
    }

    private const int WorkingSetFiles = 8;

    /// <summary>
    /// Credits a finished turn with every file whose content changed on disk while it ran, including
    /// edits made through the shell, which never arrive as a tool's file change. Re-indexes first, so
    /// the next prompt's retrieval already sees those files fresh.
    /// </summary>
    public Task RecordTurnChangesAsync(string sessionId, string worktree, DateTime startedUtc) => Task.Run(async () =>
    {
        var root = ProjectPaths.Normalize(worktree);
        try
        {
            await _knowledge.RefreshAsync(root).ConfigureAwait(false);
            foreach (var relative in _knowledge.ChangedSince(root, startedUtc))
                if (ProjectKnowledgeIndex.Eligible(relative)) _store.RecordSessionFile(sessionId, relative);
        }
        catch (Exception)
        {
            // Best effort: a missed credit only weakens the next prompt's ranking.
        }
    });

    public void RecordRetrieval(string retrievalId, string sessionId, RetrievalResult result) => _store.RecordRetrieval(retrievalId, sessionId, result);
    public void RecordExploration(string retrievalId) => _store.RecordExploration(retrievalId);
    public void RecordLearnedEdit(string sessionId, string worktree, string path, string prompt) =>
        RecordLearned(sessionId, worktree, path, prompt, LearnedKind.Edit);

    public void RecordLearned(string sessionId, string worktree, string path, string prompt, string kind)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        var root = ProjectPaths.Normalize(worktree);
        var full = Path.GetFullPath(path, root);
        if (!ProjectPaths.IsInside(root, full)) return;
        var relative = Path.GetRelativePath(root, full);
        if (ProjectKnowledgeIndex.Eligible(relative)) _store.RecordLearned(sessionId, root, relative, prompt, kind);
    }

    /// <summary>The user opened a global-search result: choosing it from that query is the clearest sign it was the answer.</summary>
    public void RecordSearchOpen(string query, string filePath) =>
        RecordLearned("global-search", _projectPath, filePath, query, LearnedKind.Open);

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
        }
    }

    public void IndexFile(string sessionId, string path)
    {
        var root = _store.GetProviderSession(sessionId)?.Worktree ?? _projectPath;
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (ProjectPaths.IsInside(root, path) && ProjectKnowledgeIndex.Eligible(relative)) _store.RecordSessionFile(sessionId, relative);
        _knowledge.Warm(root);
    }

    public void Dispose()
    {
        _databaseWatcher?.Dispose();
        _historyRefresh.Dispose();
        _activity.HistoryDeleted -= OnHistoryDeleted;
        AppSettings.Changed -= OnAppSettingsChanged;
        SmartExperienceHost.Instance.Changed -= OnSmartChanged;
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
