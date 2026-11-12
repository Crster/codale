using System.Text;
using Codale.Agents.History;
using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Core.Features;
using Codale.Core.Helper;
using Codale.Core.Projects;
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
    private readonly HashSet<string> _deliveredContext = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceReference> _turnReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _turnEditedPaths = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files the agent opened with a read tool this turn, first few only: what it needed beyond the enrichment.</summary>
    private readonly HashSet<string> _turnReadPaths = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxLearnedReads = 6;
    /// <summary>When the current turn's request went out; files changed on disk since then are the turn's work.</summary>
    private DateTime? _turnStartedUtc;
    private readonly StringBuilder _turnFindings = new();
    private string? _retrievalId;

    /// <summary>The intent Auto routed the prompt being sent to; null outside Auto, where the context stays the plain verified kind.</summary>
    private Codale.Smart.SmartIntent? _routedIntent;

    /// <summary>The prompt Auto already put in the transcript before routing, with the composer attachments to restore if it never goes out.</summary>
    private (UserMessageItem Item, List<ComposerAttachment> Composer)? _preShown;
    private bool _needsResumeContext;
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
            _needsResumeContext = true;
            OnPropertyChanged(nameof(EndpointName));
        }
        else if (mapping is not null)
        {
            CliId = mapping.Provider;
            EndpointName = mapping.Endpoint;
            ResumeSessionId = mapping.ProviderSessionId;
            OnPropertyChanged(nameof(EndpointName));
            foreach (var key in features.DeliveredContext(mapping.ProviderSessionId)) _deliveredContext.Add(key);
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

    /// <summary>The feature's summary, or what it was started for while it has none.</summary>
    public string FeatureSummary() =>
        FeatureId is { } id && Features?.Get(id) is { } feature
            ? feature.Summary.Length > 0 ? feature.Summary : feature.Description
            : "";

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
        _deliveredContext.Clear();
        _retrievalId = null;
        _needsResumeContext = false;
    }

    private void BindHistoryProvider()
    {
        if (FeatureId is { } id && SessionId is { Length: > 0 } native)
            Features?.BindProviderSession(id, CliId, native, _projectPath, EndpointName);
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

    private void RecordUserTurn(string text)
    {
        if (Features is not { } features) return;
        if (FeatureId is null)
        {
            var title = text.ReplaceLineEndings(" ").Trim();
            if (title.Length > 90) title = title[..90];
            SetFeature(features.Create(title.Length > 0 ? title : "Attachments", text));
            Title = FeatureName;
        }
        if (_featureLog is not { } log || FeatureId is not { } id) return;
        if (!_attachLogged) { log.Attach(CliId); _attachLogged = true; }
        BindHistoryProvider();
        log.AppendUser(text);
        features.RecordProgress(id, log, text);
        _featureRequest = text;
        _turnStartedUtc = DateTime.UtcNow;
        PublishFeatureActivity(true);
    }

    private void CaptureReference(string path)
    {
        var root = _projectPath;
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
            if (_retrievalId is { } retrieval && IsExplorationTool(tool.ToolName)) features.RecordExploration(retrieval);
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
            if (_retrievalId is { } usageId && completed.Usage is { } usage) features.RecordUsage(usageId, usage.ContextTokens, usage.OutputTokens);
            EndFeatureTurn(completed.Subtype);
            if (FeatureId is { } id)
            {
                foreach (var path in _turnEditedPaths)
                    features.RecordLearnedEdit(id, _projectPath, path, _featureRequest ?? "");
                // A file the agent had to open to do the work is evidence for the next similar request, weaker than an edit.
                foreach (var path in _turnReadPaths.Where(p => !_turnEditedPaths.Contains(p)))
                    features.RecordLearned(id, _projectPath, path, _featureRequest ?? "", LearnedKind.Read);
                // Shell edits (sed, scripts) only show on disk: credit them to this history's working set.
                if (_turnStartedUtc is { } started)
                    _ = features.RecordTurnChangesAsync(id, _projectPath, started);
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                    _turnFindings.ToString(), @"(?:[A-Za-z0-9_.-]+[/\\])+[A-Za-z0-9_.-]+")) CaptureReference(match.Value);
                features.RecordFinding(id, CliId, _projectPath,
                    _featureRequest + "\n" + _turnFindings, _turnReferences.Values.ToList());
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

    private static bool IsExplorationTool(string name) =>
        name.Contains("read", StringComparison.OrdinalIgnoreCase) || name.Contains("search", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("grep", StringComparison.OrdinalIgnoreCase) || name.Contains("glob", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("exec", StringComparison.OrdinalIgnoreCase) || name is "Bash" or "Shell";

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
    private static bool IsNewTopic(string text, IReadOnlyList<string> earlier, string? lastReply) =>
        TopicShift.IsNewTopic(text, earlier, lastReply);

    private async Task<RetrievalResult?> RetrieveProjectContextAsync(string text, UserMessageItem? sent = null, CancellationToken preparationToken = default, DateTimeOffset? preparationDeadline = null)
    {
        _retrievalId = null;
        if (text.StartsWith('/')) return null;
        // "commit this", "continue in full permission mode": no subject to look up, and whatever the
        // conversation is about was enriched when it was asked. Matching the words alone adds noise.
        if (!SearchText.HasTopic(text)) return null;
        using var cts = new CancellationTokenSource();
        using var smartDeadline = CancellationTokenSource.CreateLinkedTokenSource(preparationToken, cts.Token);
        _briefing = cts;
        var session = _session;
        try
        {
            var priorItems = Items.TakeWhile(i => !ReferenceEquals(i, sent)).ToList();
            var earlier = priorItems.OfType<UserMessageItem>().Where(i => i.Text.Length > 0)
                .TakeLast(3).Select(i => i.Text).ToList();
            var lastReply = priorItems.AsEnumerable().Reverse().TakeWhile(i => i is not UserMessageItem)
                .OfType<AssistantMessageItem>().FirstOrDefault(i => !i.IsStreaming)?.Text;
            var newTopic = sent is not null && IsNewTopic(text, earlier, lastReply);
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, _session)) throw new OperationCanceledException();
            if (newTopic) sent!.IsNewTopic = true;
            // Project context enrichment is removed: prompts go out as typed.
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return null;
        }
        finally { _briefing = null; }
    }

    private void ContextDelivered(RetrievalResult? result)
    {
        if (result is null) return;
        _needsResumeContext = false;
        foreach (var key in result.Keys) _deliveredContext.Add(key);
        try
        {
            if (SessionId is { } native) Features?.MarkContextDelivered(native, result.Keys);
        }
        catch (Exception) {  }
    }

    private void ForgetDeliveredContext()
    {
        _retrievalId = null;
        ContextCompacted();
    }

    /// <summary>
    /// The conversation was summarised, by "/compact" or by the CLI on its own: the excerpts it carried
    /// are gone, so the next prompt about them must get them again rather than be told they were sent.
    /// </summary>
    private void ContextCompacted()
    {
        _deliveredContext.Clear();
        try
        {
            if (SessionId is { } native) Features?.ClearDeliveredContext(native);
        }
        catch (Exception) {  }
    }

    private void AbandonFeatureTurn()
    {
        if (IsBusy) EndFeatureTurn("stopped");
    }
}
