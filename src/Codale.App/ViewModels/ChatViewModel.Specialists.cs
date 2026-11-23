using Codale.Agents;
using Codale.Core.Agents;
using Codale.Core.Helper;
using System.Collections.ObjectModel;

namespace Codale.App.ViewModels;

public sealed partial class ChatViewModel
{
    private AgentSessionRequest? _connectedRequest;
    public ObservableCollection<SpecialistItem> Specialists { get; } = [];
    private ObservableCollection<SpecialistItem> _specialists => Specialists;
    public event EventHandler<SpecialistItem>? SpecialistDetailsRequested;
    public string AutoRoutingDetails => _autoAssessment is not { } a ? "No Auto Mode decision yet." :
        $"Source: {a.Source}\nTopic: {a.Topic}\nIntent: {a.Kind}\nDifficulty: {(a.Hard ? "Hard" : "Easy")}\n" +
        $"Needs planning: {a.NeedsPlan}\nUnclear instructions: {a.Unclear}\nConfiguration / preference: {a.Configuration}";
    private SpecialistItem? _answerSpecialist;
    private string? _restoredSpecialistsFor;
    private bool HasRunningSpecialist => _specialists.Any(s => s.Session.IsRunning);

    private void AddRedirectedAttachments(IReadOnlyList<TurnAttachment> attachments)
    {
        foreach (var a in attachments)
            if (!Attachments.Any(existing => existing.Path == a.Path))
                Attachments.Add(new ComposerAttachment { Kind = a.Kind, Name = a.Name, Path = a.Path });
    }

    private async Task StartSpecialistAsync(string text, IReadOnlyList<TurnAttachment> attachments, RouteIntent intent, CancellationToken ct)
    {
        if (_connectedRequest is null) return;
        _preShown = null;
        RecordUserTurn(text);
        var routingTurn = _activeTurn;
        EndTurn(TurnOutcome.Completed);
        if (routingTurn is not null) { Items.Remove(routingTurn); Reflow(); }
        IsBusy = false;
        var planner = intent == RouteIntent.Plan;
        var item = planner ? _specialists.LastOrDefault(s => s.Session.Snapshot.Planner && !s.Session.Snapshot.HandedOff &&
            s.Session.Snapshot.ParentId == FeatureId && !s.Status.StartsWith("Stopped"))
            : _autoAssessment?.Topic == "current" ? _answerSpecialist : null;
        if (item is null || item.Session.Snapshot.HandedOff || item.Session.Snapshot.CliId != CliId || item.Session.Snapshot.Endpoint != EndpointName)
        {
            var (model, effort) = TierConfig(planner ? "plus" : "lite");
            if (Status.IsCustomProvider) model = _smart.Settings.FindByok(EndpointName)?.ResolveEndpointModel(model);
            var snapshot = new SpecialistSnapshot
            {
                ParentId = FeatureId ?? _chatId, Planner = planner, OriginalPrompt = text, CliId = CliId, Endpoint = EndpointName,
                Model = model, Effort = effort, Attachments = attachments,
                Briefing = $"Project: {ProjectPath}\nCurrent session: {FeatureName}\n" +
                    "The parent conversation below already covers what was read and found in the workspace: build on it, " +
                    "and inspect only the files you still need instead of rescanning the project.\n\n" +
                    $"Parent conversation:\n{ParentTranscript()}\n{RecallMemory(text)}",
            };
            item = CreateSpecialist(snapshot);
            _specialists.Add(item);
            if (!planner) _answerSpecialist = item;
        }
        Add(new SpecialistHistoryItem(item));
        SpecialistDetailsRequested?.Invoke(this, item);
        PersistSpecialists();
        await item.Session.SendAsync(text, attachments, ct);
        item.Refresh();
        PersistSpecialists();
        NotifySpecialists();
    }

    /// <summary>The recent parent conversation, user and assistant turns, newest kept when over budget.</summary>
    private string ParentTranscript(int maxChars = 24000, int perTurn = 4000)
    {
        var turns = Items.Where(i => !ReferenceEquals(i, _preShown?.Item)).Select(i => i switch
        {
            UserMessageItem user => "user: " + user.Text,
            AssistantMessageItem { IsStreaming: false } answer => "assistant: " + answer.Text,
            SpecialistHistoryItem child => child.Specialist.Session.Snapshot.Messages.LastOrDefault(m => m.Role == "assistant")?.Text is { } t
                ? $"{(child.Specialist.Session.Snapshot.Planner ? "planner" : "specialist")}: {t}" : null,
            _ => null,
        }).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Length > perTurn ? t[..perTurn] + " …" : t!).ToList();
        var kept = new List<string>();
        var total = 0;
        for (var i = turns.Count - 1; i >= 0 && kept.Count < 16 && total + turns[i].Length <= maxChars; i--)
        {
            kept.Insert(0, turns[i]);
            total += turns[i].Length;
        }
        return string.Join("\n\n", kept);
    }

    private SpecialistItem CreateSpecialist(SpecialistSnapshot snapshot)
    {
        var session = new SpecialistSession(_agentSessions, _connectedRequest! with
        {
            // Only memory tools are useful to a read-only child; terminal, command, browser mutation and secret tools are excluded.
            McpServers = _connectedRequest!.McpServers.Where(s => s.Name == "codale-memory").ToArray(),
            SystemPromptAppend = "Recall project preferences with codale-memory memory_search. Do not use parent task or artifact tools. " +
                "Saved notes are context; current user instructions take precedence.",
        }, snapshot);
        SpecialistItem? item = null;
        item = new SpecialistItem
        {
            Session = session,
            Send = SendSpecialistInputAsync,
            Approve = child => ApproveSpecialistAsync(child),
            Continue = ContinueSpecialistAsync,
            Skip = SkipSpecialistAsync,
            ShowDetails = child => SpecialistDetailsRequested?.Invoke(this, child),
            Retry = async child =>
            {
                await child.Session.SendAsync("Continue the interrupted request. Inspect the earlier conversation and answer the outstanding questions or finish the response.", child.Session.Snapshot.Attachments);
            },
        };
        var wasRunning = false;
        session.Changed += () =>
        {
            var finished = wasRunning && !session.IsRunning;
            wasRunning = session.IsRunning;
            item.Refresh();
            PersistSpecialists();
            NotifySpecialists();
            if (finished)
            {
                var state = session.Snapshot;
                EndFeatureTurn(state.Status);
                AttentionNeeded?.Invoke(this, new AttentionRequest(state.Status.StartsWith("Failed") ? AttentionKind.TurnFailed : state.Plan is not null ? AttentionKind.Plan : AttentionKind.TurnFinished,
                    state.Status.StartsWith("Failed") ? state.Status : state.Plan is not null ? "The planner has a plan for review."
                    : state.Status.StartsWith("Stopped") ? "The specialist was stopped." : "The specialist response is ready."));
            }
        };
        item.Refresh();
        return item;
    }

    private void NotifySpecialists()
    {
        OnPropertyChanged(nameof(ShowStop));
        OnPropertyChanged(nameof(IsWaitingForYou));
        OnPropertyChanged(nameof(IsWorkingOnItsOwn));
        OnPropertyChanged(nameof(WaitingStatusMessage));
        OnPropertyChanged(nameof(ShowStatusLine));
        OnPropertyChanged(nameof(IsBusyWorking));
        OnPropertyChanged(nameof(WorkingStatusMessage));
        PublishFeatureActivity(HasRunningSpecialist || IsBusy);
    }

    private string SpecialistContext() => string.Join("\n\n", _specialists.TakeLast(2).Select(child =>
    {
        var s = child.Session.Snapshot;
        var text = string.Join("\n", s.Messages.TakeLast(4).Select(m => $"{m.Role}: {m.Text}"));
        return $"Inline {(s.Planner ? "planner" : "answer")} conversation ({s.Id}):\n" + text[..Math.Min(text.Length, 8000)];
    }));

    private async Task<bool> TryApplyAutoConfigurationAsync(string text, IReadOnlyList<TurnAttachment> attachments)
    {
        if (attachments.Count > 0 || AutoConfiguration.Tier(text) is not { } tier) return false;
        _preShown = null;
        RecordUserTurn(text);
        EndTurn(TurnOutcome.Completed);
        await ApplyTierAsync(tier);
        var answer = $"Configured this chat to use the {TierTitle(tier)} tier.";
        Add(new AssistantMessageItem { Text = answer });
        _featureLog?.Append(new AssistantMessageCompleted { MessageId = Guid.NewGuid().ToString("N"), Model = "Codale", Text = answer });
        EndFeatureTurn("configuration applied");
        return true;
    }

    private async Task SendSpecialistInputAsync(SpecialistItem item)
    {
        try
        {
            if (item.HasQuestions)
            {
                // A typed reply fills the questions left unanswered; with everything answered it rides along as a note.
                var reply = item.Input.Trim();
                if (reply.Length == 0 && item.Questions.Any(q => !q.IsAnswered)) return;
                var last = item.Questions[^1];
                var answers = item.Questions.ToDictionary(q => q.Id, q => q.IsAnswered
                    ? q.Answer + (reply.Length > 0 && ReferenceEquals(q, last) && item.Questions.All(o => o.IsAnswered) ? "\nNote: " + reply : "")
                    : item.Questions.Count == 1 ? reply : "The user's combined reply to the questions: " + reply);
                await item.Session.AnswerAsync(answers);
            }
            else
            {
                var input = item.Input.Trim();
                // Whatever is typed here belongs to this pane's conversation - follow-ups and extra
                // instructions alike. The reader chose this composer over the main one; guessing
                // "this sounds like an action" and sending it to the main chat acted on words meant
                // as context. The answer specialist is read-only and says so when asked to act.
                if (input.Length == 0) return;
                if (item.IsRevising && item.Session.Snapshot.Plan is not null) input = "Revise the plan: " + input;
                await item.Session.SendAsync(input);
            }
            item.Input = "";
        }
        catch (Exception e) { ComposerNotice = new NoticeItem { Text = e.Message, Severity = NoticeSeverity.Warning }; }
        PersistSpecialists();
    }

    /// <summary>Auto Mode's "Continue": submit the interview (blanks become defaults), or hand the ready plan to the parent.</summary>
    private async Task ContinueSpecialistAsync(SpecialistItem item)
    {
        try
        {
            if (item.HasQuestions)
            {
                var reply = item.Input.Trim();
                await item.Session.AnswerAsync(item.Questions.ToDictionary(q => q.Id, q => q.IsAnswered ? q.Answer
                    : reply.Length > 0 ? reply : "No preference. Choose a sensible default and note it in the plan."));
                item.Input = "";
                PersistSpecialists();
            }
            else if (item.Session.Snapshot.Plan is not null) await ApproveSpecialistAsync(item, requireInterview: false);
        }
        catch (Exception e) { ComposerNotice = new NoticeItem { Text = e.Message, Severity = NoticeSeverity.Warning }; }
    }

    /// <summary>Auto Mode's "Skip": stop and close the planner. Nothing is sent to the main chat; the conversation stays in history.</summary>
    private async Task SkipSpecialistAsync(SpecialistItem item)
    {
        if (item.Session.Snapshot.HandedOff) return;
        try
        {
            await item.Session.SkipAsync();
            PersistSpecialists();
            NotifySpecialists();
        }
        catch (Exception e) { ComposerNotice = new NoticeItem { Text = "Could not skip planning: " + e.Message, Severity = NoticeSeverity.Warning }; }
    }

    private async Task ApproveSpecialistAsync(SpecialistItem item, bool requireInterview = true)
    {
        if (IsBusy || HasRunningSpecialist || _session is null || !IsConnected) return;
        var state = item.Session.Snapshot;
        if (state.ParentId != FeatureId || state.CliId != CliId || state.Endpoint != EndpointName)
        {
            ComposerNotice = new NoticeItem { Text = "Reopen this plan in its original CLI and endpoint before approval.", Severity = NoticeSeverity.Warning };
            return;
        }
        if (!item.Session.ClaimPlan(state.PlanVersion, requireInterview)) return;
        try
        {
            if (Features is null || !Features.Data.Config.TrySetIfAbsent($"auto-approved-{state.Id}-{state.PlanVersion}", DateTimeOffset.UtcNow.ToString("O")))
            {
                ComposerNotice = new NoticeItem { Text = "This plan version was already handed to its parent.", Severity = NoticeSeverity.Info };
                return;
            }
        }
        catch (Exception e)
        {
            item.Session.ReleasePlanClaim();
            ComposerNotice = new NoticeItem { Text = "Could not record plan approval: " + e.Message, Severity = NoticeSeverity.Warning };
            return;
        }
        PersistSpecialists(); // Claim before any asynchronous work; double clicks and reopened views cannot launch it twice.
        using var handoff = new CancellationTokenSource();
        _briefing = handoff; // Reserve the parent while the child's final process disposal completes.
        try
        {
            await item.Session.StopAsync();
            var prompt = $"Implement this approved plan (planner {state.Id}, version {state.PlanVersion}).\n\n" +
                $"Original request:\n{state.OriginalPrompt}\n\nApproved plan:\n{state.Plan}\n\n" +
                "The user approved this plan. Follow its agreed requirements and constraints; implement and validate it.";
            // The agent receives the plan; the chat and its history show a short line under the plan's link instead of echoing it.
            await SendTurnAsync(prompt, state.Attachments, planOnly: false, askOnly: false, handoff.Token, displayText: ApprovedPlanPrompt);
        }
        finally { if (ReferenceEquals(_briefing, handoff)) _briefing = null; }
    }

    private const string ApprovedPlanPrompt = "Implement the approved plan";

    private void PersistSpecialists()
    {
        if (FeatureId is not { } id || Features is null) return;
        try { Features.Data.Config.SetJson("auto-specialists-" + id, _specialists.Select(s => s.Session.Snapshot).Where(s => s.ParentId == id).ToArray()); }
        catch (Exception e) { ComposerNotice = new NoticeItem { Text = "Could not save child session history: " + e.Message, Severity = NoticeSeverity.Warning }; }
    }

    private void RestoreSpecialists()
    {
        if (FeatureId is not { } id || Features is null || _connectedRequest is null || _restoredSpecialistsFor == id) return;
        _restoredSpecialistsFor = id;
        try
        {
            foreach (var state in Features.Data.Config.GetJson<List<SpecialistSnapshot>>("auto-specialists-" + id) ?? [])
            {
                if (_specialists.Any(s => s.Session.Snapshot.Id == state.Id)) continue;
                var restored = state with { Status = state.Skipped ? "Skipped" : state.HandedOff ? "Approved · sent to parent" : state.Plan is not null
                    ? state.InterviewAnswered ? "Plan ready for review" : "Interview confirmation required before approval"
                    : state.Questions.Count > 0 ? "Waiting for answers" : state.Status is "Starting" or "Working" ? "Stopped · resume available" : state.Status };
                var item = CreateSpecialist(restored);
                _specialists.Add(item);
                // Earlier UI versions logged child prose in the parent. Replace it with a compact review entry.
                var childReplies = state.Messages.Where(m => m.Role == "assistant").Select(m => m.Text).Append(state.Plan ?? "").ToHashSet();
                foreach (var reply in Items.OfType<AssistantMessageItem>().Where(m => childReplies.Contains(m.Text)).ToArray()) Items.Remove(reply);
                // The link goes back where it was made: right after the prompt that started the planner or answer.
                var prompt = Items.OfType<UserMessageItem>().LastOrDefault(m => m.Text == state.OriginalPrompt);
                var link = new SpecialistHistoryItem(item);
                if (prompt is not null) Items.Insert(Items.IndexOf(prompt) + 1, link);
                else Add(link);
                if (!state.Planner) _answerSpecialist = item;
            }
        }
        catch (Exception e) { ComposerNotice = new NoticeItem { Text = "Could not restore child sessions: " + e.Message, Severity = NoticeSeverity.Warning }; }
        Reflow();
    }

    private async Task StopSpecialistsAsync()
    {
        foreach (var item in _specialists.Where(s => s.Session.IsRunning).ToArray()) await item.Session.StopAsync();
        PersistSpecialists();
    }
}
