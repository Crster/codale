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

    /// <summary>True for a word too common to say what a message is about ("please", "the").</summary>
    public static bool IsStopWord(string token) => Stop.Contains(token);

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