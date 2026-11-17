using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Codale.Core.Features;
using Codale.Search.Engine;

namespace Codale.Search;

/// <summary>
/// Short keyword search (<c>public static void</c>, <c>RetryPolicy</c>). The pool is every file the source index
/// holds any of the words in, plus what the knowledge index's semantic search retrieves. A file ranks by its
/// keyword likeness in chain order: the longest run of the query's words it holds together and in the query's
/// order ("public static void" whole, then "public static" or "static void", then the words apart), then how many
/// of the words it holds, how often it holds that run, and finally the semantic and BM25 ranks.
/// </summary>
public sealed partial class SourceExplorer
{
    private const int MaxChainCandidates = 600;
    private const int MaxChainSemantic = 20;
    private const int MaxChainLinesPerFile = 12;

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex ChainWord();

    /// <summary>One file's likeness: its longest in-order run, the words it holds, and the lines holding the run.</summary>
    private sealed record ChainScore(SourceFile File, int Chain, int Words, int ChainLines, string Run, Dictionary<int, int> Weights);

    /// <summary>The ranked files; null when neither index holds anything for the query.</summary>
    private async Task<DiscoveryResult?> ChainSearchAsync(SourceSnapshot snapshot, string text, CancellationToken ct)
    {
        var words = ChainWord().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToArray();
        if (words.Length == 0) return null;

        // Semantic retrieval runs beside the keyword pass.
        var semanticTask = Knowledge is not null && UseSemanticSearch
            ? Knowledge.SearchAsync(_index.Root, text, maxFiles: MaxChainSemantic, ct: ct)
            : null;

        // Candidates from the postings: files holding more of the words, rarer ones, first.
        var keys = words.Distinct().Where(w => w.Length >= SourceTokens.MinLength).ToList();
        var held = new Dictionary<int, (int Count, double Idf)>();
        foreach (var key in keys)
        {
            if (snapshot.PostingOf(key) is not { } posting) continue;
            var idf = Idf(snapshot, key);
            foreach (var id in posting.Files)
            {
                var (count, sum) = held.GetValueOrDefault(id);
                held[id] = (count + 1, sum + idf);
            }
        }

        var candidates = held.OrderByDescending(h => h.Value.Count).ThenByDescending(h => h.Value.Idf)
            .Take(MaxChainCandidates).Select(h => snapshot.Files[h.Key]).ToList();

        IReadOnlyList<SourceChunk> semantic = [];
        if (semanticTask is not null)
        {
            try { semantic = await semanticTask.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        ct.ThrowIfCancellationRequested();
        var semanticRank = new Dictionary<SourceFile, (int Rank, int Line)>();
        foreach (var chunk in semantic)
        {
            if (snapshot.Find(chunk.Path) is { } file && !semanticRank.ContainsKey(file))
                semanticRank[file] = (semanticRank.Count, chunk.StartLine);
        }

        var pool = candidates.Concat(semanticRank.Keys).Distinct().ToList();
        if (pool.Count == 0) return null;

        var scores = new ConcurrentBag<ChainScore>();
        Parallel.ForEach(pool, new ParallelOptions { CancellationToken = ct }, file =>
        {
            if (Score(file, words) is { } score) scores.Add(score);
        });

        // BM25 over the words breaks the last ties between files alike in chain and coverage.
        var bm25 = Rank(snapshot, Resolve(snapshot, keys.Select(k => new ExploreTerm(k, 1.0))), [], setup: false)
            .Select((r, i) => (r.File, i)).ToDictionary(p => p.File, p => p.i);

        var ordered = scores
            .Where(s => s.Chain > 0 || semanticRank.ContainsKey(s.File))
            .OrderByDescending(s => s.Chain)
            .ThenByDescending(s => s.Words)
            .ThenByDescending(s => s.ChainLines)
            .ThenBy(s => semanticRank.TryGetValue(s.File, out var r) ? r.Rank : int.MaxValue)
            .ThenBy(s => bm25.TryGetValue(s.File, out var b) ? b : int.MaxValue)
            .ThenBy(s => s.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxExactResults)
            .ToList();
        if (ordered.Count == 0) return null;

        var files = new List<DiscoveredFile>();
        foreach (var score in ordered)
        {
            if (score.Chain == 0)
            {
                // Found by meaning alone: its retrieved chunk is the slice.
                var line = semanticRank[score.File].Line;
                if (ToFile(new Ranked(score.File) { SemanticLine = line }, [], confirmed: false, setup: false) is { } near)
                    files.Add(near with { Reason = "semantic match" });
                continue;
            }

            var lines = score.File.ReadLines();
            if (lines.Length == 0) continue;
            var ranges = Widen(lines, CodeDiscovery.Slice(lines, score.Weights), score.File.Symbols);
            files.Add(new DiscoveredFile
            {
                FilePath = score.File.FullPath,
                RelativePath = score.File.RelativePath,
                MatchCount = score.Weights.Count,
                Ranges = ranges,
                Keywords = score.Chain > 1 ? [score.Run] : [.. words.Distinct()],
            });
        }

        return files.Count == 0 ? null : new DiscoveryResult
        {
            Files = files,
            Keywords = [text, .. words.Distinct()],
            IsFinal = true,
            IsExact = ordered[0].Chain == words.Length,
        };
    }

    /// <summary>
    /// The file's longest run of consecutive query words found together and in order on one line (words
    /// apart count as runs of one), how many distinct query words it holds, and the lines worth showing.
    /// </summary>
    private static ChainScore? Score(SourceFile file, string[] words)
    {
        string[] lines;
        try { lines = file.ReadLines(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var best = 0;
        var run = "";
        var present = new HashSet<string>();
        var lineChains = new Dictionary<int, int>();
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            if (!words.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            var matches = ChainWord().Matches(line);
            var tokens = matches.Select(m => m.Value.ToLowerInvariant()).ToArray();
            var lineBest = 0;
            for (var t = 0; t < tokens.Length; t++)
            {
                for (var q = 0; q < words.Length; q++)
                {
                    if (tokens[t] != words[q]) continue;
                    present.Add(words[q]);
                    var k = 1;
                    while (t + k < tokens.Length && q + k < words.Length && tokens[t + k] == words[q + k]) k++;
                    if (k > lineBest) lineBest = k;
                    if (k > best)
                    {
                        // The run as the source writes it ("Items.Add"), so it is what gets highlighted.
                        var last = matches[t + k - 1];
                        (best, run) = (k, line[matches[t].Index..(last.Index + last.Length)]);
                    }
                }
            }

            if (lineBest > 0) lineChains[n + 1] = lineBest;
        }

        if (best == 0) return new ChainScore(file, 0, 0, 0, "", []);

        // The lines holding the file's best run are its slices; a file whose words stand apart shows the lines holding any.
        var weights = lineChains.Where(l => l.Value == best)
            .OrderBy(l => l.Key).Take(MaxChainLinesPerFile)
            .ToDictionary(l => l.Key, l => 100 * l.Value);
        return new ChainScore(file, best, present.Count, lineChains.Count(l => l.Value == best), run, weights);
    }
}
