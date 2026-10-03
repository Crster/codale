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

/// <summary>The router's verdict on one message: the tidied text, its intent, and whether it starts a new topic.</summary>
public sealed record RouteDecision(string Text, RouteIntent Intent, bool NewTopic);

/// <summary>
/// Automatic mode's single helper-model call per message: tidy the wording, pick the
/// mode for the turn, and notice a change of subject. Prompt, tool and interpretation
/// live here so the rules are testable without a model; the call itself is the app's.
/// </summary>
public static partial class MessageRouting
{
    private const int MaxMessageChars = 4000;
    private const int MaxTurnChars = 400;
    private const int MaxAssistantChars = 800;

    public const string SystemPrompt =
        "You prepare a developer's chat message for a coding agent. Call the route tool once. " +
        "Never answer the message, never ask for more information, and never talk to the user.\n" +
        "message: the user's message, corrected and made clear. Fix every spelling, grammar and punctuation " +
        "mistake, and every wrong, doubled or missing word. Where a thought is tangled, run-on or hard to " +
        "follow, restate it so it reads clearly - in the user's own voice (first person, same tone), with their " +
        "points in their order. Keep all of the meaning: do not add requirements, steps, examples or " +
        "assumptions, do not drop anything, and do not summarise. Keep every code snippet, file path, " +
        "identifier, command, URL and number exactly as written. A message that is already clear comes back " +
        "unchanged.\n" +
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
        "related: yes when the message continues, follows up on, gives feedback on or refers to the current " +
        "session's work (\"it\", \"this\", \"that\", \"also\", \"again\", \"still\"), or when there is no session " +
        "yet. no when it is a separate task about a different feature, screen, file or problem than the " +
        "session's title and messages, with nothing tying it to them - for example a session fixing the login " +
        "redirect, then \"add a dark theme to the settings page\". When unsure, answer yes.";

    public static readonly ToolDefinition RouteTool = new()
    {
        Name = "route",
        Description = "Hand the corrected message to the agent with its intent.",
        Parameters =
        [
            new ToolParameter { Name = "message", Description = "the message, corrected and clear", Required = true },
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
    /// The context block the model reads: the session so far, then the new message. The
    /// session's opening message is kept when it has scrolled out of the recent turns,
    /// since it says best what the session is about.
    /// </summary>
    public static string BuildConversation(
        string message,
        string? sessionTitle,
        IReadOnlyList<string> recentUserTurns,
        string? lastAssistantText,
        string? firstUserTurn = null)
    {
        var sb = new StringBuilder();

        if (recentUserTurns.Count == 0)
        {
            sb.AppendLine("Current session: none yet (this is the first message).");
        }
        else
        {
            sb.AppendLine("Current session:");
            if (sessionTitle is { Length: > 0 })
            {
                sb.Append("Title: ").AppendLine(Clip(sessionTitle, MaxTurnChars));
            }

            if (firstUserTurn is { Length: > 0 } && !recentUserTurns.Contains(firstUserTurn))
            {
                sb.Append("First message: ").AppendLine(Clip(firstUserTurn, MaxTurnChars));
                sb.AppendLine("...");
            }

            foreach (var turn in recentUserTurns)
            {
                sb.Append("User: ").AppendLine(Clip(turn, MaxTurnChars));
            }

            if (lastAssistantText is { Length: > 0 })
            {
                sb.Append("Agent: ").AppendLine(Clip(lastAssistantText, MaxAssistantChars));
            }
        }

        sb.AppendLine();
        sb.AppendLine("New message:");
        sb.Append(Clip(message, MaxMessageChars));
        return sb.ToString();
    }

    /// <summary>
    /// A message that needs no model: a bare go-ahead ("yes", "do it", "continue") has
    /// nothing to correct, is always act and always belongs where it was typed. Null
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
    /// Turns the model's call into a decision, distrusting it wherever it could hurt:
    /// a rewrite that shrinks, balloons or loses code falls back to the original words.
    /// </summary>
    public static RouteDecision Interpret(ToolCall? call, string original, bool hasConversation)
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
        var newTopic = hasConversation &&
            string.Equals(call.GetString("related")?.Trim(), "no", StringComparison.OrdinalIgnoreCase) &&
            !IsFollowUp(original);

        var text = call.GetString("message")?.Trim() ?? "";
        if (!IsFaithful(text, original))
        {
            text = original;
        }

        // A small model leans ask, and an ask turn refuses every edit, so ask has to be
        // earned: the message must read as a question and carry no change request. A
        // statement ("the header is ugly"), a go-ahead or a question mixed with a
        // request all go out as act. The tidied text counts too, so a typo in the
        // opening word ("wat does it do") does not turn a question into an act.
        if (intent == RouteIntent.Ask &&
            (IsChangeRequest(original) || !(IsQuestion(original) || IsQuestion(text))))
        {
            intent = RouteIntent.Act;
        }

        return new RouteDecision(text, intent, newTopic);
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

    /// <summary>
    /// A rewrite is kept only when it is about the same size, keeps every piece of code
    /// and keeps most of the user's words. A corrected spelling counts as the same word.
    /// </summary>
    public static bool IsFaithful(string rewrite, string original)
    {
        if (rewrite.Length == 0)
        {
            return false;
        }

        // Short messages ("fix it") have room to grow; a ratio alone would refuse them.
        var ratio = (double)rewrite.Length / Math.Max(original.Length, 1);
        if (original.Length >= 40 && (ratio < 0.5 || ratio > 1.8))
        {
            return false;
        }

        if (original.Length < 40 && rewrite.Length > 100)
        {
            return false;
        }

        // A correction keeps the user's words: most of theirs survive, and few new ones
        // arrive. A rewrite that swaps the vocabulary has changed the content.
        var originalWords = Words(original);
        var rewriteWords = Words(rewrite);
        if (originalWords.Count > 0 && rewriteWords.Count > 0 &&
            (Overlap(originalWords, rewriteWords) < MinWordOverlap || Overlap(rewriteWords, originalWords) < MinWordOverlap))
        {
            return false;
        }

        foreach (Match code in CodePattern().Matches(original))
        {
            if (!rewrite.Contains(code.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private const double MinWordOverlap = 0.5;

    /// <summary>Lower-case words with apostrophes dropped, so "let's" and "lets" match.</summary>
    private static List<string> Words(string text) =>
        [.. WordPattern().Matches(text.Replace("'", "").Replace("’", "")).Select(m => m.Value.ToLowerInvariant())];

    private static int CountWords(string text) => WordPattern().Count(text);

    /// <summary>The share of <paramref name="from"/>'s words that also appear in <paramref name="to"/>, typos forgiven.</summary>
    private static double Overlap(List<string> from, List<string> to)
    {
        var present = to.ToHashSet(StringComparer.Ordinal);
        return (double)from.Count(word => present.Contains(word) || present.Any(other => IsSpellingOf(word, other))) / from.Count;
    }

    /// <summary>
    /// True when two words are one spelling fix apart: "buton" and "button", "teh" and
    /// "the", "stitch" and "stitched". They must share a first letter, so "design" and
    /// "redesign" stay different words.
    /// </summary>
    private static bool IsSpellingOf(string a, string b)
    {
        if (a.Length < 3 || b.Length < 3 || a[0] != b[0])
        {
            return false;
        }

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length >= 4 && longer.StartsWith(shorter, StringComparison.Ordinal))
        {
            return true;
        }

        var allowed = shorter.Length >= 7 ? 2 : 1;
        return longer.Length - shorter.Length <= allowed && EditDistance(a, b, allowed) <= allowed;
    }

    /// <summary>Edit distance with swapped neighbours as one edit; stops counting past <paramref name="limit"/>.</summary>
    private static int EditDistance(string a, string b, int limit)
    {
        var previous2 = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    current[j] = Math.Min(current[j], previous2[j - 2] + 1);
                }

                rowBest = Math.Min(rowBest, current[j]);
            }

            if (rowBest > limit)
            {
                return rowBest;
            }

            (previous2, previous, current) = (previous, current, previous2);
        }

        return previous[b.Length];
    }

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

    /// <summary>Fenced blocks and inline backticks: text the rewrite must carry verbatim.</summary>
    [GeneratedRegex(@"```[\s\S]*?```|`[^`\n]+`")]
    private static partial Regex CodePattern();
}
