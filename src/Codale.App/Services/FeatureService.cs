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
    private const int RecentCandidates = 3;
    private const int SeedFiles = 30;

    /// <summary>The whole brief - search and write-up - before the message goes out on the plain seed instead.</summary>
    private static readonly TimeSpan BriefTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ExploreTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A catch-up on other chats' work before the message goes out without one.</summary>
    private static readonly TimeSpan CatchUpTimeout = TimeSpan.FromSeconds(30);

    private readonly string _projectPath;
    private readonly IHelperModel _helper;
    private readonly FeatureStore _store;

    /// <summary>Shared with every other window on the project: open chats, running summaries, the create gate.</summary>
    private readonly FeatureActivity _activity;

    public FeatureService(string projectPath, IHelperModel helper)
    {
        _projectPath = projectPath;
        _helper = helper;
        _store = OpenStore(projectPath);
        _activity = FeatureActivity.For(projectPath);
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

        // Ranked by how much of the message each feature accounts for, not by any one shared word;
        // the most recently active features ride along, since the next prompt is usually about those.
        var documents = _store.Documents();
        var picked = FeatureMatcher.Rank(message, documents).Take(Candidates).Select(m => m.Feature).ToList();
        foreach (var recent in all.Take(RecentCandidates).Where(r => picked.All(p => p.Id != r.Id)))
        {
            picked.Add(recent);
        }

        if (currentId is not null && picked.All(f => f.Id != currentId) && all.FirstOrDefault(f => f.Id == currentId) is { } current)
        {
            picked.Add(current);
        }

        return picked;
    }

    /// <summary>The feature a message is plainly about by its words, or null when none clearly is (see <see cref="FeatureMatcher.Confident"/>).</summary>
    public FeatureInfo? ConfidentMatch(string message) => FeatureMatcher.Confident(message, _store.Documents());

    public FeatureInfo Create(string name, string description)
    {
        var feature = _store.Create(name, description);
        Changed?.Invoke(this, EventArgs.Empty);
        return feature;
    }

    /// <summary>
    /// The feature called <paramref name="name"/> - ignoring case, spacing and punctuation - or a new
    /// one. Two chats that start the same topic at once (each routed before the other created it) end
    /// on one feature instead of splitting its history; every window on the project shares the gate.
    /// </summary>
    public (FeatureInfo Feature, bool Created) FindOrCreate(string name, string description)
    {
        lock (_activity.CreateGate)
        {
            var key = NameKey(name);
            if (key.Length > 0 && _store.List().FirstOrDefault(f => NameKey(f.Name) == key) is { } existing)
            {
                return (existing, false);
            }

            return (Create(name, description), true);
        }
    }

    private static string NameKey(string name) =>
        new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A feature's log; <paramref name="chatId"/> is the chat its appends are written as (null for a reader).</summary>
    public FeatureLog LogOf(string id, string? chatId = null) => new(ProjectPaths.FeatureDirectory(_projectPath, id), chatId);

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
    public string BuildSeed(FeatureInfo feature, string message, int maxProgressChars = SessionHandoff.MaxChars, string? chatId = null)
    {
        var (progress, _) = LogOf(feature.Id).ReadSince(feature.SummaryOffset);
        var seed = FeatureContext.Build(
            feature, progress.Where(e => e.Role != HandoffRole.Tool).ToList(), _store.FilesOf(feature.Id, SeedFiles), [], maxProgressChars,
            _activity.RunningOn(feature.Id));

        return FeatureBrief.OtherChatsSection(OtherChatsOn(feature.Id, chatId)) is { Length: > 0 } others
            ? seed + "\n\n" + others
            : seed;
    }

    /// <summary>Indexed code from earlier work that bears on the message: the feature's own first, the project's when it has little.</summary>
    private List<FileHit> RelevantCode(FeatureInfo feature, string message)
    {
        var code = _store.SearchFiles(message, feature.Id, 6).ToList();
        if (code.Count < 3)
        {
            code.AddRange(_store.SearchFiles(message, null, 4)
                .Where(h => !code.Any(c => c.Path == h.Path && c.StartLine == h.StartLine)));
        }

        return code;
    }

    /// <summary>
    /// The seed for a session's first message, written by the background-task model: it reads the
    /// feature's history with <paramref name="message"/>, explores the code for it and writes a
    /// short brief, so the chat model neither re-reads the raw history nor searches from scratch.
    /// A slow or failed model leaves <see cref="BuildSeed"/> with its progress trimmed; only
    /// <paramref name="ct"/> (the user stopping) ends it without a seed.
    /// </summary>
    public async Task<FeatureBriefResult> PrepareBriefAsync(
        FeatureInfo feature, string message, string? chatId, IProgress<string>? stage, CancellationToken ct)
    {
        // What the session would have started from without the brief: the saving is measured against it.
        var full = BuildSeed(feature, message, chatId: chatId);
        var others = OtherChatsOn(feature.Id, chatId);
        if (!_helper.IsAvailable)
        {
            return new FeatureBriefResult(full, "", [], full.Length, IsFallback: true);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(BriefTimeout);

        IReadOnlyList<ExploredFile> explored = [];
        try
        {
            stage?.Report("Exploring code…");
            try
            {
                explored = await ExploreAsync(message, budget.Token);
            }
            catch (Exception ex) when (!budget.IsCancellationRequested && ex is HelperModelException or IOException or UnauthorizedAccessException)
            {
                // A search that fails leaves the brief to the history alone.
                CrashLog.Trace($"Feature brief exploration skipped: {ex.Message}");
            }

            stage?.Report("Writing handoff…");
            var (progress, _) = LogOf(feature.Id).ReadSince(feature.SummaryOffset);
            var brief = await FeatureBrief.WriteAsync(
                _helper, feature.Name, feature.Summary.Length > 0 ? feature.Summary : feature.Description,
                progress, _store.FilesOf(feature.Id, SeedFiles), WithIndexedCode(explored, RelevantCode(feature, message)),
                message, others, budget.Token);

            if (brief.Length > 0)
            {
                var seed = FeatureBrief.BuildSeed(feature.Name, brief, explored, others);
                return new FeatureBriefResult(seed, brief, explored, full.Length, IsFallback: false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HelperModelException or OperationCanceledException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            CrashLog.Trace($"Feature brief for {feature.Id} skipped: {ex.Message}");
        }

        var fallback = BuildSeed(feature, message, FeatureBrief.FallbackProgressChars, chatId);
        if (FeatureBrief.ExploredSection(explored) is { Length: > 0 } found)
        {
            fallback += "\n\n" + found;
        }

        return new FeatureBriefResult(fallback, "", explored, full.Length, IsFallback: true);
    }

    /// <summary>
    /// What other chats did on a feature since <paramref name="since"/> in its log, for a chat
    /// already in a session there: null when they did nothing. Short histories go across as they are;
    /// longer ones are condensed by the background-task model, or trimmed when it is slow or absent.
    /// Only <paramref name="ct"/> (the user stopping) ends it without a result.
    /// </summary>
    public async Task<FeatureCatchUpResult?> CatchUpAsync(
        string featureId, string chatId, LogPosition since, string message, CancellationToken ct)
    {
        var (work, _) = LogOf(featureId).ReadSince(since, excludeChat: chatId);
        if (!FeatureCatchUp.HasWork(work))
        {
            return null;
        }

        var others = OtherChatsOn(featureId, chatId);
        var running = _activity.RunningOn(featureId);
        var verbatim = FeatureCatchUp.Verbatim(work, running, int.MaxValue);
        if (verbatim.Length <= FeatureCatchUp.VerbatimChars || !_helper.IsAvailable)
        {
            var body = verbatim.Length <= FeatureCatchUp.VerbatimChars ? verbatim : FeatureCatchUp.Verbatim(work, running);
            return new FeatureCatchUpResult(FeatureCatchUp.BuildNote(body, others), body, verbatim.Length, IsCondensed: false);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CatchUpTimeout);
        try
        {
            var note = await FeatureCatchUp.WriteAsync(_helper, work, running, message, budget.Token);
            if (note.Length > 0)
            {
                return new FeatureCatchUpResult(FeatureCatchUp.BuildNote(note, others), note, verbatim.Length, IsCondensed: true);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HelperModelException or OperationCanceledException)
        {
            CrashLog.Trace($"Catch-up for feature {featureId} condensed by trimming: {ex.Message}");
        }

        var trimmed = FeatureCatchUp.Verbatim(work, running);
        return new FeatureCatchUpResult(FeatureCatchUp.BuildNote(trimmed, others), trimmed, verbatim.Length, IsCondensed: false);
    }

    /// <summary>The source search's pick for the message, as path, line ranges and the code there.</summary>
    private async Task<IReadOnlyList<ExploredFile>> ExploreAsync(string message, CancellationToken ct)
    {
        var explorer = new SourceExplorer(SourceIndex.For(_projectPath), new HelperSearchModel(_helper))
        {
            Budget = ExploreTimeout,
            MaxRelated = 4,
        };

        var result = await explorer.RunAsync(message, ct);
        return result.Files
            .Select(f => new ExploredFile(
                f.RelativePath.Length > 0 ? f.RelativePath : f.FilePath,
                f.Ranges.Select(r => (r.StartLine, r.EndLine)).ToList(),
                f.Reason,
                string.Join("\n...\n", f.Ranges.Select(r => r.Code))))
            .ToList();
    }

    /// <summary>The model's code: what the search found, then earlier work's indexed stretches it missed.</summary>
    private static IReadOnlyList<ExploredFile> WithIndexedCode(IReadOnlyList<ExploredFile> explored, IReadOnlyList<FileHit> indexed) =>
    [
        .. explored,
        .. indexed
            .Where(hit => !explored.Any(e => string.Equals(e.Path.Replace('\\', '/'), hit.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            .Select(hit => new ExploredFile(hit.Path, [(hit.StartLine, hit.StartLine + hit.Text.Count(c => c == '\n'))], "written in earlier work on this feature", hit.Text)),
    ];

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

                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is HelperModelException or IOException or UnauthorizedAccessException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
            {
                CrashLog.Trace($"Summary of feature {featureId} skipped: {ex.Message}");
            }
            finally
            {
                // A chat's turn that ended while this one was being written asked for its own: run it now.
                if (_activity.EndSummary(featureId) is { Again: true } again)
                {
                    Summarize(featureId, again.Force);
                }
            }
        });
    }

    public void Dispose() => _store.Dispose();
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
