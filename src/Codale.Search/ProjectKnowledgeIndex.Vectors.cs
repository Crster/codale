using Codale.Core.Features;
using Codale.Core.Projects;

namespace Codale.Search;

/// <summary>Direct access to the chunk vectors, for callers that rank by meaning first and filter by words after.</summary>
public sealed partial class ProjectKnowledgeIndex
{
    /// <summary>True when a trained embedding model, not the local hashed n-gram embedder, makes the vectors.</summary>
    public bool HasEmbeddingModel => db.EmbeddingModel != HashedTextEmbedder.Instance.Model;

    /// <summary>
    /// The chunks nearest to <paramref name="text"/> by cosine, best first, from fresh source only. Only chunks
    /// that already have a vector from the current model take part: an index still embedding answers partially.
    /// </summary>
    public IReadOnlyList<(SourceChunk Chunk, float Similarity)> Similar(string worktree, string text, int limit = 60)
    {
        worktree = ProjectPaths.Normalize(worktree);
        Warm(worktree);
        var hits = new List<(SourceChunk, float)>();
        var fresh = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in ChunkRoot(worktree).Query(text, limit))
        {
            if (ReadChunk(hit.Node) is not { } chunk) continue;
            if (!fresh.TryGetValue(chunk.Path, out var valid)) fresh[chunk.Path] = valid = SourceMatches(worktree, chunk.Path, chunk.Hash);
            if (valid) hits.Add((chunk, hit.Score));
        }

        return hits;
    }

    /// <summary>How many of a worktree's chunks have a vector from the current model, of how many.</summary>
    public (int Embedded, int Total) VectorCoverage(string worktree)
    {
        // Chunk nodes are named LINE.ID; the nodes above them are the path's folders.
        var nodes = ChunkRoot(ProjectPaths.Normalize(worktree)).Descendants()
            .Where(n => n.Name.IndexOf('.') is > 0 and var dot && dot < n.Name.Length - 1 && n.Name.All(c => char.IsDigit(c) || c == '.')).ToList();
        return (nodes.Count(n => n.HasVector()), nodes.Count);
    }
}
