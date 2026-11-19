using System.Text.RegularExpressions;
using Codale.Core.Features;
using Codale.Search.Engine;

namespace Codale.Search;

/// <summary>Shared ranking for project search and compact knowledge-index lookup.</summary>
internal static partial class ProjectSearchFusion
{
    private const int ListDepth = 30;
    private const double RrfK = 8;
    private const double MinCoverage = 0.4;
    private const int MinCoveredWords = 2;
    private const int MaxMinedWords = 6;
    private const int SnippetCount = 5;
    private const int SnippetChars = 700;
    private const int GraphSeeds = 6;
    private const double PairShare = 0.6;
    private const double UseShare = 0.3;

    [GeneratedRegex(@"(^|[\\/])(migrations?|seeds?|seed-data|fixtures?|__snapshots__|__generated__|generated)[\\/]|" +
        @"\.(sql|prisma|lock|snap|csv|tsv|map|log)$|\.min\.(js|css)$|(^|[\\/])(package-lock\.json|pnpm-lock\.yaml|yarn\.lock)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataPath();

    [GeneratedRegex(@"\b(migrations?|schema|sql|seeds?|prisma|database|fixtures?|snapshots?|lockfile|json|config(uration)?|package|manifest|dependencies)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataIntent();

    [GeneratedRegex(@"\b(tests?|spec|specs|unit|e2e|testing|docs|documentation|readme|markdown)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestOrDocIntent();

    [GeneratedRegex(@"[\p{L}\p{N}_]*\p{L}[\p{L}\p{N}_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    internal sealed record FusedFile(SourceFile File, string Path, double Score, bool Supported, int? ChunkLine);
    internal sealed record Fusion(IReadOnlyList<FusedFile> Files, IReadOnlyList<SourceExplorer.ResolvedTerm> LineTerms,
        IReadOnlyDictionary<string, SourceChunk> Chunks, double Best, IReadOnlyDictionary<string, int>? Named = null);

    internal static async Task<Fusion> FuseAsync(string root, string text, SourceSnapshot snapshot,
        ProjectKnowledgeIndex? knowledge, CancellationToken ct,
        Func<string, string, CancellationToken, Task<IReadOnlyList<string>>>? expander = null)
    {
        var hints = Regex.Matches(Normal(text), @"[\w.-]+(?:/[\w.-]+)+").Select(m => m.Value.TrimEnd('.', '/')).ToList();
        var pathHints = hints.Select(h => h.Replace('/', Path.DirectorySeparatorChar)).ToList();
        var asked = PromptTerms(text, snapshot.Contains);
        var words = asked.Where(t => !t.Exact && !t.Text.Contains(' ')).Select(t => t.Text.ToLowerInvariant()).ToHashSet();
        var promptTerms = SourceExplorer.Resolve(snapshot, asked).Where(t => t.Keys.Count > 0).ToList();
        var lexical = promptTerms.Count == 0 ? [] : SourceExplorer.Rank(snapshot, promptTerms, pathHints, setup: false);
        var indexed = knowledge is null ? null : Task.Run(() => SafeSearch(knowledge, root, text, lexical), ct);

        IReadOnlyList<string> expansion = [];
        var nearest = indexed is null ? [] : await indexed.ConfigureAwait(false);
        var snippets = Snippets(nearest);
        if (expander is not null)
            expansion = await expander(Vocabulary(snapshot, lexical), snippets, ct).ConfigureAwait(false);
        if (expansion.Count == 0 && expander is null && snippets.Length > 0 && words.Count > 0 && words.Count <= MaxMinedWords)
            expansion = SourceExplorer.SnippetNames(snippets, asked);

        var expandedIndexed = knowledge is null || expansion.Count == 0 ? null
            : Task.Run(() => SafeSearch(knowledge, root, string.Join(' ', expansion)), ct);
        var expansionTerms = SourceExplorer.Resolve(snapshot, expansion.Select(e => new SourceExplorer.ExploreTerm(e, IsIdentifier(e) ? 1.5 : 1.0)))
            .Where(t => t.Keys.Count > 0).ToList();
        var expanded = expansionTerms.Count == 0 ? [] : SourceExplorer.Rank(snapshot, expansionTerms, pathHints, setup: false);

        var chunks = indexed is null ? [] : await indexed.ConfigureAwait(false);
        var expandedChunks = expandedIndexed is null ? [] : await expandedIndexed.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var wantsData = DataIntent().IsMatch(text);
        var wantsTestsOrDocs = TestOrDocIntent().IsMatch(text);
        bool ExplicitPath(string path) => hints.Any(h => path.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(h + "/", StringComparison.OrdinalIgnoreCase));
        bool Eligible(string path)
        {
            if (ExplicitPath(path)) return true;
            if (!ProjectKnowledgeIndex.Eligible(path)) return false;
            if (!wantsData && (DataPath().IsMatch(path) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) return false;
            return wantsTestsOrDocs || !CodeDiscovery.IsTestOrDoc(path);
        }

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Fuse(IEnumerable<string> ranked, double weight)
        {
            var rank = 0;
            foreach (var path in ranked.Select(Normal).Where(Eligible).Distinct(StringComparer.OrdinalIgnoreCase).Take(ListDepth))
            {
                scores[path] = scores.GetValueOrDefault(path) + weight / (RrfK + ++rank);
                votes[path] = votes.GetValueOrDefault(path) + 1;
            }
        }

        Fuse(lexical.Select(r => r.File.RelativePath), 1.0);
        Fuse(chunks.Select(c => c.Path), 1.0);
        Fuse(expanded.Select(r => r.File.RelativePath), 0.5);
        Fuse(expandedChunks.Select(c => c.Path), 0.35);
        foreach (var path in scores.Keys.Where(ExplicitPath).ToList()) scores[path] *= 2;
        if (scores.Count == 0) return new([], [], new Dictionary<string, SourceChunk>(), 0);
        if (knowledge is not null)
            foreach (var path in FollowGraph(knowledge, root, scores, Eligible)) votes[path] = votes.GetValueOrDefault(path) + 1;

        var covered = lexical.ToDictionary(r => Normal(r.File.RelativePath), r => r.Matched
            .Select(i => promptTerms[i].Term.Text.ToLowerInvariant()).Count(words.Contains), StringComparer.OrdinalIgnoreCase);
        var named = expanded.ToDictionary(r => Normal(r.File.RelativePath), r => r.Matched.Count, StringComparer.OrdinalIgnoreCase);
        var remembered = knowledge is not null
            ? knowledge.TurnEvidence(root, text).Select(Normal).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        bool Supported(string path) => ExplicitPath(path) || votes.GetValueOrDefault(path) >= 2 || remembered.Contains(path) ||
            (words.Count > 0 && covered.GetValueOrDefault(path) >= Math.Min(MinCoverage * words.Count, MinCoveredWords)) ||
            (expansionTerms.Count > 0 && named.GetValueOrDefault(path) >= Math.Max(1, Math.Min(MinCoverage * expansionTerms.Count, MinCoveredWords)));

        var lineTerms = promptTerms.Concat(expansionTerms).ToList();
        var bestChunk = chunks.Concat(expandedChunks).GroupBy(c => Normal(c.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files) byPath.TryAdd(Normal(file.RelativePath), file);
        var rankedFiles = new List<FusedFile>();
        foreach (var (path, score) in scores.OrderByDescending(s => s.Value))
        {
            if (!byPath.TryGetValue(path, out var file)) continue;
            rankedFiles.Add(new FusedFile(file, path, score, Supported(path), bestChunk.GetValueOrDefault(path)?.StartLine));
        }
        return new(rankedFiles, lineTerms, bestChunk, scores.Values.Max(), named);
    }

    internal static string Snippets(IReadOnlyList<SourceChunk> chunks) => string.Join("\n---\n", chunks.Take(SnippetCount)
        .Select(c => $"{Normal(c.Path)}:{c.StartLine}\n{(c.Text.Length > SnippetChars ? c.Text[..SnippetChars] : c.Text).Trim()}"));

    private static IReadOnlyCollection<string> FollowGraph(ProjectKnowledgeIndex knowledge, string root,
        Dictionary<string, double> scores, Func<string, bool> eligible)
    {
        var lent = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, score) in scores.OrderByDescending(s => s.Value).Take(GraphSeeds).ToList())
        {
            IReadOnlyList<(string Path, string Relation)> related;
            try { related = knowledge.RelatedFiles(root, path); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
            var uses = Math.Max(1, related.Count(r => r.Relation != ProjectKnowledgeIndex.PairRelation));
            foreach (var (target, relation) in related)
            {
                var normal = Normal(target);
                if (!eligible(normal)) continue;
                var share = relation == ProjectKnowledgeIndex.PairRelation ? PairShare : UseShare / Math.Sqrt(uses);
                lent[normal] = Math.Max(lent.GetValueOrDefault(normal), score * share);
            }
        }
        foreach (var (path, amount) in lent) scores[path] = scores.GetValueOrDefault(path) + amount;
        return lent.Keys;
    }

    private static string Normal(string path) => path.Replace('\\', '/');

    private static bool IsIdentifier(string word) => !word.Contains(' ') &&
        (word.Skip(1).Any(char.IsUpper) || word.Contains('_') || (word.Any(char.IsDigit) && word.Any(char.IsLetter)));

    internal static List<SourceExplorer.ExploreTerm> PromptTerms(string text, Func<string, bool> known)
    {
        var terms = CodeDiscovery.QuotedPhrases(text).Where(q => q.Trim().Length >= 2)
            .Select(q => new SourceExplorer.ExploreTerm(q.Trim(), 1.5, Exact: true)).ToList();
        var words = Word().Matches(CodeDiscovery.WithoutQuoted(text)).Select(m => m.Value).ToList();
        foreach (var word in words)
        {
            if (word.Length < 2 || SearchText.IsStopWord(word.ToLowerInvariant())) continue;
            if (terms.Any(t => string.Equals(t.Text, word, StringComparison.OrdinalIgnoreCase))) continue;
            terms.Add(new SourceExplorer.ExploreTerm(word, IsIdentifier(word) ? 1.5 : 1.0));
        }
        for (var i = 0; i < words.Count; i++)
            for (var n = 2; n <= 3 && i + n <= words.Count; n++)
            {
                var parts = words.Skip(i).Take(n).ToList();
                if (parts.Any(p => p.Length < 2 || SearchText.IsStopWord(p.ToLowerInvariant()))) break;
                var joined = string.Concat(parts).ToLowerInvariant();
                var singular = SearchText.Stem(joined);
                var name = known(joined) ? joined : known(singular) ? singular : null;
                if (name is null || terms.Any(t => string.Equals(t.Text, name, StringComparison.OrdinalIgnoreCase))) continue;
                terms.Add(new SourceExplorer.ExploreTerm(name, 1.0));
            }
        return terms;
    }

    private static IReadOnlyList<SourceChunk> SafeSearch(ProjectKnowledgeIndex knowledge, string root, string query,
        IReadOnlyList<SourceExplorer.Ranked>? lexical = null)
    {
        try
        {
            return lexical is null
                ? knowledge.Search(root, query, maxFiles: ListDepth, maxChunksPerFile: 1)
                : knowledge.Search(root, query, ListDepth, 1, lexical);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
    }

    private static string Vocabulary(SourceSnapshot snapshot, IReadOnlyList<SourceExplorer.Ranked> lexical)
    {
        var names = new List<string>();
        foreach (var ranked in lexical.Take(20))
        {
            names.Add(Path.GetFileNameWithoutExtension(ranked.File.RelativePath));
            names.AddRange(ranked.File.Symbols.Where(s => s.Kind == SourceSymbolKind.Type).Select(s => s.Name).Take(4));
            names.AddRange(ranked.File.Symbols.Where(s => s.Kind != SourceSymbolKind.Type).Select(s => s.Name).Take(6));
        }
        var folders = snapshot.Files.Select(f => Path.GetDirectoryName(f.RelativePath) ?? "").Where(d => d.Length > 0)
            .GroupBy(Normal, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(20).Select(g => g.Key);
        return string.Join(", ", names.Concat(folders).Distinct(StringComparer.Ordinal)).Trim();
    }
}
