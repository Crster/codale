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
    /// a go-ahead, a one-word reply), shares no topic term with them or the agent's reply, and its embedding is not close
    /// to any of them. Every signal must agree: a false "New" costs context, a missed one costs little.
    /// </summary>
    public static async Task<bool> IsNewTopicAsync(string prompt, IReadOnlyList<string> earlier, ITextEmbedder embedder,
        string? lastAgentMessage = null, CancellationToken ct = default)
    {
        // "commit all changes" shares no term with the feature it commits, yet changes no subject.
        if (earlier.Count == 0 || MessageRouting.IsFollowUp(prompt) || !SearchText.HasTopic(prompt)) return false;
        var terms = SearchText.RetrievalTerms(prompt);
        if (terms.Count == 0) return false;

        // Both providers already deliver assistant prose. Reuse the latest reply as topic
        // evidence instead of spending another model call on extracting a summary.
        // Bound it so a long tool/report transcript cannot dominate similarity or latency.
        var context = earlier.TakeLast(3).ToList();
        if (!string.IsNullOrWhiteSpace(lastAgentMessage))
            context.Add(lastAgentMessage.Length > 1600 ? lastAgentMessage[..1600] : lastAgentMessage);

        foreach (var text in context)
        {
            var stems = SearchText.RetrievalTerms(text);
            if (terms.Any(t => t.Length >= 3 && stems.Any(s => s.StartsWith(t, StringComparison.Ordinal) || t.StartsWith(s, StringComparison.Ordinal) && s.Length >= 4)))
                return false;
        }

        // One request for all of them: a model behind a server pays per round trip, not per text.
        var texts = context.Select(text => string.Join(' ', SearchText.RetrievalTerms(text))).Prepend(string.Join(' ', terms)).ToList();
        var vectors = await embedder.EmbedAsync(texts, EmbedRole.Query, ct).ConfigureAwait(false);
        for (var i = 1; i < vectors.Count; i++)
        {
            if (VectorMath.Dot(vectors[0], vectors[i]) >= embedder.RelatedSimilarity) return false;
        }
        return true;
    }
}
