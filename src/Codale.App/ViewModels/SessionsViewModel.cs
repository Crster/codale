using System.Collections.ObjectModel;
using System.Text;

using Codale.Agents.Claude;
using Codale.Core.Agents;
using Codale.Storage;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Codale.App.ViewModels;

/// <summary>
/// Past conversations for this project, plus anything a crash left behind.
/// </summary>
/// <remarks>
/// The list comes from Claude's own transcript files rather than from Codale's database, so
/// sessions started outside Codale - in a plain terminal - show up too.
/// The database only supplies what the CLI does not know: which sessions Codale itself
/// started, the worktree they run in, and whether one was still running when the app died.
/// </remarks>
public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly ClaudeTranscriptReader _reader;
    private readonly CodaleStore _store;
    private readonly string _projectPath;
    private readonly string _databasePath;

    /// <param name="databasePath">The database <paramref name="store"/> was opened on; background reads open their own connection to it.</param>
    public SessionsViewModel(string projectPath, CodaleStore store, string databasePath)
    {
        _projectPath = projectPath;
        _store = store;
        _databasePath = databasePath;
        _reader = new ClaudeTranscriptReader(projectPath);
    }

    public ObservableCollection<SessionListItem> Sessions { get; } = [];

    /// <summary>
    /// Sessions open in a chat tab right now, and whether each is mid-turn. Kept in
    /// sync by the workspace, so the history list can show which conversations are
    /// live - and which are working - without re-reading any transcript.
    /// </summary>
    private IReadOnlyDictionary<string, bool> _open = new Dictionary<string, bool>();

    /// <summary>Open sessions running in their own worktree; their rows get a lock in the title.</summary>
    private IReadOnlySet<string> _isolated = new HashSet<string>();

    public void UpdateOpenSessions(IReadOnlyDictionary<string, bool> open, IReadOnlySet<string> isolated)
    {
        _open = open;
        _isolated = isolated;
        foreach (var item in Sessions)
        {
            item.ApplyOpenState(_open, _isolated, _worktrees);
        }

        SyncOpenSessions();
    }

    /// <summary>
    /// The sessions open in a chat tab, in history order: what the history list folds
    /// to once the chat in front holds a conversation.
    /// </summary>
    public ObservableCollection<SessionListItem> OpenSessions { get; } = [];

    private void SyncOpenSessions()
    {
        // Live sessions the scan has not caught up with yet keep a stand-in row: a
        // brand-new conversation reaches disk only when its first turn flushes, and
        // until then the list shows nothing where an open tab clearly is. The real
        // summary replaces the stand-in the moment the scan sees the transcript.
        foreach (var id in _open.Keys)
        {
            if (_placeholders.TryGetValue(id, out var held))
            {
                if (Sessions.Any(s => s.SessionId == id && !ReferenceEquals(s, held)))
                {
                    Sessions.Remove(held);
                    _placeholders.Remove(id);
                }

                continue;
            }

            if (!Sessions.Any(s => s.SessionId == id))
            {
                var placeholder = CreatePlaceholder(id);
                _placeholders[id] = placeholder;
                Sessions.Insert(0, placeholder);
            }
        }

        // A tab that closed before its transcript landed takes its stand-in back.
        foreach (var id in _placeholders.Keys.Where(id => !_open.ContainsKey(id)).ToList())
        {
            Sessions.Remove(_placeholders[id]);
            _placeholders.Remove(id);
        }

        var wanted = Sessions.Where(s => s.IsOpen).ToList();
        if (wanted.SequenceEqual(OpenSessions))
        {
            return;
        }

        OpenSessions.Clear();
        foreach (var item in wanted)
        {
            OpenSessions.Add(item);
        }
    }

    /// <summary>The stand-ins currently standing in <see cref="Sessions"/>; see <see cref="SyncOpenSessions"/>.</summary>
    private readonly Dictionary<string, SessionListItem> _placeholders = new(StringComparer.Ordinal);

    /// <summary>
    /// A row for a session that is open in a tab but has no transcript yet. Titled
    /// "(New session)", like an empty one, until the scan replaces it with the real summary.
    /// </summary>
    private SessionListItem CreatePlaceholder(string sessionId)
    {
        var item = new SessionListItem
        {
            Summary = new TranscriptSummary
            {
                SessionId = sessionId,
                FilePath = Path.Combine(_reader.HistoryDirectory, sessionId + ".jsonl"),
                UpdatedAt = DateTimeOffset.Now,
                CustomTitle = "(New session)",
            },
        };
        item.ApplyOpenState(_open, _isolated, _worktrees);
        return item;
    }

    public ObservableCollection<SessionRecord> Interrupted { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInterrupted))]
    public partial int InterruptedCount { get; set; }

    /// <summary>
    /// The session the recovery prompt offers.
    /// </summary>
    /// <remarks>
    /// Exposed as a property rather than binding to <c>Interrupted[0]</c>: x:Bind
    /// evaluates an indexer even when the collection is empty, and the resulting
    /// out-of-range exception surfaces as a stowed WinRT crash at startup.
    /// </remarks>
    [ObservableProperty]
    public partial SessionRecord? FirstInterrupted { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public bool HasInterrupted => InterruptedCount > 0;

    /// <summary>Raised when the user picks a stored session to read.</summary>
    public event EventHandler<TranscriptSummary>? SessionOpened;

    /// <summary>Raised when the user chooses to continue an interrupted session.</summary>
    public event EventHandler<SessionRecord>? SessionResumed;

    /// <summary>
    /// Called once at startup, before anything is marked running: whatever is still
    /// flagged as running belongs to a process that no longer exists.
    /// </summary>
    public void DetectInterrupted()
    {
        Interrupted.Clear();

        foreach (var session in _store.GetInterruptedSessions(_projectPath))
        {
            Interrupted.Add(session);
        }

        InterruptedCount = Interrupted.Count;
        FirstInterrupted = Interrupted.FirstOrDefault();

        // Clear the flags so a second launch does not offer the same recovery forever.
        _store.MarkAllStopped(_projectPath);
    }

    private bool _refreshing;
    private bool _refreshQueued;

    /// <summary>
    /// Rebuilds the list. Calls arriving mid-refresh collapse into one follow-up pass,
    /// so a burst of session changes cannot stack scans or flicker the panel.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshQueued = true;
            return;
        }

        _refreshing = true;
        IsLoading = Sessions.Count == 0;

        try
        {
            do
            {
                _refreshQueued = false;
                await RefreshOnceAsync();
            }
            while (_refreshQueued);
        }
        finally
        {
            _refreshing = false;
            IsLoading = false;
        }
    }

    private async Task RefreshOnceAsync()
    {
        // Reading every transcript in a busy project is disk-bound, and each row also
        // wants its stored title: all of it stays off the UI thread so opening the panel
        // never stutters.
        var scanned = await Task.Run(ReadSessions);
        _worktrees = scanned.Worktrees;
        Merge(scanned.Sessions);

        foreach (var item in Sessions)
        {
            item.ApplyOpenState(_open, _isolated, _worktrees);
        }
    }

    /// <summary>Stored worktree paths of past isolated sessions that still exist on disk, by session id.</summary>
    private IReadOnlyDictionary<string, string> _worktrees = new Dictionary<string, string>();

    /// <summary>The worktree a past session ran in, if it ran isolated and the worktree is still there.</summary>
    public string? WorktreeOf(string sessionId) => _worktrees.GetValueOrDefault(sessionId);

    private (List<TranscriptSummary> Sessions, Dictionary<string, string> Worktrees) ReadSessions()
    {
        var worktrees = new Dictionary<string, string>(StringComparer.Ordinal);
        var sessions = _reader.ListSessions().OrderByDescending(s => s.UpdatedAt).ToList();

        // The shared connection belongs to the UI thread; this read gets its own (WAL lets
        // readers run beside its writes).
        CodaleStore? reader = null;
        try
        {
            reader = new CodaleStore(_databasePath);
        }
        catch (Exception ex) when (ex is StoreSchemaException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            CrashLog.Warn("sessions", $"stored titles unavailable: {ex.Message}");
        }

        using (reader)
        {
            if (reader is null)
            {
                return (sessions, worktrees);
            }

            var stored = reader.GetSessions(_projectPath, limit: int.MaxValue);

            foreach (var record in stored)
            {
                if (record.WorktreePath is { Length: > 0 } worktree && Directory.Exists(worktree))
                {
                    worktrees.TryAdd(record.SessionId, worktree);
                }
            }

            // Titles the helper model generated live in Codale's own database; show them
            // for sessions the CLI itself has not named, then the opening prompt.
            var titles = stored
                .Where(s => s.Title is { Length: > 0 })
                .GroupBy(s => s.SessionId)
                .ToDictionary(g => g.Key, g => g.First().Title!, StringComparer.Ordinal);

            // A title the user typed beats every other source.
            var retitled = sessions.Select(session =>
            {
                if (reader.GetUiState(_projectPath, RetitleKey(session.SessionId)) is { Length: > 0 } chosen)
                {
                    return session with { CustomTitle = chosen };
                }

                return titles.TryGetValue(session.SessionId, out var title) && session.CustomTitle is null
                    ? session with { CustomTitle = title }
                    : session;
            }).ToList();

            return (retitled, worktrees);
        }
    }

    private static string RetitleKey(string sessionId) => $"session.title.{sessionId}";

    /// <summary>
    /// Gives a past session the user's own name. It is kept in Codale's database, not in
    /// the agent's transcript, so it survives the transcript; an empty title
    /// drops the override and the list falls back to the automatic name.
    /// </summary>
    public async Task RetitleAsync(SessionListItem item, string title)
    {
        _store.SetUiState(_projectPath, RetitleKey(item.SessionId), title.Trim());
        await RefreshAsync();
    }

    /// <summary>
    /// Brings <see cref="Sessions"/> in line with the scan without clearing it: rows
    /// that did not change stay put, so the list neither flickers nor re-realizes on
    /// every refresh.
    /// </summary>
    private void Merge(IReadOnlyList<TranscriptSummary> wanted)
    {
        var existing = new Dictionary<string, SessionListItem>(StringComparer.Ordinal);
        foreach (var current in Sessions)
        {
            existing.TryAdd(current.SessionId, current);
        }

        // Placeholders hold the list's top slots - they are the newest conversations -
        // so every scanned row's target index shifts by however many still stand.
        var offset = _placeholders.Values.Count(Sessions.Contains);

        for (var i = 0; i < wanted.Count; i++)
        {
            var summary = wanted[i];
            existing.TryGetValue(summary.SessionId, out var item);

            if (item is not null && item.Summary == summary)
            {
                var at = Sessions.IndexOf(item);
                // A replacement above shrank the list mid-loop; the clamped move still
                // lands every remaining row in scan order (later rows shift it up).
                var target = Math.Min(i + offset, Sessions.Count - 1);
                if (at != target)
                {
                    Sessions.Move(at, target);
                }

                continue;
            }

            var fresh = new SessionListItem { Summary = summary };
            fresh.ApplyOpenState(_open, _isolated, _worktrees);

            if (item is not null)
            {
                Sessions.Remove(item);
            }

            Sessions.Insert(Math.Min(i + offset, Sessions.Count), fresh);
        }

        // Placeholders whose transcript the scan still has not seen survive this pass;
        // the trim below must not eat real rows to make room for them.
        var standing = _placeholders.Values.Count(Sessions.Contains);

        while (Sessions.Count > wanted.Count + standing)
        {
            Sessions.RemoveAt(Sessions.Count - 1);
        }

        SyncOpenSessions();
    }

    [RelayCommand]
    private void Open(SessionListItem? item)
    {
        if (item is not null)
        {
            SessionOpened?.Invoke(this, item.Summary);
        }
    }

    /// <summary>
    /// Removes a past session: Claude's transcript file and Codale's record of it. Open sessions are refused - their tab
    /// would keep writing to it.
    /// </summary>
    public async Task DeleteAsync(SessionListItem item)
    {
        if (item.IsOpen)
        {
            throw new InvalidOperationException("Close the session's tab before deleting it.");
        }

        if (File.Exists(item.Summary.FilePath))
        {
            File.Delete(item.Summary.FilePath);
        }

        _store.DeleteSession(item.SessionId);
        _store.SetUiState(_projectPath, RetitleKey(item.SessionId), "");
        Sessions.Remove(item);
    }

    /// <summary>
    /// A Markdown handout of a past session - what was asked, what the agent answered,
    /// and which files it touched - for pasting into another session, a ticket or a chat.
    /// Subagent chatter and thinking are left out; only the main conversation reads well.
    /// </summary>
    public async Task<string> BuildHandoutAsync(SessionListItem item)
    {
        var events = await LoadTranscriptAsync(item.Summary);
        var text = new StringBuilder();
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        text.AppendLine($"# {item.Title}");
        text.AppendLine();
        text.AppendLine($"Session `{item.SessionId}` · {item.UserTurns} turns · last active {item.UpdatedAt:yyyy-MM-dd HH:mm}");
        text.AppendLine();
        text.AppendLine("## Conversation");

        foreach (var e in events)
        {
            switch (e)
            {
                case UserMessageRecorded { Text.Length: > 0 } user:
                    text.AppendLine();
                    text.AppendLine("### User");
                    text.AppendLine(user.Text.Trim());
                    break;
                case AssistantMessageCompleted { ParentToolUseId: null, Text.Length: > 0 } reply:
                    text.AppendLine();
                    text.AppendLine("### Assistant");
                    text.AppendLine(reply.Text.Trim());
                    break;
                case ToolCallCompleted { FileChange: { } change }:
                    files.Add(Path.GetRelativePath(_projectPath, change.FilePath));
                    break;
            }
        }

        if (files.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Files changed");
            foreach (var file in files)
            {
                text.AppendLine($"- `{file}`");
            }
        }

        return text.ToString();
    }

    /// <summary>What the fork summarizer reads: the session's conversation, clipped to a budget (see <see cref="SessionForking"/>).</summary>
    public async Task<string> BuildForkSourceAsync(SessionListItem item)
    {
        var events = await LoadTranscriptAsync(item.Summary);
        return SessionForking.BuildSource(item.Title, events, _projectPath);
    }

    /// <summary>
    /// Removes what a throwaway summarizer session left behind: Claude's transcript folder
    /// for its scratch directory. Best effort.
    /// </summary>
    public void DiscardScratchSession(string scratchDirectory)
    {
        try
        {
            var history = new ClaudeTranscriptReader(scratchDirectory).HistoryDirectory;
            if (Directory.Exists(history))
            {
                Directory.Delete(history, recursive: true);
            }

            if (Directory.Exists(scratchDirectory))
            {
                Directory.Delete(scratchDirectory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Fork scratch cleanup skipped: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Resume(SessionRecord? record)
    {
        if (record is null)
        {
            return;
        }

        Interrupted.Remove(record);
        InterruptedCount = Interrupted.Count;
        FirstInterrupted = Interrupted.FirstOrDefault();
        SessionResumed?.Invoke(this, record);
    }

    /// <summary>
    /// The stored transcript of a session Codale started - under the project's history,
    /// or its worktree's when it ran isolated. Empty when the CLI never wrote one.
    /// </summary>
    public Task<IReadOnlyList<AgentEvent>> LoadTranscriptAsync(SessionRecord record) =>
        Task.Run<IReadOnlyList<AgentEvent>>(() =>
        {
            var readers = new List<ClaudeTranscriptReader> { _reader };
            if (record.WorktreePath is { Length: > 0 } worktree)
            {
                readers.Insert(0, new ClaudeTranscriptReader(worktree));
            }

            foreach (var reader in readers)
            {
                var file = Path.Combine(reader.HistoryDirectory, record.SessionId + ".jsonl");
                if (File.Exists(file))
                {
                    return reader.Replay(file).ToList();
                }
            }

            return [];
        });

    /// <summary>Replays a stored transcript into chat items for read-only viewing.</summary>
    public Task<IReadOnlyList<AgentEvent>> LoadTranscriptAsync(TranscriptSummary summary) =>
        Task.Run<IReadOnlyList<AgentEvent>>(() => _reader.Replay(summary.FilePath).ToList());
}
