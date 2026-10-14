using System.Text;

using Codale.Agents.History;
using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Core.Features;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// The chat's feature: which big area of the project its conversation is work on. Each message is
/// routed to a feature (or to none, when it is no project work); project work is appended to the
/// feature's log, and a session that changes feature starts fresh from that feature's summary
/// rather than loading any earlier message.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>Events kept for a turn that is not (yet) project work: enough to save it if it turns out to write files.</summary>
    private const int SkipBufferLimit = 400;

    public FeatureService? Features { get; init; }

    /// <summary>The feature this chat's conversation belongs to; null until a message is routed to one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeature))]
    public partial string? FeatureId { get; set; }

    [ObservableProperty]
    public partial string FeatureName { get; set; } = "";

    public bool HasFeature => FeatureId is not null;

    private FeatureLog? _featureLog;

    /// <summary>The current turn is project work and goes into the feature's log.</summary>
    private bool _recording;

    /// <summary>The feature whose summary the next session's first message carries.</summary>
    private string? _seedFeatureId;

    private bool _attachLogged;

    private readonly List<AgentEvent> _skipBuffer = [];
    private string? _skipPrompt;

    private bool SmartHistory => Features is not null && AppSettings.SmartHistory;

    /// <summary>
    /// Acts on the router's verdict before the message goes out: a change of feature restarts the
    /// session on the new one. False when the session could not be restarted - the message then stays in the composer.
    /// </summary>
    public async Task<bool> ApplyFeatureDecisionAsync(FeatureDecision decision, string text)
    {
        if (Features is not { } features)
        {
            return true;
        }

        switch (decision.Action)
        {
            case FeatureAction.Skip:
                _recording = false;
                return true;

            case FeatureAction.Switch when decision.FeatureId is { } id && features.Get(id) is { } target:
                return await SwitchFeatureAsync(target, created: false);

            case FeatureAction.Create when decision.NewNames is { Count: > 0 } names:
                var created = names
                    .Select(name => features.Create(name, FeatureRouting.DescribeFrom(text)))
                    .ToList();
                return await SwitchFeatureAsync(created[0], created: true);

            default:
                // No model verdict (none installed, a slow or unparsable reply) and no feature yet: check
                // whether an existing feature in this project matches the request before creating a new one.
                if (FeatureId is null && IsRequest(text))
                {
                    var matches = features.Search(text, 1);
                    if (matches.Count > 0)
                    {
                        return await SwitchFeatureAsync(matches[0], created: false);
                    }

                    if (features.Create(
                            FeatureRouting.CleanNames(null, text, seed: false)[0], FeatureRouting.DescribeFrom(text)) is { } started)
                    {
                        SetFeature(started, seed: false);
                        Title = started.Name;
                    }
                }

                _recording = FeatureId is not null;
                return true;
        }
    }

    /// <summary>Long enough to be a request rather than a go-ahead or a greeting.</summary>
    private static bool IsRequest(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 3;

    /// <summary>Puts the chat on a feature: a fresh session whose first message carries the feature's summary.</summary>
    public async Task<bool> AttachFeatureAsync(FeatureInfo feature) => await SwitchFeatureAsync(feature, created: false, announce: false);

    private async Task<bool> SwitchFeatureAsync(FeatureInfo feature, bool created, bool announce = true)
    {
        if (Features is not { } features)
        {
            return true;
        }

        var previous = FeatureId is { } id ? features.Get(id) : null;
        if (previous is not null)
        {
            features.Summarize(previous.Id, force: true);
        }

        // A chat that has said nothing yet needs no restart: the briefing rides on its first message.
        // One that has is left behind - its session and transcript go; an isolated chat keeps its worktree.
        var hadConversation = HasConversation;
        if (hadConversation)
        {
            var worktree = WorktreePath;
            await DisposeSessionAsync();
            ResetForNewSession();
            WorktreePath = worktree;
        }

        SetFeature(feature, seed: true);
        Title = feature.Name;

        if (hadConversation)
        {
            await ConnectAsync();
            if (!IsConnected)
            {
                return false;
            }
        }

        if (announce && (hadConversation || !created))
        {
            Add(new NoticeItem
            {
                Text = created ? $"New history: {feature.Name}" : $"Continuing history: {feature.Name}",
                ActionText = previous is null ? null : $"Back to {previous.Name}",
                Action = previous is null ? null : () => AttachFeatureAsync(previous),
            });
        }

        return true;
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

                case ToolCallStarted tool:
                    text.AppendLine($"  · {tool.ToolName}");
                    break;
            }
        }

        return (text.ToString().Trim(), hasMore);
    }

    private void SetFeature(FeatureInfo feature, bool seed)
    {
        FeatureId = feature.Id;
        FeatureName = feature.Name;
        _featureLog = Features!.LogOf(feature.Id);
        _recording = true;
        _attachLogged = false;
        _seedFeatureId = seed ? feature.Id : null;
        _skipBuffer.Clear();
        _skipPrompt = null;
    }

    private void ClearFeature()
    {
        FeatureId = null;
        FeatureName = "";
        _featureLog = null;
        _recording = false;
        _attachLogged = false;
        _seedFeatureId = null;
        _skipBuffer.Clear();
        _skipPrompt = null;
    }

    /// <summary>The user's turn: appended to the feature's log when it is project work, else held back in case it turns into some.</summary>
    private void RecordUserTurn(string text)
    {
        if (!SmartHistory)
        {
            return;
        }

        if (_recording && _featureLog is { } log && FeatureId is { } id)
        {
            AttachLog(log);
            log.AppendUser(text);
            Features!.RecordProgress(id, log, text);
            return;
        }

        _recording = false;
        _skipBuffer.Clear();
        _skipPrompt = text;
    }

    private void RecordFeatureEvent(AgentEvent e)
    {
        if (!SmartHistory)
        {
            return;
        }

        if (e is TurnCompleted)
        {
            if (_recording && FeatureId is { } id)
            {
                Features!.Summarize(id);
            }

            return;
        }

        if (_recording && _featureLog is { } log)
        {
            log.Append(e);
            return;
        }

        if (_skipBuffer.Count < SkipBufferLimit)
        {
            _skipBuffer.Add(e);
        }

        // A turn that was no project work but wrote files was work after all.
        if (e is ToolCallCompleted { FileChange: not null } && _skipPrompt is { } prompt)
        {
            PromoteSkippedTurn(prompt);
        }
    }

    private void PromoteSkippedTurn(string prompt)
    {
        var features = Features!;
        var feature = FeatureId is { } id ? features.Get(id) : null;
        feature ??= features.Create(FeatureRouting.CleanNames(null, prompt, seed: false)[0], FeatureRouting.DescribeFrom(prompt));

        FeatureId = feature.Id;
        FeatureName = feature.Name;
        _featureLog = features.LogOf(feature.Id);
        _recording = true;

        AttachLog(_featureLog);
        _featureLog.AppendUser(prompt);
        foreach (var held in _skipBuffer)
        {
            _featureLog.Append(held);
        }

        features.RecordProgress(feature.Id, _featureLog, prompt);
        _skipBuffer.Clear();
        _skipPrompt = null;
    }

    private void AttachLog(FeatureLog log)
    {
        if (!_attachLogged)
        {
            log.Attach(CliId);
            _attachLogged = true;
        }
    }

    /// <summary>A file the agent wrote is indexed under the feature it was written for.</summary>
    private void IndexWrittenFile(string path)
    {
        if (SmartHistory && _recording && FeatureId is { } id)
        {
            Features!.IndexFile(id, path);
        }
    }

    /// <summary>
    /// The feature's briefing for a session's first message, once: its summary, the progress
    /// since, and the earlier code that bears on the message. Null for a feature with no history yet.
    /// </summary>
    private string? TakeFeatureSeed(string text)
    {
        if (_seedFeatureId is not { } id || Features?.Get(id) is not { } feature)
        {
            return null;
        }

        _seedFeatureId = null;
        return feature.Summary.Length > 0 || feature.LogBytes > 0 || Features.FileCount(id) > 0
            ? Features.BuildSeed(feature, text)
            : null;
    }
}
