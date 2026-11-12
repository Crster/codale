using System.Collections.ObjectModel;

using Codale.Agents.History;
using Codale.App.Services;
using Codale.Core.Features;
using Codale.Core.Projects;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Codale.App.ViewModels;

/// <summary>
/// Explicit project sessions, newest first. Opening a row resumes that conversation.
/// </summary>
public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly FeatureService _features;
    private readonly string _projectPath;

    private readonly SynchronizationContext? _uiContext;

    public SessionsViewModel(string projectPath, FeatureService features)
    {
        _projectPath = projectPath;
        _features = features;
        _uiContext = SynchronizationContext.Current;
        _features.Changed += OnFeaturesChanged;
    }

    /// <summary>A feature was created, renamed, deleted or has a new summary or file: the list follows.</summary>
    private void OnFeaturesChanged(object? sender, EventArgs e) => PostRefresh();

    private void PostRefresh()
    {
        if (_uiContext is { } ui)
        {
            ui.Post(_ => _ = RefreshAsync(), null);
        }
    }

    public ObservableCollection<SessionListItem> Sessions { get; } = [];

    /// <summary>
    /// Features open in a chat tab right now, and whether each is mid-turn. Kept in sync by the
    /// workspace, so the list can show which features are live - and which are working -
    /// without re-reading anything.
    /// </summary>
    private IReadOnlyDictionary<string, bool> _open = new Dictionary<string, bool>();

    public void UpdateOpenSessions(IReadOnlyDictionary<string, bool> open)
    {
        _open = open;
        foreach (var item in Sessions)
        {
            item.ApplyOpenState(_open);
        }

        SyncOpenSessions();
    }

    /// <summary>The features open in a chat tab, in list order: what the list folds to once the chat in front holds a conversation.</summary>
    public ObservableCollection<SessionListItem> OpenSessions { get; } = [];

    private void SyncOpenSessions()
    {
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

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Raised when the user picks a row: a feature to work on, or a terminal CLI's conversation to read.</summary>
    public event EventHandler<TranscriptSummary>? SessionOpened;

    private string? _openingSessionId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial bool IsOpening { get; private set; }

    [ObservableProperty]
    public partial string OpenError { get; set; } = "";

    public bool CanOpen => !IsOpening;

    public bool TryBeginOpening(string sessionId)
    {
        if (IsOpening) return false;
        _openingSessionId = sessionId;
        OpenError = "";
        IsOpening = true;
        foreach (var item in Sessions) item.IsOpening = item.SessionId == sessionId;
        return true;
    }

    public void EndOpening()
    {
        _openingSessionId = null;
        foreach (var item in Sessions) item.IsOpening = false;
        IsOpening = false;
    }

    private bool _refreshing;
    private bool _refreshQueued;

    /// <summary>
    /// Rebuilds the list. Calls arriving mid-refresh collapse into one follow-up pass,
    /// so a burst of changes cannot stack scans or flicker the panel.
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
                var scanned = await Task.Run(ReadRows);
                Merge(scanned);
                foreach (var item in Sessions)
                {
                    item.ApplyOpenState(_open);
                }
            }
            while (_refreshQueued);
        }
        finally
        {
            _refreshing = false;
            IsLoading = false;
        }
    }

    private List<TranscriptSummary> ReadRows()
    {
        var readers = new Dictionary<(string, string), Dictionary<string, TranscriptSummary>>();

        return _features.List()
            .Select(f =>
            {
                var mapping = _features.ProviderSession(f.Id);
                var provider = mapping?.Provider;
                return new TranscriptSummary
                {
                    SessionId = f.Id,
                    FilePath = "",
                    StartedAt = f.CreatedAt,
                    UpdatedAt = f.UpdatedAt,
                    CustomTitle = NativeTitle(readers, mapping, f.Name, f.Description) ?? f.Name,
                    FirstPrompt = f.Description,
                    Detail = provider is not null ? provider == "codex" ? "Codex" : provider == "claude" ? "Claude" : provider : "Session",
                };
            })
            .ToList();
    }

    /// <summary>
    /// The title the CLI stored for a feature's session, but only while the feature still carries the
    /// name cut from its prompt: a name the user or the router chose is kept.
    /// </summary>
    private static string? NativeTitle(
        Dictionary<(string, string), Dictionary<string, TranscriptSummary>> readers, ProviderSession? mapping, string name, string description)
    {
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.ProviderSessionId))
        {
            return null;
        }

        var stem = name.TrimEnd('…', '.', ' ');
        if (stem.Length == 0 || !description.Contains(stem, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = (mapping.Provider, mapping.Worktree);
        if (!readers.TryGetValue(key, out var byId))
        {
            byId = [];
            try
            {
                foreach (var s in CliResume.CreateReader(mapping.Provider, mapping.Worktree)?.ListSessions() ?? [])
                {
                    byId[s.SessionId] = s;
                }
            }
            catch (IOException)
            {
            }

            readers[key] = byId;
        }

        return byId.TryGetValue(mapping.ProviderSessionId, out var hit) && !string.IsNullOrWhiteSpace(hit.CustomTitle)
            ? hit.CustomTitle
            : null;
    }

    /// <summary>Renames a feature.</summary>
    public async Task RetitleAsync(SessionListItem item, string title)
    {
        if (title.Trim() is { Length: > 0 } name)
        {
            _features.Rename(item.SessionId, name);
        }

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

        for (var i = 0; i < wanted.Count; i++)
        {
            var summary = wanted[i];
            existing.TryGetValue(summary.SessionId, out var item);

            if (item is not null && item.Summary == summary)
            {
                var at = Sessions.IndexOf(item);
                var target = Math.Min(i, Sessions.Count - 1);
                if (at != target)
                {
                    Sessions.Move(at, target);
                }

                continue;
            }

            var fresh = new SessionListItem { Summary = summary };
            fresh.ApplyOpenState(_open);
            fresh.IsOpening = fresh.SessionId == _openingSessionId;

            if (item is not null)
            {
                Sessions.Remove(item);
            }

            Sessions.Insert(Math.Min(i, Sessions.Count), fresh);
        }

        while (Sessions.Count > wanted.Count)
        {
            Sessions.RemoveAt(Sessions.Count - 1);
        }

        SyncOpenSessions();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open(SessionListItem? item)
    {
        if (item is not null && CanOpen)
        {
            SessionOpened?.Invoke(this, item.Summary);
        }
    }

    /// <summary>Removes a feature: its summary, its log and what was indexed of its files. Open chats detach through the feature change notification.</summary>
    public void Delete(SessionListItem item)
    {
        _features.Delete(item.SessionId);
        Sessions.Remove(item);
        SyncOpenSessions();
    }
}
