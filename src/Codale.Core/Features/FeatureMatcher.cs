namespace Codale.Core.Features;

/// <summary>A feature's searchable words: what <see cref="FeatureMatcher"/> scores a message against.</summary>
public sealed record FeatureDocument(FeatureInfo Feature, string Tokens);

/// <summary>One feature's match with a message.</summary>
/// <param name="Score">Share of the message's weight the feature accounts for, 0 to about 2 (name words count double).</param>
/// <param name="Matched">How many distinct message words the feature shares.</param>
/// <param name="NameHit">The feature's own name shares a word with the message.</param>
public sealed record FeatureMatch(FeatureInfo Feature, double Score, int Matched, bool NameHit);

/// <summary>
/// Scores a message against features without a model, for the cases the model cannot decide
/// (it is off, slow, or the project has too many features to show it all of them). One shared
/// word is not a match: a word counts by how rare it is across the project's features, and a
/// feature only wins when enough of the message's weight lands on it and it clearly beats the next.
/// </summary>
public static class FeatureMatcher
{
    private const int MinWordLength = 3;
    private const double MinCoverage = 0.30;
    private const double MinNameCoverage = 0.20;
    private const double MinLead = 1.25;

    /// <summary>Every feature that shares a meaningful word with the message, best first.</summary>
    public static IReadOnlyList<FeatureMatch> Rank(string message, IReadOnlyList<FeatureDocument> documents)
    {
        var words = MessageWords(message);
        if (words.Count == 0 || documents.Count == 0)
        {
            return [];
        }

        var sets = documents.Select(d => new HashSet<string>(SearchText.Tokens(d.Tokens), StringComparer.Ordinal)).ToList();
        var weight = words.ToDictionary(w => w, w => Idf(sets.Count, sets.Count(s => s.Contains(w))), StringComparer.Ordinal);

        // Words no feature has say nothing about which feature the message is for: they stay out of the share.
        var total = words.Where(w => sets.Any(s => s.Contains(w))).Sum(w => weight[w]);

        var matches = new List<FeatureMatch>();
        for (var i = 0; i < documents.Count; i++)
        {
            var nameWords = new HashSet<string>(SearchText.Tokens(documents[i].Feature.Name), StringComparer.Ordinal);
            double hit = 0;
            var matched = 0;
            var nameHit = false;
            foreach (var word in words.Where(sets[i].Contains))
            {
                matched++;
                hit += weight[word];
                if (nameWords.Contains(word))
                {
                    nameHit = true;
                    hit += weight[word];
                }
            }

            if (matched > 0 && total > 0)
            {
                matches.Add(new FeatureMatch(documents[i].Feature, hit / total, matched, nameHit));
            }
        }

        return [.. matches.OrderByDescending(m => m.Score).ThenBy(m => m.Feature.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The one feature the message is plainly about, or null. Needs two shared words (or the feature's own
    /// name), a real share of the message's weight, and a clear lead over the runner-up.
    /// </summary>
    public static FeatureInfo? Confident(string message, IReadOnlyList<FeatureDocument> documents)
    {
        var ranked = Rank(message, documents);
        if (ranked.Count == 0)
        {
            return null;
        }

        var best = ranked[0];
        var enough = best.NameHit
            ? best.Score >= MinNameCoverage
            : best.Matched >= 2 && best.Score >= MinCoverage;
        if (!enough)
        {
            return null;
        }

        return ranked.Count == 1 || best.Score >= ranked[1].Score * MinLead ? best.Feature : null;
    }

    /// <summary>The distinct words of the message that can tell features apart.</summary>
    private static List<string> MessageWords(string message) =>
        SearchText.Tokens(message)
            .Where(t => t.Length >= MinWordLength && !t.All(char.IsDigit) && !SearchText.IsStopWord(t))
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .ToList();

    /// <summary>Rarer words weigh more; a word in every feature weighs next to nothing.</summary>
    private static double Idf(int features, int withWord) =>
        Math.Log(1 + (features + 1.0) / (withWord + 0.5)) - Math.Log(1 + (features + 1.0) / (features + 0.5)) + 0.05;
}
