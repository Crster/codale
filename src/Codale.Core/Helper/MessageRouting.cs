using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Codale.Core.Text;

namespace Codale.Core.Helper;

/// <summary>What a message is asking for, as the router reads it.</summary>
public enum RouteIntent
{
    /// <summary>A question: explain, change nothing.</summary>
    Ask,

    /// <summary>Plan first: explicit planning, large cross-cutting work, or a request too vague to act on.</summary>
    Plan,

    /// <summary>A direct change: just do it.</summary>
    Act,
}

/// <summary>
/// The router's verdict on one message: the message as typed, its intent, and whether it starts a new topic.
/// <see cref="Confident"/> means the local rules are sure, so no model needs to be asked.
/// </summary>
public sealed record RouteDecision(string Text, RouteIntent Intent, bool NewTopic, bool Confident = false);

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
            return Interpret(call, message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return RouteWithoutModel(message);
        }
    }

    public const string SystemPrompt =
        "Classify new_message; call route exactly once. Do not answer it.\n" +
        "act (default): requests to change, fix, add, adjust, test or run something, bug reports, complaints about " +
        "something specific (spacing, layout, a broken flow), approvals, answers to the agent's questions, continuation. " +
        "A feature request that says what to add and where is act even when it adds new UI, a column, a toolbar or a field; " +
        "screenshots, links and examples make a request clearer, not unclear. Polite questions like 'can you fix X?' are act. " +
        "Examples: add a column flagging overdue milestones, this spacing is annoying can we redesign it, " +
        "arrange the permissions by menu, rename Save to Apply, run tests, implement the approved plan.\n" +
        "plan: only when (a) the user asks for a plan, proposal or design before implementation, " +
        "(b) the work is large and cross-cutting: a new subsystem such as authentication or billing, a migration, " +
        "or a rewrite or re-architecture spanning many files, or (c) the target or desired result cannot be identified at all " +
        "('make it better'). Size, the word new, or a missing detail the agent can reasonably choose never means plan.\n" +
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
    /// The decision when the local rules are sure, null when a model should read the message. Sure means a go-ahead, an
    /// explicit plan, work that is big, new or unclear (see <see cref="ScopeOfWork"/>), a plain question, or a short
    /// single-purpose change request.
    /// </summary>
    public static RouteDecision? TryRouteLocally(string message)
    {
        var decision = Decide(message);
        return decision.Confident ? decision : null;
    }

    /// <summary>
    /// The decision when the model could not give one: the local rules' best reading. Plans come only from the
    /// local rules, questions are asks, and everything else is act: a slow or failed helper must not turn
    /// ordinary requests into plans.
    /// </summary>
    public static RouteDecision RouteWithoutModel(string message) =>
        Assess(message) is { Confident: false, Intent: RouteIntent.Plan } weak ? weak with { Intent = RouteIntent.Act } : Assess(message);

    /// <summary>
    /// The local rules' reading of the message, including what they are unsure of: a weak plan (many changes in one long
    /// message) is kept as a plan with <see cref="RouteDecision.Confident"/> false, for a model to confirm or overrule
    /// through <see cref="Reconcile"/>.
    /// </summary>
    public static RouteDecision Assess(string message) => Decide(message);

    private static RouteDecision Decide(string message)
    {
        var text = Normalize(message);
        if (IsGoAhead(text)) return new(message, RouteIntent.Act, NewTopic: false, Confident: true);
        if (IsPlanOnly(text)) return new(message, RouteIntent.Plan, NewTopic: false, Confident: true);

        // A question that asks for nothing to change. Long text may hide a request inside pasted detail: the model reads it.
        if (IsQuestion(text) && !IsChangeRequest(text) && !BugConfirmation().IsMatch(text))
            return new(message, RouteIntent.Ask, NewTopic: false, Confident: text.Length <= MaxPureQuestionChars);

        // Large subsystems, rewrites and migrations, and requests with no identifiable target always plan, unless the
        // message is plainly a small fix that happens to mention one ("fix the typo in the migration script").
        var stripped = Codale.Core.Agents.SessionTitles.StripFillers(text);
        if ((LargeSubsystem().IsMatch(stripped) || SubstantialWork().IsMatch(stripped) || VagueRequest().IsMatch(stripped)) &&
            !(CountWords(stripped) >= 3 && BoundedChange().IsMatch(stripped)) &&
            !(ApprovedImplementation().IsMatch(stripped) && !AdditionalScope().IsMatch(stripped)))
            return new(message, RouteIntent.Plan, NewTopic: false, Confident: true);

        if (ScopeOfWork(message) is { } scope)
            return new(message, RouteIntent.Plan, NewTopic: false, Confident: scope.Strong);

        // Act when it is short, single-purpose and starts like a change. Otherwise act is only a default.
        var bounded = text.Length <= 200 && CountWords(text) <= 24 && IsChangeRequest(text) && !AmbiguousClause().IsMatch(text);
        return new(message, RouteIntent.Act, NewTopic: false, Confident: bounded || RoutineWork().IsMatch(text));
    }

    private const int MaxPureQuestionChars = 600;

    /// <summary>"is this correct? the totals are over 100%": the user reports a problem and wants it fixed, so it is not an ask.</summary>
    [GeneratedRegex(@"^(?:is|are)\s+(?:this|that|these|those|it)\s+(?:correct|right|expected|normal|intended|a\s+bug|wrong|broken|supposed\s+to)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BugConfirmation();

    /// <summary>
    /// Plans that hold whatever the model says: an explicit plan request, work that is big, a new feature or too
    /// unclear to act on.
    /// </summary>
    private static bool RequiresPlanning(string message)
    {
        if (IsPlanOnly(message)) return true;
        if (IsQuestion(message) && !IsChangeRequest(message)) return false;
        var text = Codale.Core.Agents.SessionTitles.StripFillers(message.Trim());
        if (ApprovedImplementation().IsMatch(text) && !AdditionalScope().IsMatch(text)) return false;
        return LargeSubsystem().IsMatch(text) || SubstantialWork().IsMatch(text) || VagueRequest().IsMatch(text) ||
               ScopeOfWork(message) is { Strong: true };
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
        (NoEdits().IsMatch(message) && (PlanWord().IsMatch(message) || DesignWord().IsMatch(message))) ||
        AskForPlan().IsMatch(message);

    /// <summary>"give me a plan first", "plan before you implement": a plan requested anywhere in the message, not only at its start.</summary>
    [GeneratedRegex(@"\b(?:give|write|draft|propose|prepare|make|create|show)\s+(?:me\s+)?(?:a|an|the)?\s*(?:implementation\s+)?(?:plan|proposal)\b|\bplan\s+(?:first|before)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AskForPlan();

    /// <summary>With "don't edit yet", a proposal for a design or approach is a plan.</summary>
    [GeneratedRegex(@"\b(?:design|approach|outline|strategy)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DesignWord();

    /// <summary>
    /// A model's classification checked against the local rules. A confident local decision stands. Otherwise the
    /// model's plan is kept unless the message is a bounded change, and its ask has to be earned: the message must
    /// read as a question and ask for no change.
    /// </summary>
    public static RouteDecision Reconcile(RouteDecision local, RouteIntent model)
    {
        if (local.Confident) return local;
        var intent = model;
        // A plan needs substance: a few words, or one short bounded change, is not worth interrupting for.
        var words = CountWords(Normalize(local.Text));
        if (intent == RouteIntent.Plan && (words < 5 || (words <= 24 && IsBoundedChange(local.Text)))) intent = RouteIntent.Act;
        if (intent == RouteIntent.Ask && (IsChangeRequest(local.Text) || !IsQuestion(local.Text))) intent = RouteIntent.Act;
        return local with { Intent = intent, Confident = true };
    }

    /// <summary>
    /// Turns the model's call into a decision for the message as typed, distrusting it
    /// wherever it could hurt. Only explicit user actions control session boundaries.
    /// </summary>
    public static RouteDecision Interpret(ToolCall? call, string original)
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
        // The model leans plan on anything that sounds like a feature; work the local
        // rules recognise as bounded goes straight to implementation.
        else if (intent == RouteIntent.Plan && !PlanWord().IsMatch(original) && IsBoundedChange(original)) intent = RouteIntent.Act;
        if (AcknowledgementPrefix().IsMatch(original.Trim()) &&
            IsQuestion(original) && !IsChangeRequest(original)) intent = RouteIntent.Ask;

        // A small model leans ask, and an ask turn refuses every edit, so ask has to be
        // earned: the message must read as a question and carry no change request. A
        // statement ("the header is ugly"), a go-ahead or a question mixed with a
        // request falls back to the local decision, which is act unless it must plan.
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
            BackReference().IsMatch(text) ||
            ContinuationPhrase().IsMatch(text) ||
            (CountWords(text) <= 6 && PronounObject().IsMatch(text)) ||
            ReactionOpener().IsMatch(text) ||
            AnswerToAgent().IsMatch(text) ||
            ReferencesPriorWork().IsMatch(text) ||
            (CountWords(text) <= 8 && ShortFeedback().IsMatch(text));
    }

    /// <summary>Opens as a reaction or approval of what the agent just said: "looks good, commit it", "great, now ...", "go for it".</summary>
    [GeneratedRegex(
        @"^(?:(?:great|good|nice|perfect|awesome|excellent|cool|exactly|agreed|thanks?|thank\s+you|thx|go\s+for\s+it|i\s+approve|i\s+agree|that\s+works|works\s+for\s+me|do\s+that|ship\s+it|lgtm|yes|yep|yeah|yup|ok|okay|sure|alright|approved?|proceed|go\s+ahead|continue|carry\s+on|keep\s+going)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReactionOpener();

    /// <summary>Picks an option or answers a question the agent asked: "option 2", "the first one", "use the second approach", "what about X", "how about Y".</summary>
    [GeneratedRegex(
        @"^(?:(?:go\s+with|use|pick|choose|take|do|try|take)\s+)?(?:option|choice|approach|plan|variant|#)\s*[a-z0-9]\b|" +
        @"^(?:(?:go\s+with|use|pick|choose|take|do|try)\s+)?(?:the\s+)?(?:first|second|third|fourth|last|former|latter|other|1st|2nd|3rd)\s+(?:one|option|approach|choice|plan)\b|" +
        @"^(?:what|how)\s+about\b|^(?:and\s+)?(?:why|how)\s+(?:did|does|is|was|come)\s+(?:it|that|this|you)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex AnswerToAgent();

    /// <summary>Names the earlier work instead of a subject: "the above", "as discussed", "like you said", "per the plan", "the previous error", "your suggestion".</summary>
    [GeneratedRegex(
        @"\b(?:the\s+above|as\s+(?:discussed|planned|suggested|agreed|mentioned|said|before)|like\s+(?:you|we)\s+(?:said|discussed|suggested|planned)|per\s+the\s+plan|" +
        @"the\s+plan\s+(?:above|you|we)|your\s+(?:plan|suggestion|proposal|recommendation|answer|reply|response|fix|change|changes|code|implementation)|" +
        @"(?:that|the)\s+(?:error|bug|issue|problem|crash|fix|change|changes|plan|approach|result|output|log|logs)\s+(?:above|you|we|from\s+before|earlier)|" +
        @"you\s+(?:said|suggested|proposed|mentioned|recommended|wrote|ran|found)|we\s+(?:discussed|talked|planned|just)|just\s+now|a\s+moment\s+ago|so\s+far|" +
        @"what\s+you\s+(?:did|made|changed|wrote)|(?:last|previous|prior|above)\s+(?:message|reply|response|answer|plan|step|result))\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReferencesPriorWork();

    /// <summary>A short complaint or correction about the last result: "doesn't work", "still broken", "wrong", "that's not what I meant", "same error".</summary>
    [GeneratedRegex(
        @"\b(?:doesn'?t|does\s+not|didn'?t|did\s+not|isn'?t|is\s+not|won'?t|not)\s+(?:work|working|fixed|right|correct|showing|appear|change|apply|applied|updated?)\b|" +
        @"\b(?:broken|wrong|incorrect|not\s+what\s+i|still\s+(?:fails?|broken|wrong|happens)|same\s+(?:error|result|thing)|nothing\s+(?:happened|changed)|no\s+change|failed|fails)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ShortFeedback();

    /// <summary>"continue where it left off", "pick up where we stopped", "carry on with the rest".</summary>
    [GeneratedRegex(
        @"\b(?:(?:continue|carry\s+on|resume|pick\s+up|keep\s+going|go\s+on)\b.*\b(?:where|from|with\s+the\s+rest|left\s+off|stopped|before)|where\s+(?:it|you|we|they)\s+(?:left|stopped)|left\s+off|the\s+rest)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ContinuationPhrase();

    /// <summary>A short command whose object is a pronoun: "implement it", "fix that", "apply this", "do them".</summary>
    [GeneratedRegex(@"^\w+(?:\s+\w+)?\s+(?:it|this|that|them|those|these)(?:\s+(?:now|too|please|again))?[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex PronounObject();

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
            if (part.Length > 0 && ChangeVerbPattern().IsMatch(Codale.Core.Agents.SessionTitles.StripFillers(LeadIn().Replace(part.Trim(), ""))))
            {
                return true;
            }

            // "investigate the crash and fix it": the request can follow an "and" in the same sentence. A question
            // keeps its own conjunctions ("how do I add and remove items?").
            if (part.Length > 0 && !message.TrimEnd().EndsWith('?') && !QuestionOpener().IsMatch(Codale.Core.Agents.SessionTitles.StripFillers(part.Trim())))
            {
                var clauses = ClauseBreak().Split(part);
                for (var i = 1; i < clauses.Length; i++)
                {
                    if (ChangeVerbPattern().IsMatch(Codale.Core.Agents.SessionTitles.StripFillers(clauses[i].Trim()))) return true;
                }
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

    /// <summary>
    /// How a question opens: an interrogative, an auxiliary with its subject ("is the build broken", "do we have", but not
    /// "do the same"), or an explain-style verb. "investigate", "check" and "look into" are not here: they usually lead to a fix.
    /// </summary>
    [GeneratedRegex(
        @"^(?:what|whats|what's|why|how|where|when|which|who|whom|whose|" +
        @"(?:is|are|was|were|does|did|can|could|should|would|will|has|have|am)\s+(?:you|we|i|they|he|she|it|this|that|these|those|there|the|a|an|my|our|your|any|some|all|each|every)\b|" +
        @"do\s+(?!(?:the\s+same|that|this|it|so|these|those)\b)(?:you|we|i|they|he|she|there|the|a|an|my|our|your|any|some|all|each|every|this)\b|" +
        @"explain|describe|tell\s+me|walk\s+me|summari[sz]e|review|compare|list|find\s+out|check\s+whether|check\s+if|i\s+wonder|any\s+(?:idea|way|suggestion)s?|is\s+there|are\s+there|how\s+come)\b",
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
        @"do\s+(?:the\s+same|that|this|it)|apply|have\s+(?:a|an|the|some|another|different|more)|group|sort|filter|display|combine|reorder|highlight|colou?rize|expand|collapse|show(?!\s+me)|optimi[sz]e|speed it up|speed up|simplify|extract|split|merge|install|upgrade|bump|commit|push|run|test|debug|deploy|" +
        @"enable|disable|turn|hide|put|drop|revert|undo|reset|generate|migrate|port|wire|hook|support|" +
        @"allow|prevent|stop|handle|translate|format|resize|center|align|color|colour)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChangeVerbPattern();

    [GeneratedRegex(@"\b(and|but|then|instead|unless|except|before|after|first|yet|don't|do not|without)\b|[,;:]|[.!?]\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex AmbiguousClause();

    [GeneratedRegex(@"^(plan\b|(?:write|create|draft|propose)\s+(?:(?:a|an|the)\s+)?(?:implementation\s+)?(?:plan|proposal)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitPlan();

    [GeneratedRegex(@"^(?:yes|yep|yeah|sure)\s*[,!:]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AcknowledgementPrefix();

    [GeneratedRegex(@"\b(implement|build|fix|apply|execute|make|edit|write code)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImplementationRequest();

    [GeneratedRegex(@"\b(do not|don't|dont)\s+(edit|implement|modify)(?:\s+(yet|anything|any files|the code))?(?=\s*(?:[;,.!?]|$))|\bno\s+(edits|implementation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoEdits();

    [GeneratedRegex(@"\b(plan|proposal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlanWord();

    [GeneratedRegex(@"\b(rewrite|rearchitect|re-architect|overhaul|migrat\w*|architecture|major changes?|large[- ]scale|across (?:all|multiple|every)|entire (?:app|application|system|project|codebase)|whole (?:app|application|system|project|codebase))\b", RegexOptions.IgnoreCase)]
    private static partial Regex SubstantialWork();

    [GeneratedRegex(@"\bnew (?:subsystem|system|module|service)\b|\b(?:add|build|create|implement|introduce|support)\s+(?:(?:a|an|the|complete|full|new)\s+)*(?:features?\s+(?:for\s+)?)?(?:authentication|authorization|user accounts?|payments?|billing|subscriptions?|offline support|multi-?tenan\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LargeSubsystem();

    [GeneratedRegex(@"^(?:make (?:it|this|everything|the app) better|improve (?:it|this|everything|the app)|do whatever you think|whatever you think)[\s.!]*$|\b(?:not sure what|figure out what|requirements are unclear)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VagueRequest();

    [GeneratedRegex(@"^(?:implement|execute|apply|carry out)\s+(?:(?:the|this|that|approved|agreed)\s+)*plan\b", RegexOptions.IgnoreCase)]
    private static partial Regex ApprovedImplementation();

    [GeneratedRegex(@"\b(?:and|also|plus|then)\s+(?:add|build|rewrite|migrate|refactor|implement|create|redesign)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AdditionalScope();

    [GeneratedRegex(@"^(?:commit|push|run|test|retry|revert|undo)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoutineWork();

    [GeneratedRegex(@"^(?:fix|debug|rename|remove|delete|move|resize|center|align|color|colour|hide|disable|enable|format|tweak|adjust)\b|^(?:add|write|create)\s+(?:(?:a|an|the|status|unit|integration|regression)\s+)*(?:column|label|tooltip|log|logging|test|tests)\b|^(?:make|set|change|update)\b.*\b(?:blue|red|green|black|white|font|width|height|padding|margin|label|text|colour|color)\b|^(?:build|compile)\s+(?:the\s+)?(?:project|solution|release|package|tests)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BoundedChange();
}
