using System.Text;
using System.Text.RegularExpressions;
using Codale.Core.Features;
using Codale.Search.Engine;

namespace Codale.Search;

public sealed record FileLocatorContext(string Text, IReadOnlyList<string> Files);

/// <summary>
/// The edit sites an Auto Act prompt most likely means, sent ahead of it so the agent opens the
/// right lines instead of searching: a few files, each with its best line range and the one or
/// two lines that matched. Nothing is sent unless a file holds several of the prompt's subjects
/// together - a wrong hint costs the agent more than none.
/// </summary>
public static partial class PromptFileLocator
{
    private const int MaxFiles = 4;
    private const int MaxBytes = 1800;
    private const int MaxAnchorChars = 110;
    private const int MaxRangeSpan = 24;
    private const int Pool = 40;
    private const double WeakWeight = 0.2;
    private const double AliasFactor = 0.7;
    private const double PlaceFactor = 1.5;
    private const double KindFactor = 0.6;

    /// <summary>
    /// Verbs saying what to do to the code, not which code: "add", "remove", "compact". Weak
    /// where they are the verb ("Update the header"); a subject where they are a noun ("the
    /// Codex update", "the delete dialog"). Weak words still break ties, but never count as a
    /// file matching the request.
    /// </summary>
    private static readonly HashSet<string> Verbs =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "add", "adds", "adding", "remove", "removes", "removing", "delete", "deleting", "create", "creating",
        "insert", "move", "moving", "show", "shows", "showing", "hide", "hiding", "make", "makes", "update",
        "updating", "enrich", "align", "display", "support", "include", "place", "allow", "enable", "disable",
        "compact", "simplify", "refactor", "clean", "cleanup", "tidy", "polish", "tighten", "shrink", "expand",
    ];

    /// <summary>Words that are never the subject: positions, modality, manner, contraction leftovers.</summary>
    private static readonly HashSet<string> AlwaysWeak =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "unused", "never", "always", "should", "must", "let", "into", "instead", "properly", "correctly", "consistent",
        "under", "below", "above", "beneath", "beside", "inside", "between", "within", "near", "over",
        "smaller", "bigger", "wider", "narrower", "faster", "shorter", "longer",
        // What is left of a contraction once the apostrophe splits it: "don't" → "don".
        "don", "doesn", "didn", "isn", "aren", "wasn", "weren", "won", "wouldn", "shouldn", "couldn", "hasn", "haven",
    ];

    /// <summary>Words after which the next word is a verb: "please add", "and show", "should hide".</summary>
    private static readonly HashSet<string> VerbLeads =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "and", "or", "then", "please", "to", "should", "must", "can", "could", "would", "will", "also", "just",
        "not", "never", "always", "t", "we", "i", "you", "it", "now", "so", "let", "lets", "us", "me",
    ];

    private static bool IsActionWord(string word) => Verbs.Contains(word) || AlwaysWeak.Contains(word);

    /// <summary>Words that make test files a plausible edit site.</summary>
    private static readonly HashSet<string> TestWords = [with(StringComparer.OrdinalIgnoreCase), "test", "tests", "spec", "specs", "unit", "e2e", "testing"];

    /// <summary>
    /// Interface words people use for the same thing a codebase names differently: a "navbar"
    /// request edits navGroups or Sidebar. Words in one group are one subject, so a file is not
    /// counted twice for holding two of them.
    /// </summary>
    private static readonly string[][] Aliases =
    [
        ["navbar", "nav", "navigation", "sidebar", "navmenu", "sidenav"],
        ["modal", "dialog", "popup", "popover"],
        ["button", "btn"],
    ];

    /// <summary>A "menu" is often the navigation, but a navbar or sidebar is never just any menu (a DropdownMenu).</summary>
    private static readonly Dictionary<string, string[]> OneWayAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["menu"] = ["nav", "navigation", "sidebar", "navbar"],
    };

    /// <summary>
    /// What a request adds: "add a Brand Health <b>link</b>", "create a Retry <b>button</b>". The
    /// words naming the new thing are not in the code yet, so they only find unrelated matches.
    /// </summary>
    private static readonly HashSet<string> NewThings =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "link", "links", "menu", "menus", "item", "items", "page", "pages", "tab", "tabs", "button", "buttons",
        "column", "columns", "field", "fields", "section", "sections", "card", "cards", "tile", "tiles", "option",
        "options", "entry", "entries", "route", "routes", "filter", "filters", "setting", "settings", "toggle",
        "chart", "charts", "modal", "dialog", "group", "groups", "screen", "view", "views", "widget", "badge",
        "label", "labels", "endpoint", "command", "shortcut", "report", "panel",
    ];

    private static readonly HashSet<string> Creating = [with(StringComparer.OrdinalIgnoreCase), "add", "adding", "create", "creating", "insert", "introduce", "new"];

    private static readonly HashSet<string> Articles = [with(StringComparer.OrdinalIgnoreCase), "a", "an", "the", "new", "two", "three", "some", "another", "and", "or"];

    /// <summary>Words that make data files (migrations, seeds, SQL) a plausible edit site.</summary>
    private static readonly HashSet<string> DataWords =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "migration", "migrations", "migrate", "schema", "sql", "seed", "seeds", "prisma", "database", "db",
        "column", "columns", "fixture", "fixtures", "snapshot", "snapshots", "lockfile",
    ];

    [GeneratedRegex(@"(^|[\\/])(migrations?|seeds?|seed-data|fixtures?|__snapshots__|__generated__|generated)[\\/]|" +
        @"\.(sql|prisma|lock|snap|csv|tsv|map|log)$|\.min\.(js|css)$|(^|[\\/])(package-lock\.json|pnpm-lock\.yaml|yarn\.lock)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataPath();

    [GeneratedRegex(@"[\p{L}\p{N}_]*\p{L}[\p{L}\p{N}_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    public static bool ShouldRetrieve(string prompt, bool hasAttachments)
    {
        var text = prompt.Trim();
        if (hasAttachments || text.Length == 0 || text.StartsWith('/')) return false;
        // Operational requests already tell the CLI what to do. Diagnostics and feature
        // changes mentioning these words still benefit from source discovery.
        if (Regex.IsMatch(text, @"^(?:(?:please|now|just|then)\s+)*(?:(?:git|dotnet|npm|pnpm|yarn|cargo)\s+)?(?:commit|build|rebuild|rename|push|pull|fetch|checkout|publish|deploy|run\s+(?:the\s+)?tests?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        return SearchText.RetrievalTerms(text).Count > 0;
    }

    /// <summary>One subject of the prompt: a word or quoted phrase, with the other words that mean it.</summary>
    private sealed record Concept(string Text, double Weight, bool Weak, bool Exact, string[] Aliases);

    public static async Task<FileLocatorContext> RetrieveAsync(string root, string prompt,
        ProjectKnowledgeIndex? knowledge = null, CancellationToken ct = default, IReadOnlySet<string>? knownFiles = null)
    {
        var snapshot = await SourceIndex.For(root).GetSnapshotAsync(ct).ConfigureAwait(false);
        var concepts = Concepts(prompt);
        var terms = new List<SourceExplorer.ResolvedTerm>();
        var weak = new List<bool>();
        foreach (var concept in concepts)
        {
            if (Resolve(snapshot, concept) is not { } term) continue;
            // A word spell-corrected onto another subject ("ingrowth" → growth) is that subject again.
            var primary = term.Keys.Count == 0 ? null : term.Keys.MaxBy(k => k.Factor).Key;
            if (primary is not null && terms.Any(t => t.Keys.Count > 0 && t.Keys.MaxBy(k => k.Factor).Key == primary)) continue;
            terms.Add(term);
            weak.Add(concept.Weak);
        }

        var content = Enumerable.Range(0, terms.Count).Where(i => !weak[i] && terms[i].Keys.Count > 0).ToList();
        if (content.Count == 0) return Empty;
        var contentTerms = content.Select(i => terms[i]).ToList();
        // Identifiers built from several subjects at once (agent + team + kpi → AgentTeamKpiCards) lift the file declaring them.
        terms.AddRange(SourceExplorer.Compounds(snapshot, contentTerms));

        // The project's own index adds semantic neighbours and files that answered similar
        // requests before. Both only lift a file the words already found: neither alone is evidence.
        var lifted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (knowledge is not null)
        {
            try
            {
                foreach (var chunk in await knowledge.SearchAsync(root, prompt, maxFiles: 12, ct: ct).ConfigureAwait(false))
                    lifted.Add(Normal(chunk.Path));
                foreach (var feature in knowledge.SearchFeatures(prompt, 3))
                    foreach (var file in knowledge.FilesOf(feature.Id, 12))
                        lifted.Add(Normal(file));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        var wantsData = Word().Matches(prompt).Any(m => DataWords.Contains(m.Value));
        var wantsTests = Word().Matches(prompt).Any(m => TestWords.Contains(m.Value));
        var need = Math.Min(2, content.Count);
        var picked = new List<(SourceExplorer.Ranked Ranked, double Score, List<SourceExplorer.ResolvedTerm> Matched, double Together, Region Region)>();
        foreach (var ranked in SourceExplorer.Rank(snapshot, terms, [], setup: false).Take(Pool))
        {
            ct.ThrowIfCancellationRequested();
            var path = Normal(ranked.File.RelativePath);
            if (knownFiles?.Contains(path) == true) continue;
            if (!ProjectKnowledgeIndex.Eligible(ranked.File.RelativePath) || !File.Exists(ranked.File.FullPath)) continue;

            var matched = content.Where(ranked.Matched.Contains).ToList();
            // Named after a subject: "hiky.service.ts" for a Hiky request is evidence by itself.
            var named = matched.Any(i => terms[i].Keys.Any(k => k.Factor >= 0.85 && k.Key.Length >= 4 &&
                ranked.File.NameKey.Contains(k.Key, StringComparison.Ordinal)));
            if (matched.Count < need && !(named && matched.Count > 0)) continue;

            // Together, not scattered: "Marketing" inside navGroups answers "the marketing menu in
            // the navbar"; a file with "menu" in an import and "marketing" in a header does not.
            var matchedTerms = matched.Select(i => terms[i]).ToList();
            var region = Colocated(ranked.File, matchedTerms);
            if (region.Count < need && !named) continue;

            // Data files and tests only lead a request about them: elsewhere they hold every word in passing.
            var aside = (!wantsData && DataPath().IsMatch(path)) || (!wantsTests && ranked.File.IsTestOrDoc);
            var score = ranked.Score;
            if (aside) score *= 0.2;
            if (lifted.Contains(path)) score *= 1.3;
            if (named) score *= 1.2;
            var together = aside ? region.Weight * 0.5 : region.Weight;
            picked.Add((ranked, score, matchedTerms, together, region));
        }

        if (picked.Count == 0) return Empty;
        // Breadth first: a file holding more of the request's subjects in one place beats one
        // repeating fewer of them, whatever the term density. Score orders files of equal breadth.
        picked.Sort((a, b) => Math.Abs(a.Together - b.Together) > 0.01 ? b.Together.CompareTo(a.Together) : b.Score.CompareTo(a.Score));
        var best = picked[0];

        var text = new StringBuilder();
        var files = new List<string>();
        foreach (var (ranked, score, matched, together, region) in picked)
        {
            // As broad as the leader, or one subject short of a broad leader while scoring close
            // to it; anything less is a guess the agent would have to rule out.
            if (files.Count == MaxFiles) break;
            var breadth = together / best.Together;
            var close = breadth >= 0.99 ? score >= best.Score * 0.15 : breadth >= 0.75 && score >= best.Score * 0.6;
            if (!close) continue;
            if (Entry(ranked.File, Normal(ranked.File.RelativePath), matched, region, concepts, terms) is not { } entry)
                continue;
            if (Encoding.UTF8.GetByteCount(text.ToString() + entry) > MaxBytes) break;
            text.Append(entry);
            files.Add(Normal(ranked.File.RelativePath));
        }

        return files.Count == 0 ? Empty : new(
            "Likely edit sites, from a local search of the project (path:lines | matched words, then the matching lines). " +
            "Read these ranges first; search further only if they do not cover the request.\n" + text + "\n", files);
    }

    private static FileLocatorContext Empty { get; } = new("", []);

    private static string Normal(string path) => path.Replace('\\', '/');

    /// <summary>
    /// The prompt's subjects in order: quoted text whole, identifiers above plain words, action
    /// words weak, and words of one alias group merged. Filler ("the", "please") is dropped.
    /// </summary>
    private static List<Concept> Concepts(string prompt)
    {
        var concepts = new List<Concept>();
        var text = SearchText.WithoutAttachments(prompt);
        foreach (var quoted in CodeDiscovery.QuotedPhrases(text).Where(q => q.Trim().Length >= 2))
            concepts.Add(new Concept(quoted.Trim(), 1.5, false, true, []));

        var unquoted = CodeDiscovery.WithoutQuoted(text);
        var matches = Word().Matches(unquoted).ToList();
        var (named, kinds) = NewNames(unquoted, matches);
        var places = Places(unquoted, matches);
        var typed = matches.Select(m => m.Value.ToLowerInvariant()).ToHashSet();
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var word = match.Value;
            var lower = word.ToLowerInvariant();
            if (word.Length < 2 || SearchText.IsStopWord(lower)) continue;
            // A verb at the start of a clause or after "please", "and", "should"; elsewhere a noun.
            var verb = i == 0 || VerbLeads.Contains(matches[i - 1].Value) ||
                unquoted[(matches[i - 1].Index + matches[i - 1].Length)..match.Index].IndexOfAny(['.', ',', ';', ':', '!', '?', '\n']) >= 0;
            var action = AlwaysWeak.Contains(lower) || (Verbs.Contains(lower) && verb) || named.Contains(match.Index);
            if (!action && SearchText.RetrievalTerms(word).Count == 0) continue;
            if (concepts.Any(c => string.Equals(c.Text, word, StringComparison.OrdinalIgnoreCase))) continue;
            if (concepts.Any(c => c.Aliases.Contains(lower))) continue;

            // A word the prompt also uses on its own is its own subject: "the marketing menu in the
            // navbar" names a menu and a navbar, and a dropdown menu is no navbar.
            var group = action ? null : Aliases.FirstOrDefault(g => g.Contains(lower) || g.Contains(SearchText.Stem(lower)));
            var related = group ?? (action ? null : OneWayAliases.GetValueOrDefault(SearchText.Stem(lower)));
            var aliases = related is null ? [] : related.Where(a => a != lower && a != SearchText.Stem(lower) && !typed.Contains(a)).ToArray();
            var identifier = word.Skip(1).Any(char.IsUpper) || word.Contains('_') || (word.Any(char.IsDigit) && word.Any(char.IsLetter));
            // Where the change goes outweighs what it is: "in the navbar", "under Marketing" name
            // the existing code; the kind of thing being added ("... menu") only resembles it.
            var place = !NewThings.Contains(lower) && matches.Any(m => places.Contains(m.Index) && string.Equals(m.Value, word, StringComparison.OrdinalIgnoreCase));
            var weight = action ? WeakWeight : (identifier ? 1.5 : 1.0) * (place ? PlaceFactor : 1) * (kinds.Contains(match.Index) ? KindFactor : 1);
            concepts.Add(new Concept(word, weight, action, false, aliases));
        }

        return concepts;
    }

    /// <summary>
    /// The positions of words naming something the request creates: after "add" or "create", up
    /// to the kind of thing it is ("growth and ingrowth" in "add growth and ingrowth menu").
    /// Only when the run ends at such a noun - "add pagination to the table" names nothing new.
    /// </summary>
    private static (HashSet<int> Names, HashSet<int> Kinds) NewNames(string text, IReadOnlyList<Match> words)
    {
        var found = new HashSet<int>();
        var kinds = new HashSet<int>();
        for (var i = 0; i < words.Count; i++)
        {
            if (!Creating.Contains(words[i].Value)) continue;
            var run = new List<int>();
            for (var j = i + 1; j < words.Count && j <= i + 6; j++)
            {
                var gap = text[(words[j - 1].Index + words[j - 1].Length)..words[j].Index];
                if (gap.IndexOfAny(['.', ',', ';', ':', '!', '?', '(', ')']) >= 0) break;
                var word = words[j].Value;
                if (NewThings.Contains(word))
                {
                    if (run.Count > 0) found.UnionWith(run);
                    kinds.Add(words[j].Index);
                    break;
                }

                if ((SearchText.IsStopWord(word.ToLowerInvariant()) || IsActionWord(word)) && !Articles.Contains(word)) break;
                if (!Articles.Contains(word)) run.Add(words[j].Index);
            }
        }

        return (found, kinds);
    }

    private static readonly HashSet<string> PlacePrepositions =
        [with(StringComparer.OrdinalIgnoreCase), "in", "into", "inside", "under", "below", "above", "to", "on", "onto", "within", "beside", "near", "from"];

    /// <summary>
    /// The positions of words in location phrases - "in the navbar", "under the marketing menu",
    /// "to the sidebar": up to four words after the preposition, to punctuation or the next one.
    /// </summary>
    private static HashSet<int> Places(string text, IReadOnlyList<Match> words)
    {
        var found = new HashSet<int>();
        for (var i = 0; i < words.Count; i++)
        {
            if (!PlacePrepositions.Contains(words[i].Value)) continue;
            var taken = 0;
            for (var j = i + 1; j < words.Count && taken < 4; j++)
            {
                var gap = text[(words[j - 1].Index + words[j - 1].Length)..words[j].Index];
                if (gap.IndexOfAny(['.', ',', ';', ':', '!', '?', '(', ')']) >= 0) break;
                var word = words[j].Value;
                if (Articles.Contains(word) || word is "this" or "that" or "our" or "my") continue;
                if (PlacePrepositions.Contains(word) || SearchText.IsStopWord(word.ToLowerInvariant()) || IsActionWord(word)) break;
                found.Add(words[j].Index);
                taken++;
            }
        }

        return found;
    }

    /// <summary>A concept as the index knows it, its aliases added as lesser keys; null when nothing in the project matches it.</summary>
    private static SourceExplorer.ResolvedTerm? Resolve(SourceSnapshot snapshot, Concept concept)
    {
        var term = SourceExplorer.Resolve(snapshot, [new SourceExplorer.ExploreTerm(concept.Text, concept.Weight, Exact: concept.Exact)])
            .FirstOrDefault(t => !t.Term.Bonus);
        if (term is null) return null;
        var aliases = concept.Aliases.Where(snapshot.Contains).ToList();
        if (aliases.Count == 0) return term;

        var keys = term.Keys.Concat(aliases.Select(a => (a, AliasFactor))).DistinctBy(k => k.Item1).ToList();
        var idf = term.Keys.Count > 0 ? term.Idf : aliases.Max(a => SourceExplorer.Idf(snapshot, a));
        var line = new Regex(term.Line + "|" + SourceExplorer.LineMatcher(aliases, phrase: false),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return term with { Keys = keys, Idf = idf, Line = line };
    }

    /// <summary>
    /// One file's hint: "- path:start-end | words | in symbol", then its anchor lines. Both come
    /// from the place holding the most subjects together: the anchors are the strongest line for
    /// each subject there, the range the strongest cluster of matching lines around them.
    /// </summary>
    private static string? Entry(SourceFile file, string path, IReadOnlyList<SourceExplorer.ResolvedTerm> matched, Region region,
        IReadOnlyList<Concept> concepts, IReadOnlyList<SourceExplorer.ResolvedTerm> all)
    {
        var lines = file.ReadLines();
        if (lines.Length == 0) return null;
        var weights = SourceExplorer.LineWeights(file, lines, [.. matched, .. all.Where(t => t.Term.Bonus)], setup: false);
        // An import names what the file uses, not where the change goes.
        foreach (var n in ImportLines(lines)) weights.Remove(n);
        for (var n = 1; n < region.Present.Length; n++)
            if (region.Present[n] != 0 && !weights.ContainsKey(n)) weights[n] = 1;
        var inRegion = weights.Where(w => w.Key >= region.From && w.Key <= region.To).ToDictionary();
        if (inRegion.Count == 0) inRegion = weights;
        if (inRegion.Count == 0) return null;

        // One anchor per subject: its strongest line in the region.
        var anchors = new List<int>();
        for (var t = 0; t < matched.Count && t < 31; t++)
        {
            var bit = 1 << t;
            var line = inRegion.Keys.Where(n => n < region.Present.Length && (region.Present[n] & bit) != 0)
                .OrderByDescending(n => inRegion[n]).ThenBy(n => n).FirstOrDefault();
            if (line > 0 && !anchors.Contains(line)) anchors.Add(line);
        }
        foreach (var n in inRegion.OrderByDescending(w => w.Value).ThenBy(w => w.Key).Select(w => w.Key))
        {
            if (anchors.Count >= 2) break;
            if (!anchors.Contains(n)) anchors.Add(n);
        }
        anchors = [.. anchors.OrderByDescending(n => inRegion.GetValueOrDefault(n)).Take(MaxAnchors).Order()];

        // The range: the anchors with a little context when they sit close, else the strongest
        // cluster of matching lines that holds one of them - read where it matched.
        int start, end;
        if (anchors.Count > 0 && anchors.Max() - anchors.Min() <= MaxRangeSpan)
        {
            start = Math.Max(Math.Max(1, region.From), anchors.Min() - 2);
            end = Math.Min(Math.Min(lines.Length, region.To), anchors.Max() + 2);
        }
        else
        {
            var ranges = CodeDiscovery.Slice(lines, inRegion).Where(r => r.MatchLines.Count > 0).ToList();
            if (ranges.Count == 0) return null;
            var top = ranges.Where(r => anchors.Any(n => n >= r.StartLine && n <= r.EndLine)).DefaultIfEmpty(ranges[0])
                .MaxBy(r => r.MatchLines.Sum(n => inRegion.GetValueOrDefault(n)))!;
            start = Math.Max(top.StartLine, top.MatchLines.Min() - 2);
            end = Math.Min(Math.Min(top.EndLine, top.MatchLines.Max() + 2), start + MaxRangeSpan);
        }

        var words = matched.Select(t => concepts.FirstOrDefault(c => string.Equals(c.Text, t.Term.Text, StringComparison.OrdinalIgnoreCase))?.Text ?? t.Term.Text)
            .Select(w => w.ToLowerInvariant()).Distinct();
        // Inside the declaration holding the anchors, the range stays within it and names it.
        var holder = anchors.Count == 0 ? null : Enclosing(file, lines, anchors.Min(), anchors.Max());
        if (holder is { } h)
        {
            start = Math.Max(start, h.Line);
            end = Math.Max(start, Math.Min(end, h.End));
        }

        var entry = new StringBuilder($"- {path}:{start}-{end} | {string.Join(", ", words)}");
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

    private const int Window = 12;
    private const int MaxSymbolSpan = 300;
    private const int MaxAnchors = 3;

    /// <summary>Where a file holds the most subjects together, and which subjects each line holds (bit per matched term).</summary>
    private sealed record Region(int Count, double Weight, int From, int To, int[] Present);

    /// <summary>
    /// The most subjects found in one place: a declaration (its name included) or a window of
    /// nearby lines, the smaller on a tie. Lines match on identifier parts, as the index does -
    /// navGroups holds "nav". Import lines do not count.
    /// </summary>
    private static Region Colocated(SourceFile file, IReadOnlyList<SourceExplorer.ResolvedTerm> matched)
    {
        var lines = file.ReadLines();
        var imports = ImportLines(lines);
        // The word, its aliases and its inflections ("menus", "projects"); not every token it
        // prefixes - "under" is not "underline", "marketing" not "marketingCostCache".
        var keys = matched.Select(t =>
        {
            var root = t.Keys.MaxBy(k => k.Factor).Key.Length;
            return t.Keys.Where(k => k.Factor >= AliasFactor || k.Key.Length <= root + 3).Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
        }).ToList();
        var present = new int[lines.Length + 1];
        for (var n = 1; n <= lines.Length; n++)
        {
            if (imports.Contains(n) || lines[n - 1].Length > 2000) continue;
            var tokens = SourceTokens.Count(lines[n - 1]).Keys;
            for (var t = 0; t < keys.Count && t < 31; t++)
                if (tokens.Any(keys[t].Contains)) present[n] |= 1 << t;
        }

        // The path says what the whole file is about: features/ad-spend/components/spend-tab.tsx
        // holds "ad" and "spend" on every line.
        var pathTokens = SourceTokens.Count(file.RelativePath).Keys;
        var pathMask = 0;
        for (var t = 0; t < keys.Count && t < 31; t++)
            if (pathTokens.Any(keys[t].Contains)) pathMask |= 1 << t;

        var best = new Region(0, 0, 1, lines.Length, present);
        void Consider(int from, int to)
        {
            from = Math.Max(1, from);
            to = Math.Min(lines.Length, to);
            var mask = pathMask;
            for (var n = from; n <= to; n++) mask |= present[n];
            // A rare word ("flush") places a request more surely than a common one ("line").
            var weight = 0.0;
            for (var t = 0; t < matched.Count && t < 31; t++)
                if ((mask & (1 << t)) != 0) weight += matched[t].Weight * Math.Clamp(matched[t].Idf / 3.0, 0.5, 1.5);
            if (weight > best.Weight + 1e-9 || (Math.Abs(weight - best.Weight) <= 1e-9 && weight > 0 && to - from < best.To - best.From))
                best = new Region(System.Numerics.BitOperations.PopCount((uint)mask), weight, from, to, present);
        }

        for (var n = 1; n <= lines.Length; n++)
            if (present[n] != 0) Consider(n, n + Window - 1);
        // Innermost declarations only: a method or a navGroups literal is one place, a class
        // holding forty methods is not - in it every word is "together".
        var starts = file.Symbols.Select(s => s.Line).ToHashSet();
        foreach (var symbol in file.Symbols)
        {
            // A declaration the scan saw as one line may open a literal: navGroups = [ ...groups... ].
            var end = symbol.EndLine > symbol.Line ? symbol.EndLine : Extent(lines, symbol.Line);
            if (end <= symbol.Line || end - symbol.Line > MaxSymbolSpan) continue;
            if (starts.Any(s => s > symbol.Line && s <= end)) continue;
            Consider(symbol.Line, end);
        }
        return best;
    }

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
    /// blocks included) and whole-line comments. An import names what a file uses; comment prose
    /// mentions every subject in passing.
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
