using System.Text.RegularExpressions;

namespace Codale.Core.Helper;

/// <summary>
/// How much work a message asks for, read from its words alone. Auto mode plans when the work is big, when it
/// is a new feature that a one-line prompt cannot specify ("implement login form"), or when it is too unclear to act on,
/// and otherwise leaves the message alone: a plan for a bounded change is an interruption.
/// </summary>
public static partial class MessageRouting
{
    /// <summary>Why a message is worth planning, and how sure the rule is. A strong reason needs no second opinion.</summary>
    internal readonly record struct PlanReason(string Why, bool Strong);

    /// <summary>The message as it is judged: IDE and browser wrappers, image markers, code fences and links removed.</summary>
    internal static string Normalize(string message)
    {
        var text = message;
        text = IdeWrapper().Replace(text, " ");
        text = CodeFence().Replace(text, " ");
        text = ImageMarker().Replace(text, " ");
        text = Link().Replace(text, "link");
        return text.Trim();
    }

    /// <summary>True when the message points at something the agent can see: a screenshot or an attached file.</summary>
    internal static bool HasAttachmentMarker(string message) => ImageMarker().IsMatch(message);

    /// <summary>
    /// The reason this message should be planned first, or null when it is a bounded change. Rules apply to the
    /// start of each sentence, after politeness and lead-ins ("lets", "next", "I want to"), so "good. next add a
    /// checkout flow" reads as the request it is.
    /// </summary>
    internal static PlanReason? ScopeOfWork(string message)
    {
        var text = Normalize(message);
        if (text.Length == 0) return null;

        foreach (var raw in SentenceBreak().Split(text))
        {
            var sentence = LeadIn().Replace(Codale.Core.Agents.SessionTitles.StripFillers(raw.Trim()), "").Trim();
            if (sentence.Length == 0) continue;
            // "I want a dashboard ..." asks for the same thing as "add a dashboard ...".
            sentence = WantsNoun().Replace(sentence, "add $1 ");
            if (Structural().IsMatch(sentence)) return new("a rewrite, migration or restructure", true);
            if (BroadQuality().IsMatch(sentence) || VagueImprovement().IsMatch(sentence)) return new("too broad to act on without a plan", true);
            // Each clause of "remove the old one and implement a new index" can be a request of its own.
            foreach (var clause in ClauseBreak().Split(sentence).Prepend(sentence))
            {
                var request = LeadIn().Replace(Codale.Core.Agents.SessionTitles.StripFillers(clause.Trim()), "").Trim();
                if (BuildCommand().IsMatch(request)) continue;
                if (NewFeature().IsMatch(request) || NewFeatureWord().IsMatch(request)) return new("a new feature", true);
            }
        }

        // Many separate changes in one long message, with nothing attached to show what is meant: a project, not a tweak.
        var words = CountWords(text);
        if (words >= 50 && !HasAttachmentMarker(message) && ChangeVerbs().Count(text) >= 5) return new("many changes in one request", false);
        return null;
    }

    [GeneratedRegex(@"<(?<tag>ide_[a-z_]+|browser_instruction|system-reminder)>.*?</\k<tag>>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IdeWrapper();

    /// <summary>"build the project", "compile the app": running the build, not creating something.</summary>
    [GeneratedRegex(@"^(?:build|compile|make)\s+(?:the\s+|this\s+)?(?:project|solution|app|application|release|package|code|tests?)[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex BuildCommand();

    [GeneratedRegex(@"^(?:i\s+)?(?:want|need|would\s+like)\s+(?:to\s+have\s+)?(a|an|some|new)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex WantsNoun();

    [GeneratedRegex(@"\s+(?:and|then|also|plus)\s+|,\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseBreak();

    [GeneratedRegex(@"```.*?(```|$)", RegexOptions.Singleline)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"\[(?:Image|File|Attachment)\s*#?\d*\]", RegexOptions.IgnoreCase)]
    private static partial Regex ImageMarker();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Link();

    /// <summary>"good", "next is", "then", "also": how a follow-up sentence leads into its request.</summary>
    [GeneratedRegex(@"^(?:(?:good|great|nice|perfect|correct|ok(?:ay)?|yes|yep|sure|alright|next(?: is| up)?|then|also|now|first|and|so|well|thanks?|but)\b[\s,:-]*)+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadIn();

    [GeneratedRegex(@"\b(?:add|create|build|implement|remove|replace|change|update|move|rename|fix|make|show|save|send|allow|use|apply|rebuild|calculate|delete|improve|restart|reduce)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChangeVerbs();

    private const string Verb = @"(?:implement|create|build|develop|introduce|add|make|write|design|set\s?up|integrate|support|enable|allow)";
    private const string Preposition = @"(?:to|in|on|into|from|at|of|for|with|next|beside|above|below|under|inside|before|after|by|as|when|while|if|so|that|which|where|because)";
    /// <summary>"implement this feature" approves what was discussed; it does not ask for a new one.</summary>
    private const string NotApproval = @"(?!(?:this|that|these|those|it|them|same|above|following|approved|agreed)\b)";

    private const string Determiners =@"(?:(?:a|an|the|new|full|complete|basic|simple|proper|whole|our|some|user|users)\s+){0,3}";
    /// <summary>Descriptive words before the noun. A preposition ends them: "a column to the database" is a change to a database.</summary>
    private const string Modifiers = @"(?:(?!" + Preposition + @"\b)[\w/'-]+\s+){0,3}?";

    /// <summary>Nouns that are a feature however the sentence goes on: "a billing system for tenants".</summary>
    private const string Head =
        @"authentication|authorization|oauth|sso|2fa|rbac|dashboard|workflows?|platform|framework|engine|integrations?|features?|functionality|capability|subsystem|system|module|" +
        @"access control|multi-?tenan\w*|i18n|localization|billing|subscriptions?";

    /// <summary>Nouns that are a feature when the phrase ends with them or goes straight on to what it is for.</summary>
    private const string Other =
        @"login|log-?in|sign-?in|sign-?up|registration|checkout|cart|notifications?|notification center|leaderboard|kanban(?: board)?|admin(?: panel| area)?|portal|onboarding|wizard|" +
        @"plugins?|collaboration|offline(?: support| mode)?|real-?time \w+|dark mode|multiple languages|activity feed|payments?|invoices?|analytics|reporting|reports?|recommendations?|" +
        @"backend|frontend|database|schema|todo app|app|application|website|game|blog|project|bot|generator|builder|editor|scheduler|tracker|index|indexer|indexing|" +
        @"flows?|page|form|screen|settings|search|chat|comments?|uploads?|downloads?|sync|caching|cache|api|endpoint|endpoints|service|tool|undo(?: and redo)?|redo|" +
        @"translations?|roles?|permissions?|feed|support|pipeline|deployment|ci(?:/cd)?";

    /// <summary>
    /// The thing is new and a feature in its own right: a creating verb, a short noun phrase, and a feature noun that ends
    /// it ("login form", "shopping cart", "oauth login with google"). A small element ("a delete button", "a column", "a
    /// tooltip") has no feature noun, and a noun followed by where it goes ("search box to the header") changes what exists.
    /// </summary>
    [GeneratedRegex(
        "^" + Verb + @"\s+" + NotApproval + Determiners + Modifiers + @"(?:(?:" + Other + @")\s+){0,2}(?:" + Head + @")\b" +
        "|" +
        "^" + Verb + @"\s+" + NotApproval + Determiners + Modifiers + @"(?:(?:" + Other + @")\s+){0,2}(?:" + Other + @")" +
        @"(?:\s+(?:with|for|using|so|that|which|where|and|from|of|including|supporting|via|based\s+on|to\s+(?:the\s+|our\s+|this\s+)?(?:app|application|project|site|website|codebase|system|platform))\b.*|[\s.!]*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NewFeature();

    /// <summary>"…a feature", "…functionality", "…a new module": the user's own word says it is a feature.</summary>
    [GeneratedRegex(
        @"^(?:implement|create|build|develop|introduce|add|make|write|design|set\s?up|integrate)\s+(?!(?:this|that|these|those|it|them|same|above|following|approved|agreed)\b)(?:[\w/'""-]+\s+){0,5}?(?:feature|functionality|capability|subsystem|module|framework|engine|platform)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NewFeatureWord();

    /// <summary>A rewrite, migration or restructure of something big: always worth a plan.</summary>
    [GeneratedRegex(
        @"\b(?:rewrite|re-?architect|overhaul|revamp|restructure|migrate|port)\b.*\b(?:whole|entire|all|everything|app|application|project|code ?base|backend|frontend|system|module|service|functionality|features?|from\b.+\bto\b|to\s+(?:the\s+)?(?:new|another|a\s+new)|into\s+(?:the\s+|a\s+)?new)\b" +
        @"|^(?:refactor|redesign|convert|re-?write)\b.*\b(?:whole|entire|everything|across\s+(?:all|the)|throughout|all of)\b" +
        @"|\b(?:convert|move)\s+(?:the\s+)?(?:whole|entire)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Structural();

    /// <summary>"improve the whole ui", "clean up the codebase", "work on the current code": no target the agent can pick out.</summary>
    [GeneratedRegex(
        @"^(?:improve|optimi[sz]e|enhance|polish|clean\s?up|refactor|upgrade|modernize|speed\s+up|work\s+on|tidy(?:\s+up)?|redesign|rework)\s+(?:up\s+)?(?:(?:the|our|this|my)\s+)?" +
        @"(?:(?:whole|entire|overall|current|existing)\s+)*" +
        @"(?:app|application|project|code\s?base|code|ui|ux|ui/ux|site|website|system|performance|design|look\s+and\s+feel|everything|things|stuff|onboarding experience|experience)\b" +
        @"(?:\s+(?:of|in|for)\s+(?:the\s+)?(?:app|application|project|code\s?base|site|website|system))?(?:\s+(?:and\s+)?(?:check|review|see|find|look|verify|make\s+sure)\b.*)?[\s.!]*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VagueImprovement();

    /// <summary>"make the app better / responsive / production ready": a quality of the whole, not a change to a thing.</summary>
    [GeneratedRegex(
        @"^make\s+(?:it|this|the\s+(?:app|application|project|site|website|ui|ux|code(?:\s?base)?|whole\s+\w+)|everything)\s+(?:work\s+)?" +
        @"(?:better|faster|nicer|prettier|modern|responsive|mobile[- ]friendly|secure|accessible|scalable|robust|production[- ]ready|professional|on\s+mobile|on\s+phones?|more\s+\w+)\b" +
        @"|^(?:redesign|rework|revamp)\s+(?:(?:it|this|that|the\s+page|the\s+screen)\s+)?to\s+be\s+(?:more|less|better)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex BroadQuality();
}
