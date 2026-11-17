using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Codale.Core.Features;
using Codale.Search.Engine;
using Codale.Smart;

namespace Codale.Search;

/// <summary>How the locator ranks: every retriever voting at once, or one stage narrowing the next.</summary>
public enum LocatorStrategy
{
    /// <summary>Source index, project index and the model's names ranked side by side and fused.</summary>
    Fusion,
    /// <summary>
    /// Model keywords → embedding search → keyword-index filter → rerank on both. A stage whose model is not
    /// configured is replaced by the index: no General model searches the prompt as written, no embedding model
    /// takes its candidates from the keyword index.
    /// </summary>
    Cascade,
}

public static partial class PromptFileLocator
{
    private const int CascadeCandidates = 30;
    private const int CascadeChunks = 80;
    /// <summary>Rerank weights: meaning, then words.</summary>
    private const double SimilarityWeight = 0.5;
    private const double LexicalWeight = 0.5;

    private static async Task<FileLocatorContext> CascadeAsync(string root, string text, SourceSnapshot snapshot,
        ProjectKnowledgeIndex? knowledge, IReadOnlySet<string>? knownFiles, ISmartExperience? smart, Action<Trace>? trace, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        // 1. The General model rewrites the request as generic keywords ("files panel new file enter key").
        IReadOnlyList<string> keywords = [];
        if (smart is not null) keywords = await KeywordsAsync(smart, root, text, ct).ConfigureAwait(false);
        var keywordMs = clock.ElapsedMilliseconds;
        // The request stays in the query: the keywords add what it means, its own words keep what it names.
        var query = keywords.Count > 0 ? text + Environment.NewLine + string.Join(", ", keywords) : text;

        var hints = Regex.Matches(Normal(text), @"[\w.-]+(?:/[\w.-]+)+").Select(m => m.Value.TrimEnd('.', '/')).ToList();
        var pathHints = hints.Select(h => h.Replace('/', Path.DirectorySeparatorChar)).ToList();
        bool ExplicitPath(string path) => hints.Any(h => path.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(h + "/", StringComparison.OrdinalIgnoreCase));
        var wantsData = DataIntent().IsMatch(text);
        var wantsTestsOrDocs = TestOrDocIntent().IsMatch(text);
        bool Eligible(string path)
        {
            if (knownFiles?.Contains(path) == true) return false;
            if (ExplicitPath(path)) return true;
            if (!ProjectKnowledgeIndex.Eligible(path)) return false;
            if (!wantsData && (DataPath().IsMatch(path) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) return false;
            return wantsTestsOrDocs || !CodeDiscovery.IsTestOrDoc(path);
        }

        // The words of the prompt and of the keywords, for the keyword index and for the line anchors.
        var asked = PromptTerms(text, snapshot.Contains);
        // The model's words count half as much as the user's own.
        foreach (var term in keywords.SelectMany(k => PromptTerms(k, snapshot.Contains)))
            if (!asked.Any(a => string.Equals(a.Text, term.Text, StringComparison.OrdinalIgnoreCase))) asked.Add(term with { Weight = term.Weight * 0.5 });
        var terms = SourceExplorer.Resolve(snapshot, asked).Where(t => t.Keys.Count > 0).ToList();
        var lexical = terms.Count == 0 ? [] : SourceExplorer.Rank(snapshot, terms, pathHints, setup: false);
        var lexicalScore = lexical.Where(r => Eligible(Normal(r.File.RelativePath)))
            .GroupBy(r => Normal(r.File.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(r => r.Score), StringComparer.OrdinalIgnoreCase);

        // 2. Candidates by meaning when an embedding model made the vectors; by the keyword index otherwise.
        var similarity = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var bestChunk = new Dictionary<string, SourceChunk>(StringComparer.OrdinalIgnoreCase);
        var embedded = knowledge?.HasEmbeddingModel == true;
        if (embedded)
        {
            IReadOnlyList<(SourceChunk Chunk, float Similarity)> similar;
            try { similar = await Task.Run(() => knowledge!.Similar(root, query, CascadeChunks), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { similar = []; }
            foreach (var (chunk, score) in similar)
            {
                var path = Normal(chunk.Path);
                if (!Eligible(path) || similarity.ContainsKey(path)) continue;
                similarity[path] = score;
                bestChunk[path] = chunk;
                if (similarity.Count == CascadeCandidates) break;
            }
        }
        var indexMs = clock.ElapsedMilliseconds;
        trace?.Invoke(new Trace(keywords, keywordMs, indexMs, keywordMs));
        ct.ThrowIfCancellationRequested();

        // 3. The keyword index filters: a candidate the words never reach is a neighbour in meaning only.
        //    Without vectors the keyword index is the candidate list itself.
        var candidates = embedded && similarity.Count > 0
            ? similarity.Keys.Where(p => lexicalScore.ContainsKey(p) || ExplicitPath(p)).ToList()
            : [.. lexicalScore.OrderByDescending(s => s.Value).Take(CascadeCandidates).Select(s => s.Key)];
        if (candidates.Count == 0 && similarity.Count > 0)
            candidates = [.. similarity.OrderByDescending(s => s.Value).Take(2).Select(s => s.Key)];
        if (candidates.Count == 0) return Empty;

        // 4. Rerank on both, each as a share of its best among the candidates.
        var topSimilarity = candidates.Select(p => similarity.GetValueOrDefault(p)).DefaultIfEmpty(0).Max();
        var topLexical = candidates.Select(p => lexicalScore.GetValueOrDefault(p)).DefaultIfEmpty(0).Max();
        var scores = candidates.ToDictionary(p => p, p =>
            (embedded && topSimilarity > 0 ? SimilarityWeight * similarity.GetValueOrDefault(p) / topSimilarity : 0) +
            (topLexical > 0 ? LexicalWeight * lexicalScore.GetValueOrDefault(p) / topLexical : 0) +
            (ExplicitPath(p) ? 1 : 0), StringComparer.OrdinalIgnoreCase);
        if (knowledge is not null) FollowGraph(knowledge, root, scores, Eligible);

        var best = scores.Values.Max();
        var output = new StringBuilder();
        var files = new List<string>();
        foreach (var (path, score) in scores.OrderByDescending(s => s.Value))
        {
            if (files.Count == MaxFiles || score < best * Floor) break;
            var file = snapshot.Files.FirstOrDefault(f => Normal(f.RelativePath).Equals(path, StringComparison.OrdinalIgnoreCase));
            if (file is null || !File.Exists(file.FullPath)) continue;
            if (Entry(file, path, terms, bestChunk.GetValueOrDefault(path)) is not { } entry) continue;
            if (Encoding.UTF8.GetByteCount(output.ToString() + entry) > MaxBytes) break;
            output.Append(entry);
            files.Add(path);
        }

        return files.Count == 0 ? Empty : new(
            "Likely edit sites, from a local search of the project (path:lines | matched words, then the matching lines). " +
            "These are candidate references, not verified edit sites; check their relevance and search beyond them as needed.\n" + output + "\n", files);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<string>> Keywords = new(StringComparer.Ordinal);

    /// <summary>The model's search keywords for a request; empty when it is off, slow or fails.</summary>
    private static async Task<IReadOnlyList<string>> KeywordsAsync(ISmartExperience smart, string root, string text, CancellationToken ct)
    {
        var key = ProjectKnowledgeIndex.Hash(root + "\n" + text);
        if (Keywords.TryGetValue(key, out var cached)) return cached;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ExpansionBudget);
            var context = new SmartRequestContext(root, root, "keywords", Deadline: DateTimeOffset.UtcNow + ExpansionBudget);
            var result = await smart.SearchKeywordsAsync(context, text, budget.Token).ConfigureAwait(false);
            if (result?.Value is not { Count: > 0 } terms) return [];
            if (Keywords.Count > 128) Keywords.Clear();
            Keywords[key] = terms;
            return terms;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
    }
}
