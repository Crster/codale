using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.App.ViewModels;

/// <summary>A prompt Auto mode found belongs elsewhere: an existing session (<see cref="FeatureId"/>) or a new one.</summary>
public sealed record TopicRedirect(string Text, string? FeatureId, string? FeatureName, string? NewName)
{
    public IReadOnlyList<TurnAttachment> Attachments { get; init; } = [];
}

/// <summary>
/// Model-first assessment of independent topic, intent, difficulty, planning, clarity and preference dimensions.
/// </summary>
public sealed partial class ChatViewModel
{
    private const int MaxRoutedSessions = 40;

    /// <summary>The user chose where a redirected prompt goes; the workspace opens or creates that session and sends it.</summary>
    public event EventHandler<TopicRedirect>? TopicRedirectRequested;

    /// <summary>A prompt the user decided to keep in this session: it is not asked about again.</summary>
    private string? _keepHere;

    /// <summary>
    /// The last assessment shown in the routing notice. Decision, then General, then local fallback, per field.
    /// </summary>
    private AutoAssessment? _autoAssessment;

    private async Task<(RouteDecision Decision, TopicRedirect? Redirect)> RouteSmartAsync(
        string text, UserMessageItem shown, CancellationToken ct)
    {
        var keep = _keepHere == text;
        _keepHere = null;
        var recent = Features?.List().OrderByDescending(s => s.Id == FeatureId)
            .ThenByDescending(s => s.UpdatedAt).Take(MaxRoutedSessions).ToList() ?? [];
        var sessions = recent.Where(s => s.Id == FeatureId).Concat(Features?.Search(text, 10) ?? [])
            .Concat(recent).DistinctBy(s => s.Id).Take(MaxRoutedSessions).ToList();
        var earlier = Items.OfType<UserMessageItem>().Where(i => !ReferenceEquals(i, shown))
            .TakeLast(3).Select(i => i.Text).ToArray();
        var questions = AutoDecisions.Questions();
        var canRedirect = !keep && FeatureId is not null && earlier.Length > 0;
        if (canRedirect) questions["session"] = AutoDecisions.SessionQuestion(sessions, FeatureId!);
        var memory = RecallMemory(text);
        var state = new
        {
            current_session = sessions.FirstOrDefault(s => s.Id == FeatureId),
            recent_user_messages = earlier,
            last_agent_reply = LastRoutingReply,
            new_message = text,
            attachments = shown.Attachments.Select(a => new { a.Name, kind = a.Kind.ToString(), a.Path }),
            memory,
        };
        var general = _smart.CreateClient(SmartFeatureSettings.General);
        var assessment = await AutoRouter.AssessAsync(text, state, questions,
            (input, qs, token) => _smart.Settings.SmartFeatureEnabled("routing") && _decider.IsAvailable
                ? _decider.AskAsync(input, qs, token) : Task.FromResult<IReadOnlyDictionary<string, SmartDecision>?>(null),
            async (system, input, token) => _smart.Settings.SmartFeatureEnabled("routing") && general.IsEnabled
                ? await general.CompleteAsync("routing", system, input, 800, token) : null, ct);
        if (canRedirect && !assessment.FieldSources.ContainsKey("session") &&
            TopicShift.IsNewTopic(text, earlier, LastRoutingReply) && !MessageRouting.IsFollowUp(text))
            assessment = assessment with { Topic = "new" };
        // An empty chat is a fresh topic: only then (or on a new topic) can a planner be worth its cost.
        if (earlier.Length == 0) assessment = assessment with { FreshTopic = true };
        _autoAssessment = assessment;
        OnPropertyChanged(nameof(AutoRoutingDetails));
        TopicRedirect? redirect = assessment.Topic switch
        {
            "new" when canRedirect => new(text, null, null, TitleFromPrompt(text)) { Attachments = shown.Attachments },
            var id when canRedirect && id != "current" && sessions.FirstOrDefault(s => s.Id == id && s.Id != FeatureId) is { } target =>
                new(text, target.Id, target.Name, null) { Attachments = shown.Attachments },
            _ => null,
        };
        return (new(text, assessment.Intent, redirect is not null, assessment.Confidence >= AutoDecisions.MinimumConfidence), redirect);
    }

    /// <summary>Append a question to the transcript asking where the held-back prompt goes.</summary>
    private async void OfferRedirect(TopicRedirect redirect)
    {
        var question = new FeatureJumpItem { TargetName = redirect.FeatureName };
        Add(question);
        AttentionNeeded?.Invoke(this, new AttentionRequest(AttentionKind.Question,
            redirect.FeatureName is { Length: > 0 } target
                ? $"Continue this message in {target}, start a new history, or stay here?"
                : "Start a new history for this message, or stay here?"));
        var choice = await question.Answer;
        switch (choice)
        {
            case FeatureJumpChoice.Jump:
            case FeatureJumpChoice.New:
                if (Draft.Replace("\r\n", "\n").Replace('\r', '\n').Trim() == redirect.Text) Draft = "";
                foreach (var attachment in Attachments.Where(a => redirect.Attachments.Any(sent => sent.Path == a.Path)).ToArray())
                    Attachments.Remove(attachment);
                TopicRedirectRequested?.Invoke(this, choice == FeatureJumpChoice.New
                    ? redirect with { FeatureId = null, FeatureName = null }
                    : redirect);
                break;
            case FeatureJumpChoice.Stay:
                _keepHere = redirect.Text;
                AddRedirectedAttachments(redirect.Attachments);
                await SendAsync();
                break;
        }
    }

    /// <summary>Sends a prompt another session handed over, once this chat is connected; a new session takes <paramref name="name"/>.</summary>
    public async Task SendRedirectedAsync(string text, string? name, IReadOnlyList<TurnAttachment>? attachments = null)
    {
        if (!IsConnected && !IsConnecting) await ConnectAsync();
        for (var waited = 0; IsConnecting && waited < 300; waited++) await Task.Delay(100);
        if (!IsConnected) { Draft = text; AddRedirectedAttachments(attachments ?? []); return; }
        var isNew = FeatureId is null;
        _keepHere = text;
        Draft = text;
        AddRedirectedAttachments(attachments ?? []);
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
        if (!_smart.Settings.SmartFeatureEnabled("title") || Features is not { } features) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var named = await _smart.Instance.TitleAsync(new(ProjectPath, ProjectPath, "title", _chatId), prompt, deadline.Token);
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
