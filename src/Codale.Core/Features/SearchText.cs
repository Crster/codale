using System.Text;

namespace Codale.Core.Features;

/// <summary>
/// Turns code and prose into the tokens full-text search matches on: words, plus the parts of
/// identifiers, so "LoginService" and "login_service" are both found by "login".
/// </summary>
public static class SearchText
{
    private const int MaxQueryTokens = 24;
    private const int MinTokenLength = 2;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "that", "this", "with", "from", "not", "are", "was", "but", "can", "you", "our",
        "please", "just", "have", "has", "had", "its", "into", "also", "all", "any", "how", "why", "what",
        "when", "where", "which", "should", "would", "could", "need", "want", "make", "let", "lets", "get",
        "to", "of", "in", "is", "it", "on", "at", "as", "be", "by", "do", "if", "or", "so", "we", "an", "my", "me", "no",
    };

    /// <summary>Question filler that says how a prompt asks, not what about ("where is X located?").</summary>
    private static readonly HashSet<string> QuestionFiller = new(StringComparer.Ordinal)
    {
        "does", "did", "done", "doing", "located", "locate", "location", "find", "found", "show", "tell", "explain",
        "new", "use", "used", "using", "there", "here", "about", "like", "some", "will", "been", "were", "see",
        "look", "check", "now", "then", "them", "they", "than", "only", "still", "even", "same", "because", "feel",
        "think", "know", "sure", "yes", "ok", "okay", "thing", "things", "stuff", "code", "file", "files", "work",
        "works", "working", "currently", "actually", "really", "maybe", "where's", "whats", "who", "your",
        // Request phrasing: how a change is asked for, not what it is about ("fix the hall menu").
        "fix", "change", "improve", "good", "great", "nice", "better", "best", "easy", "able", "many", "more",
        "less", "much", "very", "try", "trying", "having", "going", "before", "after", "real", "put", "instead",
        "directly", "those", "off", "out", "way", "give", "take", "keep", "seem", "right", "wrong", "dont", "don't",
        "doesnt", "isnt", "cant", "won't", "wont", "one", "something", "everything", "anything", "again",
        "chang", "improv", // stems of "changed", "improved"
    };

    /// <summary>True for a word too common to say what a message is about ("please", "the").</summary>
    public static bool IsStopWord(string token) => Stop.Contains(token);

    /// <summary>
    /// A light English stem so inflections meet: "connectors" → "connector", "indexing" → "index",
    /// "located" → "locat". Deliberately conservative: it only has to make forms collide, not be a word.
    /// </summary>
    public static string Stem(string token)
    {
        if (token.Length <= 4 || !token.All(char.IsLetter)) return token;
        if (token.EndsWith("ies") && token.Length > 5) return token[..^3] + "y";
        if (token.EndsWith("ing") && token.Length > 6) return token[..^3];
        if (token.EndsWith("ed") && token.Length > 5) return token[..^2];
        if (token.EndsWith("es") && token.Length > 5 && "sxz".Contains(token[^3])) return token[..^2];
        if (token.EndsWith('s') && !token.EndsWith("ss") && !token.EndsWith("us") && !token.EndsWith("is")) return token[..^1];
        return token;
    }

    /// <summary>
    /// Attachment mentions that name no project file: pasted images and text ("@5f469e.png") and any
    /// image path. Their hash fragments and "png" would otherwise rank asset manifests and generators.
    /// A mention of a source file ("@server/game/units.go") is kept: it is the strongest locator there is.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AttachmentMention = new(
        @"@(?:""[^""]*\.(?:png|jpe?g|gif|webp|bmp|ico|tiff?|heic)""|[^\s""]*\.(?:png|jpe?g|gif|webp|bmp|ico|tiff?|heic)\b|" +
        @"""(?:[^""]*[\\/])?[0-9a-f]{6}\.txt""|(?:[^\s""]*[\\/])?[0-9a-f]{6}\.txt\b)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The prompt without attachment mentions that say nothing about the subject.</summary>
    public static string WithoutAttachments(string text) => AttachmentMention.Replace(text, " ");

    /// <summary>
    /// Words that run the session or the repository rather than name a subject: "commit all changes",
    /// "continue in full permission mode". A prompt made only of these has no topic of its own.
    /// </summary>
    private static readonly HashSet<string> Workflow = new(StringComparer.Ordinal)
    {
        "commit", "push", "pull", "merge", "rebase", "stash", "branch", "amend", "squash", "pr",
        "continue", "proceed", "go", "ahead", "resume", "finish", "stop", "wait", "retry", "undo", "redo", "revert",
        "permission", "mode", "full", "auto", "bypass", "accept", "approve", "approv", "plan",
    };

    /// <summary>
    /// True when the prompt names something to look up; false for pure workflow ("commit this",
    /// "continue in full permission mode"), whose subject is the conversation so far.
    /// </summary>
    public static bool HasTopic(string text) => RetrievalTerms(text).Any(t => !Workflow.Contains(t));

    /// <summary>The meaningful stems of a prompt for project retrieval, most frequent first.</summary>
    public static IReadOnlyList<string> RetrievalTerms(string text) => Tokens(WithoutAttachments(text))
        .Where(t => t.Length >= MinTokenLength && !Stop.Contains(t) && !QuestionFiller.Contains(t))
        .Select(Stem)
        // Inflected filler ("changes", "improved") is still filler once stemmed.
        .Where(t => !QuestionFiller.Contains(t))
        .GroupBy(t => t)
        .OrderByDescending(g => g.Count())
        .Select(g => g.Key)
        .Take(MaxQueryTokens)
        .ToList();

    /// <summary>
    /// A match expression for project retrieval: question filler dropped, and each stem of four or more
    /// characters matched as a prefix so "connectors" finds "connector" and "ConnectorTable".
    /// </summary>
    public static string? ToRetrievalQuery(string text)
    {
        var terms = RetrievalTerms(text);
        return terms.Count == 0 ? null : string.Join(" OR ", terms.Select(t => t.Length >= 4 ? $"\"{t}\"*" : $"\"{t}\""));
    }

    /// <summary>The tokens of <paramref name="text"/> in order: each word whole, then its camelCase / snake_case parts.</summary>
    public static IEnumerable<string> Tokens(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
                continue;
            }

            foreach (var token in Flush(word))
            {
                yield return token;
            }
        }

        foreach (var token in Flush(word))
        {
            yield return token;
        }
    }

    /// <summary>The text as it is stored for search: its tokens, space separated.</summary>
    public static string ForIndex(string text) => string.Join(' ', Tokens(text));

    /// <summary>
    /// A match expression finding any of the text's meaningful tokens (the most frequent first
    /// when there are many); null when the text has none.
    /// </summary>
    public static string? ToMatchQuery(string text)
    {
        var tokens = Tokens(text)
            .Where(t => t.Length >= MinTokenLength && !Stop.Contains(t))
            .GroupBy(t => t)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(MaxQueryTokens)
            .ToList();

        return tokens.Count == 0 ? null : string.Join(" OR ", tokens.Select(t => $"\"{t}\""));
    }

    private static IEnumerable<string> Flush(StringBuilder word)
    {
        if (word.Length == 0)
        {
            yield break;
        }

        var text = word.ToString();
        word.Clear();

        var parts = SplitCamel(text);
        yield return text.ToLowerInvariant();

        if (parts.Count > 1)
        {
            foreach (var part in parts)
            {
                yield return part;
            }
        }
    }

    /// <summary>"LoginService2" gives login, service, 2; an acronym run stays together ("HTTPServer" gives http, server).</summary>
    private static List<string> SplitCamel(string word)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 1; i < word.Length; i++)
        {
            var prev = word[i - 1];
            var c = word[i];
            var boundary =
                (char.IsLower(prev) && char.IsUpper(c)) ||
                (char.IsLetter(prev) != char.IsLetter(c)) ||
                (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < word.Length && char.IsLower(word[i + 1]));

            if (boundary)
            {
                parts.Add(word[start..i].ToLowerInvariant());
                start = i;
            }
        }

        parts.Add(word[start..].ToLowerInvariant());
        return parts;
    }
}