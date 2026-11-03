using System.Text;
using System.Text.RegularExpressions;

using Codale.Core.Text;

namespace Codale.Core.Helper;

/// <summary>What a message is asking for, as the router reads it.</summary>
public enum RouteIntent
{
    /// <summary>A question: explain, change nothing.</summary>
    Ask,

    /// <summary>An explicit request to plan or propose before implementing.</summary>
    Plan,

    /// <summary>A direct change: just do it.</summary>
    Act,
}

/// <summary>The router's verdict on one message: the message as typed, its intent, and whether it starts a new topic.</summary>
public sealed record RouteDecision(string Text, RouteIntent Intent, bool NewTopic);

/// <summary>
/// Automatic mode's local shortcuts and bounded helper call. Topic detection uses
/// local embeddings separately. The user's message always goes out as typed.
/// </summary>
public static partial class MessageRouting
{
    private const int MaxMessageChars = 4000;
    private const int MaxAssistantChars = 800;

    /// <summary>Fast local decisions first; a bounded helper call only for ambiguous messages.</summary>
    public static async Task<RouteDecision> RouteAsync(IHelperModel client, string message,
        string? lastAssistantText = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        if (TryRouteLocally(message) is { } local) return local;
        if (!client.IsAvailable) return RouteWithoutModel(message);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            var call = await client.CallToolAsync(SystemPrompt, BuildConversation(message, lastAssistantText),
                [RouteTool], deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return Interpret(call, message, canMoveTopic: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return RouteWithoutModel(message);
        }
    }

    public const string SystemPrompt =
        "Classify new_message; call route exactly once. Do not answer it.\n" +
        "act (default): requests to implement/change/test/run, bug reports, wishes, approvals or continuation. " +
        "Polite questions like 'can you fix X?' are act. Complexity alone never means plan.\n" +
        "plan: explicitly requests a plan, proposal or design before implementation; no implementation requested yet. " +
        "'Plan and implement' is act; 'plan first, do not edit yet' is plan.\n" +
        "ask: explanation, investigation, review or comparison with no requested changes. " +
        "'Why is X slow? Fix it' is act. 'How do I fix X?' is ask. " +
        "An acknowledgment followed by a question ('yes, why is it broken?') is ask, not approval to edit. " +
        "Use last_agent_message only to resolve replies such as approvals or answers to questions. " +
        "Never change sessions. When uncertain choose act.\n" +
        PromptRules.DataOnly + "\n" +
        PromptRules.CallOnly;

    public static readonly ToolDefinition RouteTool = new()
    {
        Name = "route",
        Description = "Pick the mode for the message. Keep it in the current session.",
        Parameters =
        [
            new ToolParameter
            {
                Name = "intent",
                Description = "the mode for the turn",
                Required = true,
                AllowedValues = ["ask", "plan", "act"],
            },
        ],
    };

    /// <summary>
    /// The context block the model reads: the agent's last reply - its account of what
    /// the session did - then the new message. Without a reply, only the message.
    /// </summary>
    public static string BuildConversation(string message, string? lastAssistantText)
    {
        var sb = new StringBuilder();

        if (lastAssistantText is { Length: > 0 })
        {
            sb.AppendLine(PromptRules.Tag("last_agent_message", Clip(lastAssistantText, MaxAssistantChars)));
        }

        sb.Append(PromptRules.Tag("new_message", Clip(message, MaxMessageChars)));
        return sb.ToString();
    }

    /// <summary>
    /// Clear short approvals, requests, questions and explicit plans need no model.
    /// Null means the model should read the message.
    /// </summary>
    public static RouteDecision? TryRouteLocally(string message)
    {
        var text = message.Trim();
        if (IsGoAhead(text))
        {
            return new RouteDecision(message, RouteIntent.Act, NewTopic: false);
        }

        // Only short, single-purpose messages qualify. Mixed/quoted/code prompts need the model.
        if (text.Length == 0 || text.Length > 160 || CountWords(text) > 24 ||
            text.IndexOfAny(['\r', '\n', '`', '"', '<', '>']) >= 0 ||
            AmbiguousClause().IsMatch(text)) return null;

        if (ExplicitPlan().IsMatch(Codale.Core.Agents.SessionTitles.StripFillers(text)))
            return new(message, RouteIntent.Plan, false);
        if (IsChangeRequest(text)) return new(message, RouteIntent.Act, false);
        if (PureQuestion().IsMatch(Codale.Core.Agents.SessionTitles.StripFillers(text)))
            return new(message, RouteIntent.Ask, false);

        return null;
    }

    /// <summary>
    /// The decision when the model could not give one: the words as typed, a pure
    /// question as ask and everything else as act, in the session it was typed in.
    /// </summary>
    public static RouteDecision RouteWithoutModel(string message) =>
        TryRouteLocally(message) ?? new(message,
            IsPlanOnly(message) ? RouteIntent.Plan :
            IsQuestion(message) && !IsChangeRequest(message) ? RouteIntent.Ask : RouteIntent.Act, NewTopic: false);

    private static bool IsPlanOnly(string message) =>
        (ExplicitPlan().IsMatch(Codale.Core.Agents.SessionTitles.StripFillers(message.Trim())) &&
            !ImplementationRequest().IsMatch(message)) ||
        (NoEdits().IsMatch(message) && PlanWord().IsMatch(message));

    /// <summary>
    /// Turns the model's call into a decision for the message as typed, distrusting it
    /// wherever it could hurt. The legacy <paramref name="canMoveTopic"/> argument is ignored:
    /// only explicit user actions control session boundaries.
    /// </summary>
    public static RouteDecision Interpret(ToolCall? call, string original, bool canMoveTopic)
    {
        if (call is null || call.Tool != RouteTool.Name)
        {
            return RouteWithoutModel(original);
        }

        var intent = call.GetString("intent")?.Trim().ToLowerInvariant() switch
        {
            "ask" => RouteIntent.Ask,
            "plan" => RouteIntent.Plan,
            _ => RouteIntent.Act,
        };

        if (IsPlanOnly(original)) intent = RouteIntent.Plan;
        if (AcknowledgementPrefix().IsMatch(original.Trim()) &&
            IsQuestion(original) && !IsChangeRequest(original)) intent = RouteIntent.Ask;

        // A small model leans ask, and an ask turn refuses every edit, so ask has to be
        // earned: the message must read as a question and carry no change request. A
        // statement ("the header is ugly"), a go-ahead or a question mixed with a
        // request all go out as act.
        if (intent == RouteIntent.Ask && (IsChangeRequest(original) || !IsQuestion(original)))
        {
            intent = RouteIntent.Act;
        }

        return new RouteDecision(original, intent, NewTopic: false);
    }

    /// <summary>
    /// True when the message leans on what came before: a go-ahead, a reply opening
    /// with "also", "it", "that" or "try again", a word like "still" or "again", or
    /// too few words to be a subject of its own.
    /// </summary>
    public static bool IsFollowUp(string message)
    {
        var text = PolitenessPattern().Replace(message.Trim(), "");
        return CountWords(text) < 2 ||
            IsGoAhead(text) ||
            FollowUpOpener().IsMatch(text) ||
            BackReference().IsMatch(text);
    }

    private static int CountWords(string text) => WordPattern().Count(text);

    private static bool IsGoAhead(string text) => text.Length <= 80 && GoAheadPattern().IsMatch(text);

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex WordPattern();

    /// <summary>
    /// True when the message, past its politeness ("can you", "let's", "please"), opens
    /// with a verb that changes something. "How do I make..." opens with "how" and stays a question.
    /// </summary>
    public static bool IsChangeRequest(string message)
    {
        if (IsGoAhead(message.Trim()))
        {
            return true;
        }

        // Any sentence or line can carry the request: "why is it slow? speed it up".
        foreach (var part in SentenceBreak().Split(message))
        {
            if (part.Length > 0 && ChangeVerbPattern().IsMatch(Codale.Core.Agents.SessionTitles.StripFillers(part.Trim())))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the message reads as a question: it opens with an interrogative or an
    /// explain-style verb, or is punctuated as one. Anything else - a statement, a
    /// complaint, a wish - is not something an ask turn should take.
    /// </summary>
    public static bool IsQuestion(string message)
    {
        var text = Codale.Core.Agents.SessionTitles.StripFillers(message.Trim());
        text = AcknowledgementPrefix().Replace(text, "");
        return QuestionOpener().IsMatch(text) || message.TrimEnd().EndsWith('?');
    }

    [GeneratedRegex(@"[.!?;,\r\n]+\s*")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(
        @"^(?:(?:yes|yep|yeah|ok|okay|sure|go ahead|do it|proceed|continue|try again|retry|sounds good|looks good|lgtm|approved?)[\s,.!]*(?:and\s+)?)+[.!]*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GoAheadPattern();

    [GeneratedRegex(@"^((please|pls|plz|hey|hmm+|well|so)\b[\s,.!]*)+", RegexOptions.IgnoreCase)]
    private static partial Regex PolitenessPattern();

    [GeneratedRegex(
        @"^(also|and|plus|then|again|still|instead|but|actually|nope|no|not|wait|" +
        @"it|its|it's|that|thats|that's|this|these|those|there|here|same|one more|another|" +
        @"try again|retry|undo|revert|redo)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex FollowUpOpener();

    [GeneratedRegex(
        @"\b(still|again|as well|you just|you did|you made|you changed|you added|you wrote|your last|" +
        @"previous|earlier|last change|same issue|same problem)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex BackReference();

    [GeneratedRegex(
        @"^(what|whats|why|how|where|when|which|who|whom|whose|is|are|was|were|does|do|did|can|could|should|would|will|" +
        @"explain|describe|tell me|show me|walk me|summari[sz]e|review|compare|investigate|analy[sz]e|inspect|audit|list|find out|look into|check whether|" +
        @"i wonder|any idea)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuestionOpener();

    private static string Clip(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : TextClip.Truncate(text, max).TrimEnd() + "…";
    }

    [GeneratedRegex(
        @"^(make|add|fix|change|update|create|remove|delete|rename|refactor|implement|move|replace|improve|" +
        @"redesign|rework|redo|rewrite|build|write|set|use|convert|clean|tweak|adjust|restyle|style|polish|" +
        @"optimi[sz]e|speed it up|speed up|simplify|extract|split|merge|install|upgrade|bump|commit|push|run|test|debug|deploy|" +
        @"enable|disable|turn|hide|put|drop|revert|undo|reset|generate|migrate|port|wire|hook|support|" +
        @"allow|prevent|stop|handle|translate|format|resize|center|align|color|colour)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChangeVerbPattern();

    [GeneratedRegex(@"\b(and|but|then|instead|unless|except|before|after|first|yet|don't|do not|without)\b|[,;:]|[.!?]\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex AmbiguousClause();

    [GeneratedRegex(@"^(plan\b|(?:write|create|draft|propose)\s+(?:(?:a|an|the)\s+)?(?:implementation\s+)?(?:plan|proposal)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitPlan();

    [GeneratedRegex(@"^(what|why|how|where|when|which|who|explain|describe|summari[sz]e|review|compare)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PureQuestion();

    [GeneratedRegex(@"^(?:yes|yep|yeah|sure)\s*[,!:]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AcknowledgementPrefix();

    [GeneratedRegex(@"\b(implement|build|fix|apply|execute|make|edit|write code)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImplementationRequest();

    [GeneratedRegex(@"\b(do not|don't|dont)\s+(edit|implement|modify)(?:\s+(yet|anything|any files|the code))?(?=\s*(?:[;,.!?]|$))|\bno\s+(edits|implementation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoEdits();

    [GeneratedRegex(@"\b(plan|proposal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlanWord();
}
