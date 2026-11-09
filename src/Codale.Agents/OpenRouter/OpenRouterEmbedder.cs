using Codale.Core.Features;

namespace Codale.Agents.OpenRouter;

/// <summary>
/// Project retrieval and topic detection on an OpenRouter embedding model. Vectors are stored under
/// <see cref="Model"/> (<c>openrouter:&lt;id&gt;</c>), so picking another model re-embeds the index.
/// </summary>
/// <remarks>
/// Cosine scales differ between models, so <see cref="EmbeddingFamily"/> holds starting bars for the
/// common ones and a conservative default for the rest, plus the task prefixes each was trained with.
/// </remarks>
public sealed class OpenRouterEmbedder(OpenRouterClient client, string model) : ITextEmbedder
{
    /// <summary>Texts per request.</summary>
    public const int BatchSize = 64;

    private readonly EmbeddingFamily _family = EmbeddingFamily.For(model);

    public string Name => model;

    public string Model => "openrouter:" + model;

    public float RelatedSimilarity => _family.Related;

    public float MatchSimilarity => _family.Match;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbedRole role, CancellationToken ct = default)
    {
        var prefix = role == EmbedRole.Query ? _family.QueryPrefix : _family.DocumentPrefix;
        var vectors = new List<float[]>(texts.Count);
        for (var start = 0; start < texts.Count; start += BatchSize)
        {
            var batch = texts.Skip(start).Take(BatchSize).Select(t => prefix + t).ToList();
            vectors.AddRange(await client.EmbedAsync(model, batch, ct).ConfigureAwait(false));
        }

        return vectors;
    }
}

/// <summary>An embedding model family's cosine bars and task prefixes.</summary>
internal sealed record EmbeddingFamily(float Related, float Match, string QueryPrefix, string DocumentPrefix)
{
    private static readonly EmbeddingFamily Default = new(0.55f, 0.5f, "", "");

    private static readonly (string Name, EmbeddingFamily Family)[] Known =
    [
        ("nomic-embed", new(0.62f, 0.55f, "search_query: ", "search_document: ")),
        ("embeddinggemma", new(0.5f, 0.45f, "task: code retrieval | query: ", "title: none | text: ")),
        ("qwen3-embedding", new(0.55f, 0.5f, "Instruct: Given a question about a codebase, retrieve the relevant code or documentation\nQuery: ", "")),
        ("mxbai-embed", new(0.62f, 0.55f, "Represent this sentence for searching relevant passages: ", "")),
        ("snowflake-arctic-embed", new(0.55f, 0.5f, "Represent this sentence for searching relevant passages: ", "")),
        ("text-embedding-3", new(0.45f, 0.4f, "", "")),
        ("text-embedding-004", new(0.55f, 0.5f, "", "")),
        ("bge-m3", new(0.55f, 0.5f, "", "")),
        ("all-minilm", new(0.45f, 0.35f, "", "")),
    ];

    public static EmbeddingFamily For(string model)
    {
        var name = model.Trim().ToLowerInvariant();
        foreach (var (prefix, family) in Known)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) || name.Contains("/" + prefix, StringComparison.Ordinal))
            {
                return family;
            }
        }

        return Default;
    }
}
