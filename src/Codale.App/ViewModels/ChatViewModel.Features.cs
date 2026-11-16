using System.Text;

using Codale.Agents.History;
using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Core.Features;
using Codale.Search;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>Explicit session history and project-wide context, independent of conversation topic.</summary>
public sealed partial class ChatViewModel
{
    public FeatureService? Features { get; init; }

    // Retain the binding names used by the history panel; these IDs now identify sessions.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeature))]
    public partial string? FeatureId { get; set; }
    [ObservableProperty]
    public partial string FeatureName { get; set; } = "";
    public bool HasFeature => FeatureId is not null;

    private FeatureLog? _featureLog;
    private bool _recording;
    private bool _attachLogged;
    private bool _watchingFeatures;
    private readonly string _chatId = Guid.NewGuid().ToString("N");
    private string? _featureRequest;
    private CancellationTokenSource? _briefing;
    private readonly Dictionary<string, SourceReference> _turnReferences = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly HashSet<string> _turnEditedPaths = [with(StringComparer.OrdinalIgnoreCase)];
    /// <summary>Files the agent opened with a read tool this turn, first few only: what it needed beyond the enrichment.</summary>
    private readonly HashSet<string> _turnReadPaths = [with(StringComparer.OrdinalIgnoreCase)];
    private const int MaxLearnedReads = 6;
    /// <summary>When the current turn's request went out; files changed on disk since then are the turn's work.</summary>
    private DateTime? _turnStartedUtc;
    private readonly StringBuilder _turnFindings = new();

    /// <summary>The prompt Auto already put in the transcript before routing, with the composer attachments to restore if it never goes out.</summary>
    private (UserMessageItem Item, List<ComposerAttachment> Composer)? _preShown;
    private string? _handoffContext;

    /// <summary>Only opening a history row resumes it. Prompts never select a different session.</summary>
    public async Task<bool> AttachFeatureAsync(FeatureInfo feature, string? cliId = null)
    {
        if (Features is not { } features) return false;
        await DisposeSessionAsync();
        ResetForNewSession();
        var mapping = features.ProviderSession(feature.Id);
        // A history is Codale's, not one CLI's: opened under the other CLI it cannot resume the
        // native session, so that CLI starts fresh and its first prompt carries the transcript.
        var handoff = mapping is not null && cliId is { Length: > 0 } && cliId != mapping.Provider;
        if (handoff)
        {
            CliId = cliId!;
            ResumeSessionId = null;
            OnPropertyChanged(nameof(EndpointName));
        }
        else if (mapping is not null)
        {
            CliId = mapping.Provider;
            EndpointName = mapping.Endpoint;
            ResumeSessionId = mapping.ProviderSessionId;
            OnPropertyChanged(nameof(EndpointName));
        }
        SetFeature(feature);
        if (handoff) CaptureHandoff();
        Title = feature.Name;
        // Read history off-thread so the opening indicator can animate. Tool details stay unloaded.
        var historyLog = _featureLog!;
        var events = await Task.Run(() => historyLog.ReadTail(int.MaxValue, out _));
        foreach (var row in ReplayRows(events))
        {
            Items.Add(row);
        }
        Reflow();
        RestorePendingPlan();
        await ConnectAsync();
        return IsConnected;
    }

    /// <summary>The last <paramref name="maxEvents"/> events of the feature's log as plain text, and whether earlier ones exist.</summary>
    public (string Text, bool HasMore) FeatureHistoryText(int maxEvents)
    {
        if (_featureLog is null)
        {
            return ("", false);
        }

        var text = new StringBuilder();
        var events = _featureLog.ReadTail(maxEvents, out var hasMore);
        foreach (var e in events)
        {
            switch (e)
            {
                case UserMessageRecorded user:
                    text.AppendLine().AppendLine($"You ({user.Timestamp:g})").AppendLine(user.Text);
                    break;

                case AssistantMessageCompleted reply:
                    text.AppendLine().AppendLine("Agent").AppendLine(reply.Text.Trim());
                    break;

                case ToolCallStarted tool when ToolCallItem.PlanInput(tool.ToolName, tool.Input) is { Length: > 0 } plan:
                    text.AppendLine().AppendLine("Agent proposed a plan").AppendLine(plan.Trim());
                    break;

                case ToolCallStarted tool:
                    text.AppendLine($"  · {tool.ToolName}");
                    break;
            }
        }

        return (text.ToString().Trim(), hasMore);
    }

    private void OnFeaturesChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        if (FeatureId is not { } id || Features is not { } features) return;
        if (features.Get(id) is not { } session)
        {
            ClearFeature();
            return;
        }
        if (session.Name != FeatureName) Title = FeatureName = session.Name;
    });

    private void SetFeature(FeatureInfo session)
    {
        if (!_watchingFeatures && Features is not null)
        {
            Features.Changed += OnFeaturesChanged;
            _watchingFeatures = true;
        }
        FeatureId = session.Id;
        FeatureName = session.Name;
        _featureLog = Features!.LogOf(session.Id, _chatId);
        _recording = true;
        _attachLogged = false;
        PublishFeatureActivity(false);
    }

    /// <summary>
    /// The earlier conversation as a context block for the first prompt after a history moved to
    /// another CLI; null once delivered or when nothing was handed over.
    /// </summary>
    private string? TakeHandoffContext()
    {
        var context = _handoffContext;
        _handoffContext = null;
        return context;
    }

    private void CaptureHandoff()
    {
        var (text, more) = FeatureHistoryText(60);
        _handoffContext = text.Length == 0
            ? null
            : "This conversation was started in another coding agent and is continued here. " +
              "Earlier transcript" + (more ? " (most recent part)" : "") + ":\n\n" + text;
    }

    private void ClearFeature()
    {
        _handoffContext = null;
        Features?.ClearChatActivity(_chatId);
        FeatureId = null;
        FeatureName = "";
        _featureLog = null;
        _recording = false;
        _attachLogged = false;
        _featureRequest = null;
        _turnFindings.Clear();
        _turnReferences.Clear();
        _turnEditedPaths.Clear();
        _turnReadPaths.Clear();
        _turnStartedUtc = null;
    }

    private void BindHistoryProvider()
    {
        if (FeatureId is { } id && SessionId is { Length: > 0 } native)
            Features?.BindProviderSession(id, CliId, native, ProjectPath, EndpointName);
    }

    private void PublishFeatureActivity(bool busy)
    {
        if (FeatureId is { } id && Features is { } features)
            features.SetChatActivity(new FeatureChat(_chatId, id, Title ?? FeatureName, busy, _featureRequest));
    }

    private void EndFeatureTurn(string outcome)
    {
        if (_recording) _featureLog?.EndTurn(outcome);
        PublishFeatureActivity(false);
    }

    private void RecordUserTurn(string text, bool steer = false)
    {
        if (Features is not { } features) return;
        if (FeatureId is null)
        {
            // Even a first prompt with no subject of its own names the history: there is nothing better yet.
            SetFeature(features.Create(TitleFromPrompt(text) ?? (text.Trim().Length > 0 ? SessionTitles.FromPrompt(text) : "Attachments"), text));
            Title = FeatureName;
            if (text.Length > 0) NameSessionAsync(FeatureId!, text, FeatureName);
        }
        else
        {
            RenameSessionFromPrompt(features, FeatureId, text);
        }
        if (_featureLog is not { } log || FeatureId is not { } id) return;
        if (!_attachLogged) { log.Attach(CliId); _attachLogged = true; }
        BindHistoryProvider();
        log.AppendUser(text, steer);
        features.RecordProgress(id, log, text);
        _featureRequest = text;
        _turnStartedUtc = DateTime.UtcNow;
        PublishFeatureActivity(true);
    }

    private void CaptureReference(string path)
    {
        var root = ProjectPath;
        try
        {
            var relative = Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path;
            if (ProjectKnowledgeIndex.ReadSource(root, relative) is { } content)
                _turnReferences[relative] = new SourceReference(relative, ProjectKnowledgeIndex.Hash(content));
        }
        catch (ArgumentException) { }
    }

    private void RecordFeatureEvent(AgentEvent e)
    {
        if (Features is not { } features || !_recording) return;
        if (e is SessionInitialized) BindHistoryProvider();
        if (e is ToolCallStarted tool)
        {
            if (tool.Input.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var property in tool.Input.EnumerateObject())
                    if (property.Name is "file_path" or "path" or "file" && property.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                        property.Value.GetString() is { Length: > 0 } path)
                    {
                        CaptureReference(path);
                        if (IsFileReadTool(tool.ToolName) && _turnReadPaths.Count < MaxLearnedReads) _turnReadPaths.Add(path);
                    }
        }
        if (e is ToolCallCompleted done)
        {
            if (!done.IsError)
            {
                if (done.FileChange is { } change) { CaptureReference(change.FilePath); _turnEditedPaths.Add(change.FilePath); }
                foreach (var changed in done.FileChanges ?? []) { CaptureReference(changed.FilePath); _turnEditedPaths.Add(changed.FilePath); }
            }
        }
        if (e is AssistantMessageCompleted { ParentToolUseId: null } reply && _turnFindings.Length < 6000)
        {
            _turnFindings.AppendLine(reply.Text);
        }
        if (e is TurnCompleted completed)
        {
            EndFeatureTurn(completed.Subtype);
            if (FeatureId is { } id)
            {
                foreach (var path in _turnEditedPaths)
                    features.RecordLearnedEdit(id, ProjectPath, path, _featureRequest ?? "");
                // A file the agent had to open to do the work is evidence for the next similar request, weaker than an edit.
                foreach (var path in _turnReadPaths.Where(p => !_turnEditedPaths.Contains(p)))
                    features.RecordLearned(id, ProjectPath, path, _featureRequest ?? "", LearnedKind.Read);
                // Shell edits (sed, scripts) only show on disk: credit them to this history's working set.
                if (_turnStartedUtc is { } started)
                    _ = features.RecordTurnChangesAsync(id, ProjectPath, started);
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                    _turnFindings.ToString(), @"(?:[A-Za-z0-9_.-]+[/\\])+[A-Za-z0-9_.-]+")) CaptureReference(match.Value);
                features.RecordFinding(id, CliId, ProjectPath,
                    _featureRequest + "\n" + _turnFindings, [.. _turnReferences.Values]);
            }
            _turnFindings.Clear();
            _turnReferences.Clear();
            _turnEditedPaths.Clear();
            _turnReadPaths.Clear();
            _turnStartedUtc = null;
            return;
        }
        _featureLog?.Append(e);
    }

    private static bool IsFileReadTool(string name) =>
        name.Equals("Read", StringComparison.OrdinalIgnoreCase) || name.Contains("read_file", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("readfile", StringComparison.OrdinalIgnoreCase) || name.Equals("view", StringComparison.OrdinalIgnoreCase);

    private void IndexWrittenFile(string path)
    {
        if (FeatureId is { } id) Features?.IndexFile(id, path);
    }

    // Final answers are lifted out of the activity timeline for both Claude and Codex.
    // Ignore streaming narration and answers from older turns.
    internal string? LastRoutingReply => Items.Reverse().TakeWhile(i => i is not UserMessageItem)
        .OfType<AssistantMessageItem>().FirstOrDefault(i => !i.IsStreaming)?.Text;

    /// <summary>
    /// Topic detection on local hashed vectors.
    /// </summary>
    /// <summary>
    /// Marks a prompt that opens a new topic, for the transcript's "new topic" marker. Slash commands and prompts with no
    /// subject are never marked, and a failure to read the conversation leaves the prompt unmarked.
    /// </summary>
    private void MarkNewTopic(string text, UserMessageItem? sent)
    {
        if (sent is null || text.StartsWith('/') || !SearchText.HasTopic(text)) return;
        try
        {
            var priorItems = Items.TakeWhile(i => !ReferenceEquals(i, sent)).ToList();
            var earlier = priorItems.OfType<UserMessageItem>().Where(i => i.Text.Length > 0)
                .TakeLast(3).Select(i => i.Text).ToList();
            var lastReply = priorItems.AsEnumerable().Reverse().TakeWhile(i => i is not UserMessageItem)
                .OfType<AssistantMessageItem>().FirstOrDefault(i => !i.IsStreaming)?.Text;
            if (TopicShift.IsNewTopic(text, earlier, lastReply)) sent.IsNewTopic = true;
        }
        catch (Exception)
        {
        }
    }

    private void AbandonFeatureTurn()
    {
        if (IsBusy) EndFeatureTurn("stopped");
    }
}