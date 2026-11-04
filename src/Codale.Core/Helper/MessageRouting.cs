using System.Text;
using System.Text.RegularExpressions;

using Codale.Core.Text;

namespace Codale.Core.Helper;

/// <summary>What a message is asking for, as the router reads it.</summary>
public enum RouteIntent
{
    /// <summary>A question: explain, change nothing.</summary>
    Ask,

    /// <summary>Plan first: explicit planning, substantial work, new capabilities or unclear requirements.</summary>
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
        "plan (safe default): substantial or cross-cutting changes, migrations, rewrites, new features or capabilities, " +
        "and unclear requests whose scope, desired behavior, constraints or acceptance criteria would have to be guessed. " +
        "Use plan even when the user says implement/build for substantial work: let them clarify and correct the plan first. " +
        "Examples: build authentication, add CSV export, redesign the dashboard, make the app better, plan and implement a migration.\n" +
        "act: a small, well-specified fix or adjustment, routine tests/commands, or approval to execute an already discussed plan. " +
        "Examples: rename Save to Apply, fix the login redirect, run tests, implement the approved plan. " +
        "A clear approval or continuation is act; new scope added to an approval must be classified on its own.\n" +
        "ask: explanation, investigation, review or comparison with no requested changes. " +
        "'Why is X slow? Fix it' is act. 'How do I fix X?' is ask. " +
        "An acknowledgment followed by a question ('yes, why is it broken?') is ask, not approval to edit. " +
        "Use last_agent_message only to resolve replies such as approvals or answers to questions. " +
        "Never change sessions. When requirements are unclear choose plan, not implementation or invented requirements.\n" +
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

        if (RequiresPlanning(text)) return new(message, RouteIntent.Plan, false);

        // Only short, single-purpose messages qualify. Mixed/quoted/code prompts need the model.
        if (text.Length == 0 || text.Length > 160 || CountWords(text) > 24 ||
            text.IndexOfAny(['\r', '\n', '`', '"', '<', '>']) >= 0 ||
            AmbiguousClause().IsMatch(text)) return null;

        if (ExplicitPlan().IsMatch(Codale.Core.Agents.SessionTitles.StripFillers(text)))
            return new(message, RouteIntent.Plan, false);
        if (IsChangeRequest(text) && IsBoundedChange(text)) return new(message, RouteIntent.Act, false);
        if (PureQuestion().IsMatch(Codale.Core.Agents.SessionTitles.StripFillers(text)))
            return new(message, RouteIntent.Ask, false);

        return null;
    }

    /// <summary>
    /// The decision when the model could not give one: the words as typed, a pure
    /// question as ask, bounded work as act, and otherwise a plan to clarify first.
    /// </summary>
    public static RouteDecision RouteWithoutModel(string message) =>
        TryRouteLocally(message) ?? new(message,
            IsPlanOnly(message) ? RouteIntent.Plan :
            IsQuestion(message) && !IsChangeRequest(message) ? RouteIntent.Ask :
            IsBoundedChange(message) ? RouteIntent.Act : RouteIntent.Plan, NewTopic: false);

    private static bool RequiresPlanning(string message)
    {
        if (IsPlanOnly(message)) return true;
        if (IsQuestion(message) && !IsChangeRequest(message)) return false;
        var text = Codale.Core.Agents.SessionTitles.StripFillers(message.Trim());
        if (NewCapability().IsMatch(text)) return true;
        if (SubstantialWork().IsMatch(text) && (!ApprovedImplementation().IsMatch(text) || AdditionalScope().IsMatch(text))) return true;
        if (ApprovedImplementation().IsMatch(text) || RoutineWork().IsMatch(text)) return false;
        return !IsBoundedChange(text) && (NewWorkRequest().IsMatch(text) || UnclearChange().IsMatch(text));
    }

    private static bool IsBoundedChange(string message)
    {
        var text = Codale.Core.Agents.SessionTitles.StripFillers(message.Trim());
        return IsGoAhead(text) || ApprovedImplementation().IsMatch(text) || RoutineWork().IsMatch(text) ||
            (CountWords(text) >= 3 && BoundedChange().IsMatch(text)) ||
            (IsQuestion(text) && IsChangeRequest(text) && SentenceBreak().Split(text).Any(p => BoundedChange().IsMatch(p.Trim())));
    }

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
            "act" => RouteIntent.Act,
            _ => RouteWithoutModel(original).Intent,
        };

        if (RequiresPlanning(original)) intent = RouteIntent.Plan;
        if (AcknowledgementPrefix().IsMatch(original.Trim()) &&
            IsQuestion(original) && !IsChangeRequest(original)) intent = RouteIntent.Ask;

        // A small model leans ask, and an ask turn refuses every edit, so ask has to be
        // earned: the message must read as a question and carry no change request. A
        // statement or mixed request uses the conservative fallback; only bounded
        // work and approvals proceed directly.
        if (intent == RouteIntent.Ask && (IsChangeRequest(original) || !IsQuestion(original)))
        {
            intent = RouteWithoutModel(original).Intent;
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

    [GeneratedRegex(@"\b(refactor|rewrite|redesign|rearchitect|re-architect|rework|migrat\w*|overhaul|architecture|major changes?|large[- ]scale|across (?:all|multiple|every)|entire (?:app|application|system|project)|whole (?:app|application|system|project))\b", RegexOptions.IgnoreCase)]
    private static partial Regex SubstantialWork();

    [GeneratedRegex(@"\bnew (?:feature|capability|workflow|system|module)\b|\b(?:add|build|create|implement|introduce|support)\s+(?:(?:a|an|the|complete|new)\s+)*(?:(?:csv|pdf|email|push)\s+)?(?:authentication|authorization|user accounts?|notifications?|export|import|dashboard|payment|billing|chat|search feature|dark theme|offline support)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NewCapability();

    [GeneratedRegex(@"^(?:add|create|build|implement|introduce|support|allow)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NewWorkRequest();

    [GeneratedRegex(@"\b(unclear|not sure|figure out what|whatever you think|make it better|improve everything|ugly|nicer|more modern|more professional)\b|\b(?:design|look|feel)\b.*\b(?:better|more|different)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnclearChange();

    [GeneratedRegex(@"^(?:implement|execute|apply|carry out)\s+(?:(?:the|this|that|approved|agreed)\s+)*plan\b", RegexOptions.IgnoreCase)]
    private static partial Regex ApprovedImplementation();

    [GeneratedRegex(@"\b(?:and|also|plus|then)\s+(?:add|build|rewrite|migrate|refactor|implement|create|redesign)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AdditionalScope();

    [GeneratedRegex(@"^(?:commit|push|run|test|retry|revert|undo)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoutineWork();

    [GeneratedRegex(@"^(?:fix|debug|rename|remove|delete|move|resize|center|align|color|colour|hide|disable|enable|format|tweak|adjust)\b|^(?:add|write|create)\s+(?:(?:a|an|the|status|unit|integration|regression)\s+)*(?:column|label|tooltip|log|logging|test|tests)\b|^(?:make|set|change|update)\b.*\b(?:blue|red|green|black|white|font|width|height|padding|margin|label|text|colour|color)\b|^(?:build|compile)\s+(?:the\s+)?(?:project|solution|release|package|tests)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BoundedChange();
}
