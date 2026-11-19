using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Codale.Core.Features;
using Codale.Search.Engine;
using Codale.Smart;

namespace Codale.Search;

public sealed record FileLocatorContext(string Text, IReadOnlyList<string> Files);

/// <summary>
/// The edit sites an Auto Act prompt most likely means, sent ahead of it so the agent opens the right
/// lines instead of searching. The whole prompt is the query: every word counts by how rare it is in the
/// project, never by a list of words to skip. Three retrievers vote and their ranks are fused:
/// <list type="bullet">
/// <item>the source index (BM25 per word, files declaring or named after a word lifted);</item>
/// <item>the project knowledge index (BM25 + embeddings over chunks, and files that answered similar requests);</item>
/// <item>both again for names the General model expects the code to use ("files panel" → FileTree, NameEdit).</item>
/// </list>
/// </summary>
public static partial class PromptFileLocator
{
    private const int MaxFiles = 4;
    private const int MaxBytes = 1800;
    private const int MaxAnchorChars = 110;
    private const int MaxRangeSpan = 24;
    private const int MaxAnchors = 3;
    private const int ListDepth = 30;
    /// <summary>Reciprocal-rank constant: small, so a file leading one list stays ahead of files trailing several.</summary>
    private const double RrfK = 8;
    /// <summary>A file is listed only while it scores this share of the leader: a wrong hint costs the agent more than none.</summary>
    private const double Floor = 0.5;
    /// <summary>A file one retriever alone found must hold this share of the prompt's words.</summary>
    private const double MinCoverage = 0.4;
    /// <summary>...or this many of them, for a long prompt.</summary>
    private const int MinCoveredWords = 2;
    private static readonly TimeSpan ExpansionBudget = TimeSpan.FromSeconds(2.5);

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

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    public static bool ShouldRetrieve(string prompt, bool hasAttachments, bool isFreshSession, bool isNewTopic)
    {
        if (!isFreshSession && !isNewTopic) return false;
        var text = prompt.Trim();
        if (hasAttachments || text.Length == 0 || text.StartsWith('/')) return false;
        // Operational requests already tell the CLI what to do. Diagnostics and feature
        // changes mentioning these words still benefit from source discovery.
        if (Regex.IsMatch(text, @"^(?:(?:please|now|just|then)\s+)*(?:(?:git|dotnet|npm|pnpm|yarn|cargo)\s+)?(?:commit|build|rebuild|rename|push|pull|fetch|checkout|publish|deploy|run\s+(?:the\s+)?tests?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        if (Regex.IsMatch(text, @"(?:^|[.!?]\s+)(?:(?:please|can you|could you|now|just|also)\s+)*(?:deploy\b|test\s+(?:it|this|without|on)\b|(?:use|open)\s+(?:the\s+)?browser\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        return SearchText.HasTopic(text);
    }

    /// <summary>What a retrieval used and how long each part took, for the eval and the status row.</summary>
    public sealed record Trace(IReadOnlyList<string> Expansion, long LexicalMs, long IndexMs, long ExpansionMs);

    public static async Task<FileLocatorContext> RetrieveAsync(string root, string prompt,
        ProjectKnowledgeIndex? knowledge = null, CancellationToken ct = default, IReadOnlySet<string>? knownFiles = null,
        ISmartExperience? smart = null, Action<Trace>? trace = null)
    {
        var text = SearchText.WithoutAttachments(prompt).Trim();
        if (text.Length == 0) return Empty;
        var snapshot = await SourceIndex.For(root).GetSnapshotAsync(ct).ConfigureAwait(false);
        var fused = await FuseAsync(root, text, snapshot, knowledge, knownFiles, smart, trace, ct).ConfigureAwait(false);
        // With the index, history evidence for a file that no longer exists can still be worth saying.
        if (fused.Files.Count == 0 && knowledge is null) return Empty;
        var best = fused.Best;
        var output = new StringBuilder();
        var files = new List<string>();
        foreach (var candidate in fused.Files)
        {
            if (files.Count == MaxFiles || candidate.Score < best * Floor) break;
            if (!candidate.Supported) continue;
            if (!File.Exists(candidate.File.FullPath)) continue;
            if (Entry(candidate.File, candidate.Path, fused.LineTerms, fused.Chunks.GetValueOrDefault(candidate.Path)) is not { } entry) continue;
            if (Encoding.UTF8.GetByteCount(output.ToString() + entry) > MaxBytes) break;
            output.Append(entry);
            files.Add(candidate.Path);
        }

        if (knowledge is not null)
        {
            var selected = files.Select(path => fused.Chunks.GetValueOrDefault(path)).OfType<SourceChunk>().ToArray();
            foreach (var (_, evidence) in knowledge.CompactProvenance(root, text, null, selected).Take(4))
            {
                var entry = "- Project evidence: " + evidence + "\n";
                if (Encoding.UTF8.GetByteCount(output.ToString() + entry) <= MaxBytes) output.Append(entry);
            }
        }

        return output.Length == 0 ? Empty : new(
            "Likely edit sites, from a local search of the project (path:lines | matched words, then the matching lines). " +
            "These are candidate references, not verified edit sites; check their relevance and search beyond them as needed.\n" + output + "\n", files);
    }

    /// <summary>One file of the fused ranking: its score, whether more than one retriever (or enough of the prompt) backs it, and the index's best chunk.</summary>
    internal sealed record FusedFile(SourceFile File, string Path, double Score, bool Supported, int? ChunkLine);

    /// <summary>The fused ranking, best first, with the terms that mark each file's lines.</summary>
    internal sealed record Fusion(IReadOnlyList<FusedFile> Files, IReadOnlyList<SourceExplorer.ResolvedTerm> LineTerms,
        IReadOnlyDictionary<string, SourceChunk> Chunks, double Best, IReadOnlyDictionary<string, int>? Named = null);

    /// <summary>
    /// The ranking context enrichment uses: the source index, the knowledge index and (when a model is given) the
    /// names it expects vote by reciprocal rank; tests, docs and data files only when the prompt is about them.
    /// Global search shares it so a query there finds what the same words would in a chat prompt.
    /// </summary>
    internal static async Task<Fusion> FuseAsync(string root, string text, SourceSnapshot snapshot,
        ProjectKnowledgeIndex? knowledge, IReadOnlySet<string>? knownFiles, ISmartExperience? smart, Action<Trace>? trace, CancellationToken ct,
        Func<string, string, CancellationToken, Task<IReadOnlyList<string>>>? expander = null)
    {
        var clock = Stopwatch.StartNew();

        var hints = Regex.Matches(Normal(text), @"[\w.-]+(?:/[\w.-]+)+").Select(m => m.Value.TrimEnd('.', '/')).ToList();
        var pathHints = hints.Select(h => h.Replace('/', Path.DirectorySeparatorChar)).ToList();
        var asked = PromptTerms(text, snapshot.Contains);
        // Every word the prompt uses, including those the project lacks: a request about "recharm" is not about
        // any file that only shares its "header".
        var words = asked.Where(t => !t.Exact && !t.Text.Contains(' ')).Select(t => t.Text.ToLowerInvariant()).ToHashSet();
        var promptTerms = SourceExplorer.Resolve(snapshot, asked).Where(t => t.Keys.Count > 0).ToList();
        var lexical = promptTerms.Count == 0 ? [] : SourceExplorer.Rank(snapshot, promptTerms, pathHints, setup: false);
        var lexicalMs = clock.ElapsedMilliseconds;
        // The index reuses that ranking instead of making its own, and runs beside the model's expansion; it may
        // wait briefly for a source snapshot.
        var indexed = knowledge is null ? null : Task.Run(() => SafeSearch(knowledge, root, text, lexical), ct);

        // The model guesses the names the code uses, shown a sample of this project's own names near the prompt's matches.
        var expansionClock = Stopwatch.StartNew();
        IReadOnlyList<string> expansion = [];
        // The semantic pass goes first: the code it found nearest tells the model (or, without one, the name miner) which
        // names this project really writes for what the prompt says.
        var nearest = indexed is null ? [] : await indexed.ConfigureAwait(false);
        var snippets = Snippets(nearest);
        if (smart is not null)
            expansion = await ExpandAsync(smart, root, text, Vocabulary(snapshot, lexical), snippets, ct).ConfigureAwait(false);
        else if (expander is not null)
            expansion = await expander(Vocabulary(snapshot, lexical), snippets, ct).ConfigureAwait(false);
        // No model, or none answered: a short prompt's own words explain names the snippets show (AtlasChat for "chat").
        if (expansion.Count == 0 && expander is null && snippets.Length > 0 && words.Count > 0 && words.Count <= MaxMinedWords)
            expansion = SourceExplorer.SnippetNames(snippets, asked);
        var expansionMs = expansionClock.ElapsedMilliseconds;
        var expandedIndexed = knowledge is null || expansion.Count == 0 ? null
            : Task.Run(() => SafeSearch(knowledge, root, string.Join(' ', expansion)), ct);
        var expansionTerms = SourceExplorer.Resolve(snapshot, expansion.Select(e => new SourceExplorer.ExploreTerm(e, IsIdentifier(e) ? 1.5 : 1.0)))
            .Where(t => t.Keys.Count > 0).ToList();
        var expanded = expansionTerms.Count == 0 ? [] : SourceExplorer.Rank(snapshot, expansionTerms, pathHints, setup: false);

        var chunks = indexed is null ? [] : await indexed.ConfigureAwait(false);
        var expandedChunks = expandedIndexed is null ? [] : await expandedIndexed.ConfigureAwait(false);
        var indexMs = clock.ElapsedMilliseconds;
        trace?.Invoke(new Trace(expansion, lexicalMs, indexMs, expansionMs));
        ct.ThrowIfCancellationRequested();

        // Data, JSON, tests and docs mention every subject; they lead only when the prompt is about them.
        var wantsData = DataIntent().IsMatch(text);
        var wantsTestsOrDocs = TestOrDocIntent().IsMatch(text);
        bool ExplicitPath(string path) => hints.Any(h => path.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(h + "/", StringComparison.OrdinalIgnoreCase));
        bool Eligible(string path)
        {
            if (knownFiles?.Contains(path) == true) return false;
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
        // A file linked to a leading one counts as a second retriever's vote.
        if (knowledge is not null)
            foreach (var path in FollowGraph(knowledge, root, scores, Eligible)) votes[path] = votes.GetValueOrDefault(path) + 1;

        // How much of the prompt each file's own words cover, for files only one retriever found.
        var covered = lexical.ToDictionary(r => Normal(r.File.RelativePath), r => r.Matched
            .Select(i => promptTerms[i].Term.Text.ToLowerInvariant()).Count(words.Contains), StringComparer.OrdinalIgnoreCase);
        // ...or of the names the model expects, which stand in for words the code does not use ("navbar").
        var named = expanded.ToDictionary(r => Normal(r.File.RelativePath), r => r.Matched.Count, StringComparer.OrdinalIgnoreCase);
        // A file an earlier turn on this topic edited or read is backed by that history, though it shares no words.
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

        var ranked = new List<FusedFile>();
        foreach (var (path, score) in scores.OrderByDescending(s => s.Value))
        {
            if (!byPath.TryGetValue(path, out var file)) continue;
            ranked.Add(new FusedFile(file, path, score, Supported(path), bestChunk.GetValueOrDefault(path)?.StartLine));
        }

        return new(ranked, lineTerms, bestChunk, scores.Values.Max(), named);
    }

    /// <summary>Names are mined from the nearest code only for prompts this short; a long request would match too much.</summary>
    private const int MaxMinedWords = 6;
    private const int SnippetCount = 5;
    private const int SnippetChars = 700;

    /// <summary>The nearest chunks, best first, as "path:line" and their code: what the expander reads.</summary>
    internal static string Snippets(IReadOnlyList<SourceChunk> chunks) => string.Join("\n---\n", chunks.Take(SnippetCount)
        .Select(c => $"{Normal(c.Path)}:{c.StartLine}\n{(c.Text.Length > SnippetChars ? c.Text[..SnippetChars] : c.Text).Trim()}"));

    private static FileLocatorContext Empty { get; } = new("", []);

    private const int GraphSeeds = 6;
    private const double PairShare = 0.6;
    private const double UseShare = 0.3;

    /// <summary>
    /// The leading files lend part of their score to the files linked to them in the index's graph: a view's
    /// code-behind and a class's partial files most (one unit), the few files a specific type ties them to less.
    /// A file takes its best lend, so a hub reached from many seeds gains nothing extra for it.
    /// </summary>
    private static IReadOnlyCollection<string> FollowGraph(ProjectKnowledgeIndex knowledge, string root, Dictionary<string, double> scores, Func<string, bool> eligible)
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

    /// <summary>
    /// Every word of the prompt, quoted text whole. Only the generic stop words (the, and, to) go: how much a
    /// word counts is how rare it is in this project, which the index already knows.
    /// </summary>
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

        // Prose splits the names code joins: "status bar" is StatusBar, "new file" NewFile, "chat history"
        // ChatHistory. A run of two or three words counts as an identifier when the project has that name.
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

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> Expansions = new(StringComparer.Ordinal);

    /// <summary>The model's guess at the names involved; empty when it is off, slow or fails.</summary>
    private static async Task<IReadOnlyList<string>> ExpandAsync(ISmartExperience smart, string root, string text, string vocabulary, string snippets, CancellationToken ct)
    {
        var key = ProjectKnowledgeIndex.Hash(root + "\n" + text);
        if (Expansions.TryGetValue(key, out var cached)) return cached;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(ExpansionBudget);
            var context = new SmartRequestContext(root, root, "keywords", Deadline: DateTimeOffset.UtcNow + ExpansionBudget);
            var result = await smart.ExpandQueryAsync(context, text, vocabulary, snippets, budget.Token).ConfigureAwait(false);
            if (result?.Value is not { Count: > 0 } terms) return [];
            if (Expansions.Count > 128) Expansions.Clear();
            Expansions[key] = terms;
            return terms;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
    }

    /// <summary>
    /// A sample of the project's names for the model: the files the prompt's words already reach and the types and
    /// members they declare, then the project's top folders. It teaches the model this project's naming, not the answer.
    /// </summary>
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
            .GroupBy(d => Normal(d), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(20).Select(g => g.Key);
        return string.Join(", ", names.Concat(folders).Distinct(StringComparer.Ordinal)).Trim();
    }

    /// <summary>
    /// One file's hint: "- path:start-end | words | in symbol", then its anchor lines - the strongest cluster of
    /// matching lines, or the index's best chunk when the words matched none.
    /// </summary>
    private static string? Entry(SourceFile file, string path, IReadOnlyList<SourceExplorer.ResolvedTerm> terms, SourceChunk? chunk)
    {
        var lines = file.ReadLines();
        if (lines.Length == 0) return null;
        var weights = SourceExplorer.LineWeights(file, lines, terms, setup: false);
        // An import names what the file uses, not where the change goes; comment prose mentions every subject.
        foreach (var n in ImportLines(lines)) weights.Remove(n);

        int start, end;
        var anchors = new List<int>();
        var ranges = weights.Count == 0 ? [] : CodeDiscovery.Slice(lines, weights).Where(r => r.MatchLines.Count > 0).ToList();
        if (ranges.Count > 0)
        {
            // The cluster with the most weight; the index's chunk breaks ties toward where the meaning matched.
            bool InChunk(int n) => chunk is not null && n >= chunk.StartLine && n < chunk.StartLine + 40;
            var top = ranges.MaxBy(r => r.MatchLines.Sum(n => weights.GetValueOrDefault(n)) + (r.MatchLines.Any(InChunk) ? 0.5 : 0))!;
            anchors = [.. top.MatchLines.OrderByDescending(n => weights.GetValueOrDefault(n)).ThenBy(n => n).Take(MaxAnchors).Order()];
            start = Math.Max(top.StartLine, anchors.Min() - 2);
            end = Math.Min(Math.Min(top.EndLine, anchors.Max() + 2), start + MaxRangeSpan);
            anchors = [.. anchors.Where(n => n >= start && n <= end)];
        }
        else if (chunk is not null)
        {
            start = Math.Clamp(chunk.StartLine, 1, lines.Length);
            end = Math.Min(lines.Length, start + MaxRangeSpan);
            anchors = [.. Enumerable.Range(start, end - start + 1).Where(n => lines[n - 1].Trim().Length > 3).Take(1)];
        }
        else return null;

        var holder = anchors.Count == 0 ? null : Enclosing(file, lines, anchors.Min(), anchors.Max());
        if (holder is { } h)
        {
            start = Math.Max(start, h.Line);
            end = Math.Max(start, Math.Min(end, h.End));
        }

        var span = string.Join('\n', lines[(start - 1)..Math.Min(lines.Length, end)]);
        var words = terms.Where(t => t.Line.IsMatch(span)).Select(t => t.Term.Text.ToLowerInvariant()).Distinct().Take(6).ToList();
        var entry = new StringBuilder($"- {path}:{start}-{end}");
        if (words.Count > 0) entry.Append($" | {string.Join(", ", words)}");
        if (holder is { } held) entry.Append($" | in {held.Name}");
        entry.Append('\n');
        foreach (var n in anchors)
        {
            var code = Spaces().Replace(lines[n - 1].Trim(), " ");
            if (code.Length > MaxAnchorChars) code = code[..MaxAnchorChars].TrimEnd() + "…";
            entry.Append($"    {n}: {code}\n");
        }

        return entry.ToString();
    }

    private const int MaxSymbolSpan = 300;

    /// <summary>The line closing the brackets a declaration line leaves open; the line itself when it closes them.</summary>
    private static int Extent(string[] lines, int line)
    {
        var depth = 0;
        for (var n = line; n <= lines.Length && n < line + MaxSymbolSpan; n++)
        {
            char quote = '\0';
            foreach (var c in lines[n - 1])
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'' or '`') quote = c;
                else if (c is '{' or '[' or '(') depth++;
                else if (c is '}' or ']' or ')') depth--;
            }

            if (depth <= 0) return n;
        }

        return line;
    }

    [GeneratedRegex(@"^\s*(import\b|using\s+[\w.=\s]+;|from\s+\S+\s+import\b|#include\b|export\s+(\*|\{[^}]*\})\s+from\b|(const|let|var)\s+\{?[\w\s,]+\}?\s*=\s*require\()",
        RegexOptions.CultureInvariant)]
    private static partial Regex ImportStart();

    [GeneratedRegex(@"^\s*(//|/\*|\*|#(?!include|region|endregion|if|else|elif|endif|define|pragma)|<!--|--\s)", RegexOptions.CultureInvariant)]
    private static partial Regex CommentStart();

    /// <summary>
    /// Lines that are not the code a change goes into: imports (multi-line <c>import { a, b } from "x"</c>
    /// blocks included) and whole-line comments.
    /// </summary>
    private static HashSet<int> ImportLines(string[] lines)
    {
        var found = new HashSet<int>();
        var import = false;
        var block = false;
        for (var n = 1; n <= lines.Length; n++)
        {
            var line = lines[n - 1];
            if (import)
            {
                found.Add(n);
                if (line.Contains(" from ") || line.Contains(';') || line.TrimStart().StartsWith('}')) import = false;
                continue;
            }

            if (block)
            {
                found.Add(n);
                if (line.Contains("*/")) block = false;
                continue;
            }

            if (CommentStart().IsMatch(line))
            {
                found.Add(n);
                block = line.TrimStart().StartsWith("/*") && !line.Contains("*/");
                continue;
            }

            if (!ImportStart().IsMatch(line)) continue;
            found.Add(n);
            import = line.TrimEnd().EndsWith('{') || (line.TrimStart().StartsWith("import") && line.Contains('{') && !line.Contains('}'));
        }

        return found;
    }

    /// <summary>The innermost declaration holding lines start to end, when the file has one.</summary>
    private static (string Name, int Line, int End)? Enclosing(SourceFile file, string[] lines, int start, int end) => file.Symbols
        .Select(s => (s.Name, s.Line, End: s.EndLine > s.Line ? s.EndLine : Extent(lines, s.Line)))
        .Where(s => s.Line <= start && s.End >= end && s.End > s.Line)
        .OrderBy(s => s.End - s.Line)
        .Select(s => ((string Name, int Line, int End)?)s)
        .FirstOrDefault();
}
