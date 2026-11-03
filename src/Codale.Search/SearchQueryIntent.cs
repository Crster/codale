using System.Text.RegularExpressions;

namespace Codale.Search;

/// <summary>Distinguishes direct code searches from questions and descriptions that need interpretation.</summary>
public static class SearchQueryIntent
{
    private static readonly HashSet<string> SentenceStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "what", "which", "how", "why", "who", "when",
        "does", "do", "did", "is", "are", "was", "were",
        "can", "could", "should", "would", "will",
        "find", "show", "explain", "tell", "locate",
    };

    private static readonly HashSet<string> SentenceWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "this", "that", "these", "those", "my", "our",
        "i", "we", "it", "its", "when", "where", "why", "because",
        "is", "are", "was", "were", "does", "doesn't", "don't", "isn't",
    };

    /// <summary>
    /// Questions and grammatical descriptions use discovery; identifiers, paths, quoted
    /// text and keyword lists use direct ranking. Length alone does not make a sentence.
    /// </summary>
    public static bool IsNaturalLanguage(string query)
    {
        var unquoted = CodeDiscovery.WithoutQuoted(query);
        if (unquoted.Contains('?'))
        {
            return true;
        }

        var words = unquoted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(',', '.', '!', ':', ';', '(', ')'))
            .Where(w => w.Length > 0)
            .ToList();

        var hasSubject = words.Count > 1 || (words.Count == 1 && CodeDiscovery.QuotedPhrases(query).Count > 0);
        return hasSubject &&
            (SentenceStarts.Contains(words[0]) || words.Any(SentenceWords.Contains));
    }

    /// <summary>Requests for the source to edit need a verified edit site, not a file-level pick.</summary>
    public static bool WantsEditLocation(string query) => IsNaturalLanguage(query) &&
        Regex.IsMatch(CodeDiscovery.WithoutQuoted(query),
            @"\b(chang(?:e|ing)|edit(?:ing)?|replac(?:e|ing)|customi[sz](?:e|ing)|updat(?:e|ing)|modif(?:y|ying)|rename|adjust|configure)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Keep literal code words, including words that would be filler in a question.</summary>
    internal static IEnumerable<string> Keywords(string query) =>
        Regex.Matches(query, @"[\p{L}_][\p{L}\p{N}_]*", RegexOptions.CultureInvariant)
            .Select(m => m.Value)
            .Where(w => w.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20);
}
