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
                // Another chat may have started the same topic a moment ago: that feature is joined, not doubled.
                var created = names
                    .Select(name => features.FindOrCreate(name, FeatureRouting.DescribeFrom(text)))
                    .ToList();
                return await SwitchFeatureAsync(created[0].Feature, created: created[0].Created);

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

                    var (started, isNew) = features.FindOrCreate(
                        FeatureRouting.CleanNames(null, text, seed: false)[0], FeatureRouting.DescribeFrom(text));
                    if (!isNew)
                    {
                        // Another chat just started this topic: join it with its briefing like any existing feature.
                        return await SwitchFeatureAsync(started, created: false);
                    }

                    SetFeature(started, seed: false);
                    Title = started.Name;
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
        // The previous task list belongs to a finished prompt: showing it as current leaves
        // unfinished steps hanging and hides the list the agent makes for the new one.
        ShowTodos([]);
        RestorePendingPlan();

        if (hadConversation)
        {
            await ConnectAsync();
            if (!IsConnected)
            {
                return false;
            }
        }

        // A chat that never spoke keeps its transcript, so the briefing of the feature it left goes.
        foreach (var stale in Items.Where(i => i is FeatureBriefingItem or HandoffBriefItem).ToList())
        {
            Items.Remove(stale);
            Reflow();
        }

        if (!created)
        {
            // The transcript opens on what the session carries over: the feature and its summary.
            Add(new FeatureBriefingItem
            {
                FeatureName = feature.Name,
                Summary = FeatureSummary(),
                BackText = announce && previous is not null ? $"Back to {previous.Name}" : null,
                BackAction = announce && previous is not null ? () => AttachFeatureAsync(previous) : null,
            });
        }
        else if (announce && hadConversation)
        {
            Add(new NoticeItem
            {
                Text = $"New history: {feature.Name}",
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

    private void SetFeature(FeatureInfo feature, bool seed)
    {
        FeatureId = feature.Id;
        FeatureName = feature.Name;
        _featureLog = Features!.LogOf(feature.Id, _chatId);
        _logSeen = _featureLog.Position;
        _recording = true;
        _attachLogged = false;
        _seedFeatureId = seed ? feature.Id : null;
        _skipBuffer.Clear();
        _skipPrompt = null;
        PublishFeatureActivity(busy: false);
    }

    private void ClearFeature()
    {
        Features?.ClearChatActivity(_chatId);
        FeatureId = null;
        FeatureName = "";
        _featureLog = null;
        _recording = false;
        _attachLogged = false;
        _seedFeatureId = null;
        _skipBuffer.Clear();
        _skipPrompt = null;
    }

    /// <summary>This chat's id in feature logs and in the list of chats open on a feature.</summary>
    private readonly string _chatId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Where this chat last caught up with its feature's log: other chats' records past it are news.</summary>
    private LogPosition _logSeen;

    /// <summary>What this chat last asked on its feature, for the other chats' notes.</summary>
    private string? _featureRequest;

    /// <summary>Tells the other chats on this feature that this one is here, and whether it is mid-turn.</summary>
    private void PublishFeatureActivity(bool busy)
    {
        if (FeatureId is { } id && Features is { } features)
        {
            features.SetChatActivity(new FeatureChat(_chatId, id, Title ?? FeatureName, busy, _featureRequest));
        }
    }

    /// <summary>
    /// The turn is over: its end goes into the feature's log, so readers can tell finished work from
    /// work under way, and the other chats stop seeing this one as busy.
    /// </summary>
    private void EndFeatureTurn(string outcome)
    {
        if (SmartHistory && _recording && _featureLog is { } log)
        {
            log.EndTurn(outcome);
        }

        PublishFeatureActivity(busy: false);
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
            _featureRequest = text;
            PublishFeatureActivity(busy: true);
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

        if (e is TurnCompleted done)
        {
            EndFeatureTurn(done.Subtype);
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
        feature ??= features.FindOrCreate(FeatureRouting.CleanNames(null, prompt, seed: false)[0], FeatureRouting.DescribeFrom(prompt)).Feature;

        if (FeatureId != feature.Id)
        {
            _logSeen = features.LogOf(feature.Id).Position;
        }

        FeatureId = feature.Id;
        FeatureName = feature.Name;
        _featureLog = features.LogOf(feature.Id, _chatId);
        _recording = true;
        _featureRequest = prompt;
        PublishFeatureActivity(busy: true);

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

    /// <summary>The pre-brief in progress, which Stop cancels; null while none runs.</summary>
    private CancellationTokenSource? _briefing;

    /// <summary>
    /// The feature's briefing for a session's first message, once. With the background-task
    /// model it is a brief written for this message - the history read and the code explored by
    /// the cheap model, shown as its own transcript section - so the chat model starts from a few
    /// thousand characters rather than the raw log. Without one it is the summary, the progress
    /// since and the earlier code that bears on the message. Null for a feature with no history yet.
    /// </summary>
    /// <exception cref="OperationCanceledException">Stopped while the brief was being written; the seed stays due.</exception>
    private async Task<string?> TakeFeatureSeedAsync(string text)
    {
        if (_seedFeatureId is not { } id || Features?.Get(id) is not { } feature)
        {
            return null;
        }

        // The seed covers the log as it stands now: other chats' work after this point is news for the next catch-up.
        var seen = _featureLog?.Position ?? _logSeen;
        var others = Features.OtherChatsOn(id, _chatId);
        if (feature.Summary.Length == 0 && feature.LogBytes == 0 && Features.FileCount(id) == 0 && others.Count == 0)
        {
            _seedFeatureId = null;
            _logSeen = seen;
            return null;
        }

        if (Helper?.IsAvailable != true)
        {
            _seedFeatureId = null;
            _logSeen = seen;
            return Features.BuildSeed(feature, text, chatId: _chatId);
        }

        var item = new HandoffBriefItem { FeatureName = feature.Name };
        Add(item);

        using var cts = new CancellationTokenSource();
        _briefing = cts;
        try
        {
            var stage = new Progress<string>(s => item.Stage = s);
            var result = await Task.Run(() => Features.PrepareBriefAsync(feature, text, _chatId, stage, cts.Token), cts.Token);
            _seedFeatureId = null;
            _logSeen = seen;
            item.Finish(result);
            CrashLog.Trace($"Feature brief for {id}: {result.FullChars} chars of history handed off as {result.Seed.Length} (fallback={result.IsFallback})");
            return result.Seed;
        }
        catch (OperationCanceledException)
        {
            Items.Remove(item);
            Reflow();
            throw;
        }
        finally
        {
            _briefing = null;
        }
    }

    /// <summary>
    /// For a chat already in a session on its feature: what other chats did on it since this chat's
    /// last message, as a note its next message carries - so it neither undoes nor redoes their work.
    /// Null when nothing happened elsewhere, which is the usual case and costs one read of the log tail.
    /// </summary>
    /// <exception cref="OperationCanceledException">Stopped while the catch-up was being written; nothing is marked seen.</exception>
    private async Task<string?> TakeCatchUpAsync(string text)
    {
        if (!SmartHistory || !_recording || _seedFeatureId is not null || FeatureId is not { } id || _featureLog is not { } log)
        {
            return null;
        }

        var seen = log.Position;
        var since = _logSeen;
        if (!FeatureCatchUp.HasWork(log.ReadSince(since, excludeChat: _chatId).Entries))
        {
            _logSeen = seen;
            return null;
        }

        var item = new HandoffBriefItem { FeatureName = FeatureName, Heading = "Caught up on other chats", Stage = "Reading other chats' work…" };
        Add(item);

        using var cts = new CancellationTokenSource();
        _briefing = cts;
        try
        {
            var result = await Task.Run(() => Features!.CatchUpAsync(id, _chatId, since, text, cts.Token), cts.Token);
            _logSeen = seen;
            if (result is null)
            {
                Items.Remove(item);
                Reflow();
                return null;
            }

            item.FinishCatchUp(result);
            CrashLog.Trace($"Feature catch-up for {id}: {result.FullChars} chars of other chats' work handed over as {result.Note.Length}");
            return result.Note;
        }
        catch (OperationCanceledException)
        {
            Items.Remove(item);
            Reflow();
            throw;
        }
        finally
        {
            _briefing = null;
        }
    }

    /// <summary>The session goes mid-turn (the tab closes, a restart): no turn end will come, so one is written for it.</summary>
    private void AbandonFeatureTurn()
    {
        if (IsBusy)
        {
            EndFeatureTurn("stopped");
        }
    }
}