using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.App.ViewModels;

/// <summary>A prompt Auto mode found belongs elsewhere: an existing session (<see cref="FeatureId"/>) or a new one.</summary>
public sealed record TopicRedirect(string Text, string? FeatureId, string? FeatureName, string? NewName);

/// <summary>
/// The auto mode enhancer: on the Decision model (General when Decision is off) it picks Ask, Plan or Act for the turn,
/// and when the conversation seems to change topic it checks whether the prompt belongs to another session.
/// </summary>
public sealed partial class ChatViewModel
{
    private const int MaxRoutedSessions = 40;

    /// <summary>The user chose where a redirected prompt goes; the workspace opens or creates that session and sends it.</summary>
    public event EventHandler<TopicRedirect>? TopicRedirectRequested;

    /// <summary>A prompt the user decided to keep in this session: it is not asked about again.</summary>
    private string? _keepHere;

    /// <summary>
    /// The turn's mode, and where the prompt belongs when it looks like another topic. The local rules settle most
    /// prompts; the model is asked only when they are unsure of the mode or see a topic change, and its answer is
    /// checked against them. Slow answers fall back to the local rules.
    /// </summary>
    private async Task<(RouteDecision Decision, TopicRedirect? Redirect)> RouteSmartAsync(
        string text, bool hasAttachments, UserMessageItem shown, CancellationToken ct, DateTimeOffset deadline)
    {
        var local = MessageRouting.Assess(text);
        var fallback = MessageRouting.RouteWithoutModel(text);
        if (!AppSettings.SmartFeatureEnabled("routing")) return (fallback, null);

        var earlier = Items.OfType<UserMessageItem>().Where(i => !ReferenceEquals(i, shown) && i.Text.Length > 0)
            .TakeLast(3).Select(i => i.Text).ToList();
        var keep = _keepHere == text;
        _keepHere = null;
        var canRedirect = !keep && !hasAttachments && FeatureId is not null && Features is not null && earlier.Count > 0;

        // The Decision model answers in about a second, so every prompt with a subject of its own is checked
        // against the project's sessions; its answer is trusted only when it is sure.
        if (SmartDecider.IsAvailable)
        {
            var askSession = canRedirect && SearchText.HasTopic(text) && !MessageRouting.IsFollowUp(text);
            if (local.Confident && !askSession) return (fallback, null);
            var decided = await DecideRouteAsync(text, local, fallback, askSession, ct);
            if (decided is { } answer) return answer;
            if (local.Confident) return (fallback, null);
        }

        var shifted = canRedirect && TopicShift.IsNewTopic(text, earlier, LastRoutingReply)
            && AppSettings.SmartRoleEnabled(SmartFeatureSettings.General);
        if (local.Confident && !shifted) return (fallback, null);
        if (!AppSettings.SmartRoleEnabled(SmartFeatureSettings.General) || SmartExperienceHost.Instance.Status.State != Codale.Smart.SmartState.Ready)
            return (fallback, null);

        ComposerNotice = new NoticeItem { Text = "Understanding your request…", Severity = NoticeSeverity.Info };
        try
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(TimeSpan.FromSeconds(shifted ? 6 : 4));
            if (!shifted)
            {
                var intent = await SmartExperienceHost.Instance.ClassifyAsync(new(_projectPath, _projectPath, "routing", _chatId, deadline),
                    text, LastRoutingReply, quick.Token).WaitAsync(quick.Token);
                return intent?.Value is { } selected ? (MessageRouting.Reconcile(local, selected switch
                {
                    Codale.Smart.SmartIntent.Ask => RouteIntent.Ask,
                    Codale.Smart.SmartIntent.Plan => RouteIntent.Plan,
                    _ => RouteIntent.Act,
                }), null) : (fallback, null);
            }

            // Recent sessions only: the list is the model's prompt, and a far-off session is rarely the right one.
            var sessions = Features!.List().OrderByDescending(f => f.Id == FeatureId).ThenByDescending(f => f.UpdatedAt)
                .Take(MaxRoutedSessions).ToList();
            var helper = new SmartHelperModel(_projectPath, "routing", TimeSpan.FromSeconds(6));
            var call = await helper.CallToolAsync(FeatureRouting.SystemPrompt,
                FeatureRouting.BuildConversation(text, sessions, FeatureId, LastRoutingReply), [FeatureRouting.RouteTool(sessions)], quick.Token);
            if (call is null) return (fallback, null);
            var routed = FeatureRouting.Interpret(call, text, sessions, FeatureId);
            var decision = MessageRouting.Reconcile(local, routed.Route.Intent);
            TopicRedirect? redirect = routed.Action switch
            {
                FeatureAction.Switch when sessions.FirstOrDefault(s => s.Id == routed.FeatureId) is { } target =>
                    new(text, target.Id, target.Name, null),
                FeatureAction.Create => new(text, null, null, routed.NewNames?.FirstOrDefault()),
                _ => null,
            };
            return (decision, redirect);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (fallback, null); }
        catch (HelperModelException) { return (fallback, null); }
        finally
        {
            if (ComposerNotice?.Text == "Understanding your request…") ComposerNotice = null;
        }
    }

    /// <summary>
    /// One request to the Decision model: the mode (when the local rules are unsure) and the session (when asked).
    /// Null when the request failed, so the caller falls back to General or the local rules.
    /// </summary>
    private async Task<(RouteDecision Decision, TopicRedirect? Redirect)?> DecideRouteAsync(string text, RouteDecision local,
        RouteDecision fallback, bool askSession, CancellationToken ct)
    {
        var sessions = askSession ? Features!.List().OrderByDescending(f => f.Id == FeatureId).ThenByDescending(f => f.UpdatedAt)
            .Take(MaxRoutedSessions).ToList() : [];
        var questions = new Dictionary<string, SmartQuestion>();
        if (!local.Confident) questions["intent"] = SmartDecisions.IntentQuestion();
        if (askSession) questions["session"] = SmartDecisions.SessionQuestion(sessions, FeatureId!);

        ComposerNotice = new NoticeItem { Text = "Understanding your request…", Severity = NoticeSeverity.Info };
        try
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(TimeSpan.FromSeconds(4));
            var current = sessions.FirstOrDefault(s => s.Id == FeatureId) ?? (FeatureId is { } id ? Features?.Get(id) : null);
            var answers = await SmartDecider.AskAsync(SmartDecisions.State(text, current, LastRoutingReply), questions, quick.Token);
            if (answers is null) return null;
            var decision = answers.TryGetValue("intent", out var intent) ? SmartDecisions.Route(local, intent) : fallback;
            TopicRedirect? redirect = null;
            if (askSession && SmartDecisions.Redirect(answers.GetValueOrDefault("session"), sessions, FeatureId!) is { } move)
            {
                redirect = move.New
                    ? new(text, null, null, TitleFromPrompt(text))
                    : new(text, move.FeatureId, sessions.First(s => s.Id == move.FeatureId).Name, null);
            }
            return (decision, redirect);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally
        {
            if (ComposerNotice?.Text == "Understanding your request…") ComposerNotice = null;
        }
    }

    /// <summary>Append a question to the transcript asking where the held-back prompt goes.</summary>
    private async void OfferRedirect(TopicRedirect redirect)
    {
        var question = new FeatureJumpItem { TargetName = redirect.FeatureName };
        Add(question);
        switch (await question.Answer)
        {
            case FeatureJumpChoice.Jump:
            case FeatureJumpChoice.New:
                if (Draft.Replace("\r\n", "\n").Replace('\r', '\n').Trim() == redirect.Text) Draft = "";
                TopicRedirectRequested?.Invoke(this, redirect);
                break;
            case FeatureJumpChoice.Stay:
                _keepHere = redirect.Text;
                await SendAsync();
                break;
        }
    }

    /// <summary>Sends a prompt another session handed over, once this chat is connected; a new session takes <paramref name="name"/>.</summary>
    public async Task SendRedirectedAsync(string text, string? name)
    {
        if (!IsConnected && !IsConnecting) await ConnectAsync();
        for (var waited = 0; IsConnecting && waited < 300; waited++) await Task.Delay(100);
        if (!IsConnected) { Draft = text; return; }
        var isNew = FeatureId is null;
        _keepHere = text;
        Draft = text;
        await SendAsync();
        if (isNew && name is { Length: > 0 } && FeatureId is { } id && Features is { } features)
        {
            features.Rename(id, name);
            Title = FeatureName = name;
        }
    }

    /// <summary>
    /// A history is named after its latest prompt: at once from the prompt itself, then by the General model when it is set.
    /// A prompt with no subject of its own ("yes", "continue", "commit this", a slash command) keeps the current name.
    /// </summary>
    private void RenameSessionFromPrompt(FeatureService features, string id, string prompt)
    {
        if (TitleFromPrompt(prompt) is not { } title) return;
        if (title != FeatureName)
        {
            features.Rename(id, title);
            Title = FeatureName = title;
        }
        NameSessionAsync(id, prompt, title);
    }

    /// <summary>The newest prompt the user sent in this chat, or null before the first.</summary>
    public string? LatestUserPrompt => Items.OfType<UserMessageItem>().LastOrDefault(i => i.Text.Trim().Length > 0)?.Text;

    /// <summary>The quick name for a prompt, or null when the prompt says nothing about what the session is for.</summary>
    private static string? TitleFromPrompt(string prompt)
    {
        var text = prompt.Trim();
        if (text.Length == 0 || text.StartsWith('/') || !SearchText.HasTopic(text)) return null;
        var title = SessionTitles.FromPrompt(text);
        return title.Length > 0 ? title : null;
    }

    /// <summary>The General model names the session from a prompt; the quick name stands until then, or when it is off.</summary>
    private async void NameSessionAsync(string id, string prompt, string placeholder)
    {
        if (!AppSettings.SmartFeatureEnabled("title") || Features is not { } features) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var named = await SmartExperienceHost.Instance.TitleAsync(new(_projectPath, _projectPath, "title", _chatId), prompt, deadline.Token);
            if (named?.Value is not { Length: > 0 } title) return;
            _dispatcher.TryEnqueue(() =>
            {
                // The user (or a redirect) may have named it meanwhile: only the placeholder is replaced.
                if (features.Get(id) is not { } session || session.Name != placeholder) return;
                features.Rename(id, title);
                if (FeatureId == id) Title = FeatureName = title;
            });
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }
}
