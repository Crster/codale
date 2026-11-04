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
    private readonly HashSet<string> _deliveredContext = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceReference> _turnReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _turnEditedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _turnFindings = new();
    private string? _retrievalId;
    private bool _needsResumeContext;
    private bool SmartHistory => Features is not null;

    /// <summary>Only opening a history row resumes it. Prompts never select a different session.</summary>
    public async Task<bool> AttachFeatureAsync(FeatureInfo feature)
    {
        if (Features is not { } features) return false;
        TraceMemory("chat.history.begin", $"targetFeature={feature.Id}");
        await DisposeSessionAsync();
        ResetForNewSession();
        var mapping = features.ProviderSession(feature.Id);
        if (mapping is not null)
        {
            CliId = mapping.Provider;
            EndpointName = mapping.Endpoint;
            WorktreePath = mapping.Worktree;
            ResumeSessionId = mapping.ProviderSessionId;
            OnPropertyChanged(nameof(EndpointName));
            foreach (var key in features.DeliveredContext(mapping.ProviderSessionId)) _deliveredContext.Add(key);
        }
        SetFeature(feature);
        Title = feature.Name;
        var events = _featureLog!.ReadTail(400, out var more);
        if (more) Add(new NoticeItem { Text = "Showing recent messages. Earlier messages are available in session history." });
        foreach (var e in events)
        {
            if (e is UserMessageRecorded user)
                Add(new UserMessageItem { Text = user.Text });
            else if (e is ContextEnrichmentRecorded context)
                Add(new ContextEnrichedItem { Summary = context.Summary, Details = context.Details });
            else if (e is AssistantMessageCompleted reply)
            {
                var item = new AssistantMessageItem();
                item.Complete(reply.Text);
                Add(item);
            }
        }
        RestorePendingPlan();
        TraceMemory("chat.history.loaded", $"events={events.Count} earlierEvents={more}");
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

    private void ClearFeature()
    {
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
        _deliveredContext.Clear();
        _retrievalId = null;
        _needsResumeContext = false;
    }

    private void BindHistoryProvider()
    {
        if (FeatureId is { } id && SessionId is { Length: > 0 } native)
            Features?.BindProviderSession(id, CliId, native, WorktreePath ?? _projectPath, EndpointName);
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
        PublishFeatureActivity(true);
    }

    private void CaptureReference(string path)
    {
        var root = WorktreePath ?? _projectPath;
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
                        property.Value.GetString() is { Length: > 0 } path) CaptureReference(path);
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
                    features.RecordLearnedEdit(id, WorktreePath ?? _projectPath, path, _featureRequest ?? "");
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                    _turnFindings.ToString(), @"(?:[A-Za-z0-9_.-]+[/\\])+[A-Za-z0-9_.-]+")) CaptureReference(match.Value);
                features.RecordFinding(id, CliId, WorktreePath ?? _projectPath,
                    _featureRequest + "\n" + _turnFindings, _turnReferences.Values.ToList());
                features.Summarize(id);
            }
            _turnFindings.Clear();
            _turnReferences.Clear();
            _turnEditedPaths.Clear();
            return;
        }
        _featureLog?.Append(e);
    }

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

    private async Task<RetrievalResult?> RetrieveProjectContextAsync(string text, UserMessageItem? sent = null)
    {
        _retrievalId = null;
        if (text.StartsWith('/')) return null;
        // "commit this", "continue in full permission mode": no subject to look up, and whatever the
        // conversation is about was enriched when it was asked. Matching the words alone adds noise.
        if (!SearchText.HasTopic(text)) return null;
        using var cts = new CancellationTokenSource();
        _briefing = cts;
        var session = _session;
        try
        {
            var priorItems = Items.TakeWhile(i => !ReferenceEquals(i, sent)).ToList();
            var earlier = priorItems.OfType<UserMessageItem>().Where(i => i.Text.Length > 0)
                .TakeLast(3).Select(i => i.Text).ToList();
            var lastReply = priorItems.AsEnumerable().Reverse().TakeWhile(i => i is not UserMessageItem)
                .OfType<AssistantMessageItem>().FirstOrDefault(i => !i.IsStreaming)?.Text;
            var newTopic = sent is not null && await Task.Run(() =>
                TopicShift.IsNewTopic(text, earlier, SemanticModel.Best, lastReply), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, _session)) throw new OperationCanceledException();
            if (newTopic) sent!.IsNewTopic = true;
            if (Features is not { } features || !AppSettings.ProjectKnowledge) return null;
            var recent = newTopic ? "" : string.Join("\n", earlier.TakeLast(2).Append(text));
            var result = await features.RetrieveAsync(WorktreePath ?? _projectPath, text, recent, new HashSet<string>(_deliveredContext),
                _needsResumeContext ? null : FeatureId).WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            if (newTopic) result = result with { NewTopic = true };
            if (!ReferenceEquals(session, _session)) throw new OperationCanceledException();
            _retrievalId = Guid.NewGuid().ToString("N");
            if (FeatureId is { } id) features.RecordRetrieval(_retrievalId, id, result);
            CrashLog.Trace($"Project retrieval ({CliId}, {result.Method}): {result.ElapsedMilliseconds}ms, {result.Files.Count} files, " +
                $"{result.Snippets} excerpts, {result.Findings} findings, {result.Keys.Count} keys, byte upper bound={result.Tokens}");
            CrashLog.Debug("knowledge", $"retrieval={_retrievalId} session={FeatureId ?? "none"} " +
                $"files={System.Text.Json.JsonSerializer.Serialize(result.Files)} refs={System.Text.Json.JsonSerializer.Serialize(result.Keys)}");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CrashLog.Warn("knowledge", $"Context retrieval skipped: {ex.Message}");
            return null;
        }
        finally { _briefing = null; }
    }

    /// <summary>A row under the prompt saying what project context went with it, also kept in the session's log.</summary>
    private void ShowContextEnrichment(RetrievalResult? result)
    {
        if (result is not { Text.Length: > 0 }) return;
        var item = ContextEnrichedItem.From(result);
        Add(item);
        if (_recording)
        {
            try { _featureLog?.AppendContext(item.Summary, item.Details); }
            catch (IOException ex) { CrashLog.Warn("knowledge", $"Context entry not logged: {ex.Message}"); }
        }
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
        catch (Exception ex) { CrashLog.Warn("knowledge", $"Context delivery metadata not saved: {ex.Message}"); }
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
        catch (Exception ex) { CrashLog.Warn("knowledge", $"Context compaction metadata not cleared: {ex.Message}"); }
    }

    private void AbandonFeatureTurn()
    {
        if (IsBusy) EndFeatureTurn("stopped");
    }
}
