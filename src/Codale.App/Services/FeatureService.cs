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
/// <see cref="CodaleDb"/>; every call is safe from any thread, and indexing runs in the
/// background so the chat never waits for it.
/// </summary>
public sealed partial class FeatureService : IDisposable
{
    private readonly string _projectPath;
    private readonly IWorkspaceData _data;
    internal IWorkspaceData Data => _data;
    private readonly CodaleDb _db;

    public ProjectKnowledgeIndex Knowledge { get; }
    private readonly IDisposable _databaseWatch;
    private readonly Timer _historyRefresh;

    /// <summary>Shared with every other window on the project: open chats, running summaries, the create gate.</summary>
    private readonly FeatureActivity _activity;

    private readonly SmartEmbedder _embedder;

    public FeatureService(string projectPath, SmartExperienceHost smart, IWorkspaceData data)
    {
        _projectPath = projectPath;
        _data = data;
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
            CompactEmbeddingsEnabled = () => _embedder.UsesModel,
            ReadFileHistory = ReadGitFileHistoryAsync
        };
        _activity = FeatureActivity.For(projectPath);
        _activity.HistoryDeleted += OnHistoryDeleted;
        _historyRefresh = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
        // Any change to the feature list (a chat appended to a feature, a summary written) refreshes the history list.
        _databaseWatch = _db.Open("feature").Watch(_ => ScheduleHistoryRefresh());
        Knowledge.Warm(projectPath);
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

    public void Rename(string id, string name)
    {
        Knowledge.RenameFeature(id, name);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        lock (_topicGate)
        {
            Knowledge.DeleteFeature(id);
            LogOf(id).Delete();
            Knowledge.RemoveTurnTopics(id);
            _db.Open("turn-checkpoint-v1").Open(id).Remove();
        }
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

    public void IndexFile(string sessionId, string path)
    {
        var root = Knowledge.GetProviderSession(sessionId)?.Worktree ?? _projectPath;
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (ProjectPaths.IsInside(root, path) && ProjectKnowledgeIndex.Eligible(relative)) Knowledge.RecordSessionFile(sessionId, relative);
    }

    public void Dispose()
    {
        StopTopicBackfill();
        _databaseWatch.Dispose();
        _historyRefresh.Dispose();
        _activity.HistoryDeleted -= OnHistoryDeleted;
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
