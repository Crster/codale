using Codale.Agents.History;
using Codale.Core.Features;
using Codale.Core.Projects;
using Codale.Db;
using Codale.Db.Search;
using Codale.Search;
using Codale.Workspace;

namespace Codale.App.Services;

/// <summary>
/// One repository's session history and shared knowledge. Each entry keeps its own log and provider
/// mapping; project context is retrieved independently of those session boundaries. Owns the project's
/// <see cref="CodaleDb"/>; every call is safe from any thread, and indexing and summaries
/// run in the background so the chat never waits for them.
/// </summary>
public sealed class FeatureService : IDisposable
{
    private readonly string _projectPath;
    private readonly IWorkspaceData _data;
    private readonly CodaleDb _db;

    public ProjectKnowledgeIndex Knowledge { get; }
    private readonly IDisposable _databaseWatch;
    private readonly Timer _historyRefresh;

    /// <summary>Shared with every other window on the project: open chats, running summaries, the create gate.</summary>
    private readonly FeatureActivity _activity;

    private readonly SmartEmbedder _embedder;
    private readonly SmartExperienceHost _smart;

    public FeatureService(string projectPath, SmartExperienceHost smart, IWorkspaceData data)
    {
        _projectPath = projectPath;
        _data = data;
        _smart = smart;
        _embedder = new SmartEmbedder(smart);
        try
        {
            _db = OpenDatabase(projectPath, _embedder);
        }
        catch
        {
            _embedder.Dispose();
            throw;
        }
        Knowledge = new ProjectKnowledgeIndex(_db)
        {
            Smart = smart.Instance,
            SmartProjectRoot = projectPath
        };
        UpdateSummaryCache();
        Knowledge.SummariesEnabled = () => _smart.Settings.SmartSummaries;
        _smart.Settings.Changed += OnAppSettingsChanged;
        _activity = FeatureActivity.For(projectPath);
        _activity.HistoryDeleted += OnHistoryDeleted;
        _historyRefresh = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
        // Any change to the feature list (a chat appended to a feature, a summary written) refreshes the history list.
        _databaseWatch = _db.Open("feature").Watch(_ => ScheduleHistoryRefresh());
    }

    private void ScheduleHistoryRefresh()
    {
        try { _historyRefresh.Change(TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>The project's database; a throwaway one when it cannot be opened (a path too long, a folder another process holds), so history never keeps the window from opening.</summary>
    private static CodaleDb OpenDatabase(string projectPath, IEmbedder embedder)
    {
        try
        {
            return new CodaleDb(ProjectPaths.FeatureDatabasePath(projectPath), new() { Embedder = embedder });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return new CodaleDb(Path.Combine(Path.GetTempPath(), "codale-features-" + Guid.NewGuid().ToString("N")), new() { Embedder = embedder });
        }
    }

    /// <summary>The feature list changed: one was created, renamed, deleted or has a new summary.</summary>
    public event EventHandler? Changed;

    private void OnHistoryDeleted(object? sender, EventArgs e) => Changed?.Invoke(this, e);

    public IReadOnlyList<FeatureInfo> List() => Knowledge.ListFeatures();

    public FeatureInfo? Get(string id) => Knowledge.GetFeature(id);

    public int FileCount(string id) => Knowledge.FileCount(id);

    public IReadOnlyList<FeatureInfo> Search(string text, int limit) => Knowledge.SearchFeatures(text, limit);

    public FeatureInfo Create(string name, string description)
    {
        var session = Knowledge.CreateFeature(name, description);
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
        Knowledge.RenameFeature(id, name);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        Knowledge.DeleteFeature(id);
        LogOf(id).Delete();
        _activity.NotifyHistoryDeleted(id);
    }

    /// <summary>A feature's log; <paramref name="chatId"/> is the chat its appends are written as (null for a reader).</summary>
    public FeatureLog LogOf(string id, string? chatId = null) => new(_data.History, id, chatId);

    /// <summary>A prompt was added to a feature's log: its size and what it asked are what the feature is found by.</summary>
    public void RecordProgress(string id, FeatureLog log, string? prompt)
    {
        Knowledge.RecordProgress(id, log.Length, prompt);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ProviderSession? ProviderSession(string id) => Knowledge.GetProviderSession(id);


    public void BindProviderSession(string id, string provider, string nativeId, string worktree, string endpoint)
    {
        Knowledge.SetProviderSession(new ProviderSession(id, provider, nativeId, ProjectPaths.Normalize(worktree), endpoint));
    }


    private string? _summaryIdentity;
    private string SummaryIdentity() => _smart.CreateRouter().ClientFor("summary")?.FeatureIdentity ?? "off";
    private void UpdateSummaryCache()
    {
        _summaryIdentity = SummaryIdentity();
        Knowledge.Summaries = new Codale.Smart.SummaryCache(Path.Combine(ProjectPaths.KnowledgeDirectory(_projectPath), "smart-summaries"), _summaryIdentity);
    }

    private void OnAppSettingsChanged(object? sender, EventArgs e)
    {
        if (_summaryIdentity != SummaryIdentity()) UpdateSummaryCache();
    }

    /// <summary>
    /// Credits a finished turn with every file whose content changed on disk while it ran, including
    /// edits made through the shell, which never arrive as a tool's file change. Re-indexes first, so
    /// the next prompt's retrieval already sees those files fresh.
    /// </summary>
    public Task RecordTurnChangesAsync(string sessionId, string worktree, DateTime startedUtc) => Task.Run(() =>
    {
        var root = ProjectPaths.Normalize(worktree);
        try
        {
            foreach (var relative in Knowledge.ChangedSince(root, startedUtc))
                if (ProjectKnowledgeIndex.Eligible(relative)) Knowledge.RecordSessionFile(sessionId, relative);
        }
        catch (Exception)
        {
            // Best effort: a missed credit only weakens the next prompt's ranking.
        }
    });

    public void RecordRetrieval(string retrievalId, string sessionId, RetrievalResult result) => Knowledge.RecordRetrieval(retrievalId, sessionId, result);
    public void RecordExploration(string retrievalId) => Knowledge.RecordExploration(retrievalId);
    public void RecordLearnedEdit(string sessionId, string worktree, string path, string prompt) =>
        RecordLearned(sessionId, worktree, path, prompt, LearnedKind.Edit);

    public void RecordLearned(string sessionId, string worktree, string path, string prompt, string kind)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        var root = ProjectPaths.Normalize(worktree);
        var full = Path.GetFullPath(path, root);
        if (!ProjectPaths.IsInside(root, full)) return;
        var relative = Path.GetRelativePath(root, full);
        if (ProjectKnowledgeIndex.Eligible(relative)) Knowledge.RecordLearned(sessionId, root, relative, prompt, kind);
    }

    /// <summary>The user opened a global-search result: choosing it from that query is the clearest sign it was the answer.</summary>
    public void RecordSearchOpen(string query, string filePath) =>
        RecordLearned("global-search", _projectPath, filePath, query, LearnedKind.Open);

    public void RecordUsage(string retrievalId, int inputTokens, int outputTokens) => Knowledge.RecordUsage(retrievalId, inputTokens, outputTokens);

    public void RecordFinding(string sessionId, string provider, string worktree, string text, IReadOnlyList<SourceReference> references)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Knowledge.PutKnowledge(new KnowledgeHit(Guid.NewGuid().ToString("N"), sessionId, provider,
                ProjectPaths.Normalize(worktree), ProjectKnowledgeIndex.Revision(worktree), text.Length > 2400 ? text[..2400] : text, references));
        }
        catch (Exception ex) when (ex is IOException)
        {
        }
    }

    public void IndexFile(string sessionId, string path)
    {
        var root = Knowledge.GetProviderSession(sessionId)?.Worktree ?? _projectPath;
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (ProjectPaths.IsInside(root, path) && ProjectKnowledgeIndex.Eligible(relative)) Knowledge.RecordSessionFile(sessionId, relative);
    }

    public void Dispose()
    {
        _databaseWatch.Dispose();
        _historyRefresh.Dispose();
        _activity.HistoryDeleted -= OnHistoryDeleted;
        _smart.Settings.Changed -= OnAppSettingsChanged;
        Knowledge.Dispose();
        _db.Dispose();
        _embedder.Dispose();
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