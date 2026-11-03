using Codale.Core.Helper;

namespace Codale.Core.Features;

/// <summary>
/// Tells whether a prompt changes the subject of the chat, locally and in microseconds, so the
/// prompt can be tagged "New" and its context retrieved for the new subject alone. It never moves
/// the prompt to another session: that stays the user's decision.
/// </summary>
public static class TopicShift
{
    /// <summary>
    /// New when the chat has earlier prompts, the message does not lean on them ("also…", "it still…",
    /// a go-ahead, a two-word reply), shares no topic term with them, and its embedding is not close
    /// to any of them. Every signal must agree: a false "New" costs context, a missed one costs little.
    /// </summary>
    public static bool IsNewTopic(string prompt, IReadOnlyList<string> earlier, ITextEmbedder embedder)
    {
        // "commit all changes" shares no term with the feature it commits, yet changes no subject.
        if (earlier.Count == 0 || MessageRouting.IsFollowUp(prompt) || !SearchText.HasTopic(prompt)) return false;
        var terms = SearchText.RetrievalTerms(prompt);
        if (terms.Count == 0) return false;

        foreach (var text in earlier)
        {
            var stems = SearchText.RetrievalTerms(text);
            if (terms.Any(t => t.Length >= 3 && stems.Any(s => s.StartsWith(t, StringComparison.Ordinal) || t.StartsWith(s, StringComparison.Ordinal) && s.Length >= 4)))
                return false;
        }

        var query = embedder.Embed(string.Join(' ', terms));
        foreach (var text in earlier)
        {
            var other = embedder.Embed(string.Join(' ', SearchText.RetrievalTerms(text)));
            if (VectorMath.Dot(query, other) >= embedder.RelatedSimilarity) return false;
        }
        return true;
    }
}
