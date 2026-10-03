using System.Text;
using System.Text.RegularExpressions;

using Codale.Core.Text;

namespace Codale.Core.Helper;

/// <summary>What a message is asking for, as the router reads it.</summary>
public enum RouteIntent
{
    /// <summary>A question: explain, change nothing.</summary>
    Ask,

    /// <summary>Multi-step or architectural work: research and propose first.</summary>
    Plan,

    /// <summary>A direct change: just do it.</summary>
    Act,
}

/// <summary>The router's verdict on one message: the message as typed, its intent, and whether it starts a new topic.</summary>
public sealed record RouteDecision(string Text, RouteIntent Intent, bool NewTopic);

/// <summary>
/// Automatic mode's single helper-model call per message: pick the mode for the turn
/// and notice a change of subject. The message itself goes out as typed - the model
/// writes back a few words, not the message. Prompt, tool and interpretation live here
/// so the rules are testable without a model; the call itself is the app's.
/// </summary>
public static partial class MessageRouting
{
    private const int MaxMessageChars = 4000;
    private const int MaxAssistantChars = 800;

    /// <summary>
    /// The shortest agent reply that says enough about the session to judge a change of
    /// subject against; below it (a bare "Done.") the message stays where it was typed.
    /// </summary>
    public const int MinSummaryChars = 80;

    public const string SystemPrompt =
        "You classify a developer's chat message for a coding agent. Call the route tool once. " +
        "Never answer the message, never ask for more information, and never talk to the user.\n" +
        "intent: act is the default. Choose act for any request to make, change, fix, add, remove, improve or " +
        "redesign something, however it is phrased (\"let's make X nicer\", \"can you fix Y\", \"X should look " +
        "more like Z\"), for a problem report or complaint (\"the button is misaligned\", \"there is a dot before " +
        "the name, remove it\", \"X doesn't work\", \"next issue is Y\"), for a wish (\"I want X\", \"it needs X\"), " +
        "and for short go-aheads (\"yes\", \"do it\", \"go ahead\", \"continue\", \"looks good, implement it\"). " +
        "plan - a large, multi-step, multi-file or architectural change, or one the user wants planned first. " +
        "ask - ONLY a pure question that wants an answer and no change at all (what, why, how does, where is, " +
        "explain, review, compare) and asks for nothing to be done. A message that mixes a question with a " +
        "request (\"why is X slow? speed it up\") is act. When a message asks for a change, it is never ask, " +
        "and when unsure between ask and act, choose act.\n" +
        "related: yes when the message continues, follows up on, gives feedback on or refers to the work the " +
        "last agent message describes (\"it\", \"this\", \"that\", \"also\", \"again\", \"still\"), or when there " +
        "is no agent message. no when it is a separate task about a different feature, screen, file or problem " +
        "than that work, with nothing tying it to it - for example an agent message about fixing the login " +
        "redirect, then \"add a dark theme to the settings page\". When unsure, answer yes.";

    public static readonly ToolDefinition RouteTool = new()
    {
        Name = "route",
        Description = "Pick the mode for the message and say whether it belongs to the current session.",
        Parameters =
        [
            new ToolParameter
            {
                Name = "intent",
                Description = "ask, plan or act",
                Required = true,
                AllowedValues = ["ask", "plan", "act"],
            },
            new ToolParameter
            {
                Name = "related",
                Description = "whether the message belongs to the current session",
                Required = true,
                AllowedValues = ["yes", "no"],
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
            sb.Append("Last agent message: ").AppendLine(Clip(lastAssistantText, MaxAssistantChars));
            sb.AppendLine();
        }

        sb.AppendLine("New message:");
        sb.Append(Clip(message, MaxMessageChars));
        return sb.ToString();
    }

    /// <summary>
    /// A message that needs no model: a bare go-ahead ("yes", "do it", "continue") is
    /// always act and always belongs where it was typed. Null
    /// means the model should read it.
    /// </summary>
    public static RouteDecision? TryRouteLocally(string message)
    {
        var text = message.Trim();
        if (text.Length <= 40 && GoAheadPattern().IsMatch(text) && CountWords(text) <= 4)
        {
            return new RouteDecision(text, RouteIntent.Act, NewTopic: false);
        }

        return null;
    }

    /// <summary>
    /// The decision when the model could not give one: the words as typed, a pure
    /// question as ask and everything else as act, in the session it was typed in.
    /// </summary>
    public static RouteDecision RouteWithoutModel(string message) =>
        new(message, IsQuestion(message) && !IsChangeRequest(message) ? RouteIntent.Ask : RouteIntent.Act, NewTopic: false);

    /// <summary>
    /// Turns the model's call into a decision for the message as typed, distrusting it
    /// wherever it could hurt. <paramref name="canMoveTopic"/> is false when there is no
    /// agent reply worth comparing against, and the message then never moves.
    /// </summary>
    public static RouteDecision Interpret(ToolCall? call, string original, bool canMoveTopic)
    {
        if (call is null)
        {
            return RouteWithoutModel(original);
        }

        var intent = call.GetString("intent")?.Trim().ToLowerInvariant() switch
        {
            "ask" => RouteIntent.Ask,
            "plan" => RouteIntent.Plan,
            _ => RouteIntent.Act,
        };

        // Moving a message to a new session is the costly mistake - the agent loses the
        // context it needs - so a message that plainly points back never moves.
        var newTopic = canMoveTopic &&
            string.Equals(call.GetString("related")?.Trim(), "no", StringComparison.OrdinalIgnoreCase) &&
            !IsFollowUp(original);

        // A small model leans ask, and an ask turn refuses every edit, so ask has to be
        // earned: the message must read as a question and carry no change request. A
        // statement ("the header is ugly"), a go-ahead or a question mixed with a
        // request all go out as act.
        if (intent == RouteIntent.Ask && (IsChangeRequest(original) || !IsQuestion(original)))
        {
            intent = RouteIntent.Act;
        }

        return new RouteDecision(original, intent, newTopic);
    }

    /// <summary>
    /// True when the message leans on what came before: a go-ahead, a reply opening
    /// with "also", "it", "that" or "try again", a word like "still" or "again", or
    /// too few words to be a subject of its own.
    /// </summary>
    public static bool IsFollowUp(string message)
    {
        var text = PolitenessPattern().Replace(message.Trim(), "");
        return CountWords(text) < 3 ||
            GoAheadPattern().IsMatch(text) ||
            FollowUpOpener().IsMatch(text) ||
            BackReference().IsMatch(text);
    }

    private static int CountWords(string text) => WordPattern().Count(text);

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex WordPattern();

    /// <summary>
    /// True when the message, past its politeness ("can you", "let's", "please"), opens
    /// with a verb that changes something. "How do I make..." opens with "how" and stays a question.
    /// </summary>
    public static bool IsChangeRequest(string message)
    {
        if (GoAheadPattern().IsMatch(message.Trim()))
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
        return QuestionOpener().IsMatch(text) || message.TrimEnd().EndsWith('?');
    }

    [GeneratedRegex(@"[.!?;\n]+\s*")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(
        @"^(yes|yep|yeah|ok|okay|sure|go ahead|do it|proceed|continue|sounds good|looks good|lgtm|approved?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex GoAheadPattern();

    [GeneratedRegex(@"^((please|pls|plz|hey|hmm+|well|so)\b[\s,.!]*)+", RegexOptions.IgnoreCase)]
    private static partial Regex PolitenessPattern();

    [GeneratedRegex(
        @"^(also|and|plus|now|then|next|again|still|instead|but|actually|nope|no|not|wait|" +
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
        @"explain|describe|tell me|show me|walk me|summari[sz]e|review|compare|list|find out|look into|check whether|" +
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
        @"optimi[sz]e|speed it up|speed up|simplify|extract|split|merge|install|upgrade|bump|commit|push|run|deploy|" +
        @"enable|disable|turn|hide|put|drop|revert|undo|reset|generate|migrate|port|wire|hook|support|" +
        @"allow|prevent|stop|handle|translate|format|resize|center|align|color|colour)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChangeVerbPattern();
}
