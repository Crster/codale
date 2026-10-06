using System.Text;
using System.Text.RegularExpressions;

using Codale.Core.Helper;
using Codale.Core.Text;

namespace Codale.Search;

/// <summary>
/// Finds the source behind a question over a <see cref="SourceIndex"/>: ranks every
/// indexed file at once, then follows the best ones to the code they lean on and the
/// code that leans on them - so a search returns the implementation together with the
/// definitions it uses and its callers, not only the files that repeat a keyword.
/// </summary>
/// <remarks>
/// An instant pass on the question's words is streamed straight away, then the
/// background-task model's keywords (<see cref="CodeDiscovery"/>'s prompts) and its pick of
/// the candidates - with the grep work done in memory: ranking is BM25 over identifier
/// tokens with boosts for file names, declarations and the model's path hints, and it
/// covers the whole project in milliseconds instead of a ripgrep process per keyword.
///
/// Reliability rules: the model only narrows, it never empties - a failed, slow or unsure
/// model leaves the ranked result; a keyword missing from the code falls back to the
/// tokens that start like it; and nothing outside the project or matching a secrets
/// pattern is ever indexed, so it cannot reach the model's prompt.
/// </remarks>
public sealed class SourceExplorer
{
    public const int DefaultMaxRounds = 3;

    private const int MaxCandidates = 10;
    private const int MaxProvisional = 6;
    private const int MaxPicked = 4;
    private const int MaxUnconfirmed = 3;
    private const int MaxRangeLines = 30;
    private const int MaxSeeds = 3;

    /// <summary>A term's own token, or the stem standing in for it; variants it prefixes weigh less.</summary>
    private const double PrimaryFactor = 0.85;

    /// <summary>BM25 constants: how fast repetition saturates, and how much file length counts.</summary>
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly SourceIndex _index;
    private readonly ISearchModel? _model;

    /// <param name="model">The background-task model, or null for the ranked pass alone.</param>
    public SourceExplorer(SourceIndex index, ISearchModel? model)
    {
        _index = index;
        _model = model;
    }

    public int MaxRounds { get; init; } = DefaultMaxRounds;

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How many files a search without the model lists; a references lookup wants them all.</summary>
    public int MaxRankedResults { get; init; } = 12;

    /// <summary>How many related files (definitions used, callers) join the matches.</summary>
    public int MaxRelated { get; init; } = 4;

    /// <summary>Raised with provisional results, then with each round's refinement.</summary>
    public event EventHandler<DiscoveryResult>? Progress;

    /// <summary>Gets the model and the index ready for a search about to start. Call it while the user types.</summary>
    public static void Prewarm(SourceIndex index, ISearchModel? model)
    {
        index.Warm();
        if (model is not null)
        {
            CodeDiscovery.Prewarm(model);
        }
    }

    public async Task<DiscoveryResult> RunAsync(string query, CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        var snapshot = await _index.GetSnapshotAsync(ct).ConfigureAwait(false);
        var setup = SearchAgentLoop.IsSetupQuestion(query);
        var questionTerms = QueryTerms(query);

        _model?.Prewarm(CodeDiscovery.JudgeSystemPrompt, [CodeDiscovery.JudgeTool]);

        // The model is the long pole, so it starts first; the ranked pass is on screen
        // well before its keywords come back.
        var keywordCall = _model is null
            ? Task.FromResult<ToolCall?>(null)
            : AskAsync(
                CodeDiscovery.KeywordSystemPrompt,
                CodeDiscovery.BuildKeywordPrompt(query, snapshot.AllFiles.Where(f => !CodeDiscovery.IsNoisePath(f)).ToList(), snapshot.TypesNamedApart()),
                CodeDiscovery.KeywordsTool,
                budget.Token);

        var question = Resolve(snapshot, questionTerms);
        var instant = Rank(snapshot, question, [], setup);
        var provisional = Build(snapshot, instant.Take(_model is null ? MaxRankedResults : MaxProvisional).ToList(), question,
            confirmed: false, round: 0, isFinal: _model is null, setup);
        Progress?.Invoke(this, provisional);

        var call = await keywordCall.ConfigureAwait(false);
        if (call is null)
        {
            return provisional with { IsFinal = true };
        }

        var keywords = CodeDiscovery.SplitList(call.GetString("keywords")).ToList();
        var paths = CodeDiscovery.SplitList(call.GetString("paths"))
            .Select(CodeDiscovery.NormalisePathHint)
            .Where(p => p.Length > 0)
            .Take(5)
            .ToList();
        if (keywords.Count == 0)
        {
            keywords = questionTerms.Select(t => t.Text).ToList();
        }

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allTerms = new List<ExploreTerm>();
        var best = instant;
        var bestTerms = question;
        var latest = provisional;

        for (var round = 1; round <= MaxRounds && !budget.IsCancellationRequested; round++)
        {
            var fresh = keywords.Where(tried.Add).Take(10).ToList();
            if (fresh.Count == 0)
            {
                break;
            }

            allTerms.AddRange(fresh.Select(k => new ExploreTerm(k, 1.0)));

            // The model's words lead; the question's own words only break ties.
            var terms = Resolve(snapshot, [
                .. fresh.Select(k => new ExploreTerm(k, 1.0)),
                .. questionTerms
                    .Where(q => !fresh.Contains(q.Text, StringComparer.OrdinalIgnoreCase))
                    .Select(q => q with { Weight = q.Weight * 0.3 }),
            ]);

            var found = Rank(snapshot, terms, paths, setup)
                .Where(c => !rejected.Contains(c.File.RelativePath))
                .ToList();

            // The instant ranking stays in the pool every round, so a round whose keywords
            // missed does not leave the judge choosing between junk.
            var candidates = found
                .Concat(instant.Where(c => !rejected.Contains(c.File.RelativePath) && found.All(f => f.File != c.File)))
                .Take(MaxCandidates)
                .ToList();

            if (candidates.Count == 0)
            {
                call = await AskAsync(CodeDiscovery.JudgeSystemPrompt, BuildJudgePrompt(query, fresh, candidates, terms),
                    CodeDiscovery.JudgeTool, budget.Token).ConfigureAwait(false);
                keywords = CodeDiscovery.SplitList(call?.GetString("keywords")).ToList();
                continue;
            }

            // Scores under different terms do not compare; the latest round is the model's
            // best current idea, and it still carries the instant files that hold up.
            (best, bestTerms) = (candidates, terms);

            latest = Build(snapshot, candidates.Take(MaxProvisional).ToList(), terms, confirmed: false, round, isFinal: false, setup);
            Progress?.Invoke(this, latest);

            call = await AskAsync(CodeDiscovery.JudgeSystemPrompt, BuildJudgePrompt(query, fresh, candidates, terms),
                CodeDiscovery.JudgeTool, budget.Token).ConfigureAwait(false);
            if (call is null)
            {
                break;
            }

            var picked = Regex.Matches(call.GetString("relevant") ?? "", @"\d+")
                .Select(m => int.TryParse(m.Value, out var n) ? n - 1 : -1)
                .Where(i => i >= 0 && i < candidates.Count)
                .Distinct()
                .Take(MaxPicked)
                .Select(i => candidates[i])
                .ToList();

            if (picked.Count > 0)
            {
                return Build(snapshot, picked, terms, confirmed: true, round, isFinal: true, setup);
            }

            // A miss is the round's own keywords missing, not the instant files being wrong.
            foreach (var candidate in found)
            {
                rejected.Add(candidate.File.RelativePath);
            }

            keywords = CodeDiscovery.SplitList(call.GetString("keywords")).ToList();
        }

        // No confirmation: the best ranked guess is still better than nothing.
        // The model narrows, it never empties: unconfirmed, the latest round's ranking is
        // still the best guess, turned-down files included.
        return allTerms.Count == 0
            ? latest with { IsFinal = true }
            : Build(snapshot, best.Take(MaxUnconfirmed).ToList(), bestTerms, confirmed: false, latest.Round, isFinal: true, setup);
    }

    /// <summary>One model call; a failure or timeout is "no answer", only the caller's own cancel propagates.</summary>
    private async Task<ToolCall?> AskAsync(string system, string prompt, ToolDefinition tool, CancellationToken ct)
    {
        if (_model is null)
        {
            return null;
        }

        try
        {
            return await _model.NextCallAsync(system, prompt, [tool], ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- terms

    /// <summary>A search term and how much it counts: the model's keywords 1, the question's words less.</summary>
    /// <param name="Bonus">
    /// A lift rather than a requirement: a file without it is not "missing a term". The
    /// compound identifiers built from the other terms are bonuses.
    /// </param>
    internal sealed record ExploreTerm(string Text, double Weight, bool Bonus = false);

    /// <summary>
    /// A term as the index knows it: the tokens it matches (itself, or failing that the
    /// tokens that start like its stem), how specific it is, and the regex that marks
    /// its lines.
    /// </summary>
    internal sealed record ResolvedTerm(ExploreTerm Term, IReadOnlyList<(string Key, double Factor)> Keys, double Idf, Regex Line)
    {
        public double Weight => Term.Weight;
    }

    /// <summary>
    /// The question's content words; identifiers written as such (<c>RetryPolicy</c>,
    /// <c>max_retries</c>) and quoted text count more than plain words.
    /// </summary>
    internal static List<ExploreTerm> QueryTerms(string query)
    {
        var terms = new List<ExploreTerm>();

        foreach (Match quoted in Regex.Matches(query, "[\"`]([^\"`]{2,60})[\"`]"))
        {
            terms.Add(new ExploreTerm(quoted.Groups[1].Value.Trim(), 1.5));
        }

        foreach (var word in CodeDiscovery.QuestionWords(query))
        {
            if (terms.Any(t => string.Equals(t.Text, word, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var identifier = word.Skip(1).Any(char.IsUpper) || word.Contains('_');
            terms.Add(new ExploreTerm(word, identifier ? 1.5 : 1.0));
        }

        return terms;
    }

    internal static List<ResolvedTerm> Resolve(SourceSnapshot snapshot, IEnumerable<ExploreTerm> terms)
    {
        var resolved = new List<ResolvedTerm>();
        foreach (var term in terms)
        {
            var text = term.Text.Trim();
            if (text.Length < 2 || resolved.Any(r => string.Equals(r.Term.Text, text, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var tokens = SourceTokens.Of(text);
            if (tokens.Count == 0)
            {
                continue;
            }

            var keys = new List<(string Key, double Factor)>();
            var phrase = text.Any(char.IsWhiteSpace);

            if (phrase)
            {
                // Each word of a phrase ("save changes") counts, at a share of the whole.
                var words = tokens.Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase) && !CodeDiscovery.Filler.Contains(t)).ToList();
                keys.AddRange(words.Where(snapshot.Contains).Select(w => (w, 1.0 / Math.Max(1, words.Count))));
            }
            else
            {
                var whole = tokens[0];
                if (snapshot.Contains(whole))
                {
                    keys.Add((whole, 1.0));
                }

                // A keyword the code does not contain verbatim - "uploading", "SessionTitles" -
                // still finds the tokens that start like it: upload, uploader, SessionTitle.
                if (Stem(whole) is { Length: >= 4 } stem)
                {
                    foreach (var token in snapshot.TokensWithPrefix(stem, 8))
                    {
                        if (token != whole)
                        {
                            keys.Add((token, keys.Count == 0 ? 0.85 : 0.6));
                        }
                    }
                }

                // Last resort for a compound the code spells differently: its words.
                if (keys.Count == 0 && tokens.Count > 1)
                {
                    keys.AddRange(tokens.Skip(1).Where(snapshot.Contains).Select(p => (p, 0.4)));
                }
            }

            // Specificity is the term's own: a rare variant it happens to prefix
            // (RetryPolicyTests for RetryPolicy) must not make a common term look rare.
            var idf = keys.Count == 0 ? 0 : Idf(snapshot, keys.MaxBy(k => k.Factor).Key);
            var forms = new[] { text }.Concat(keys.Where(k => k.Factor >= 0.6).Select(k => k.Key)).Distinct(StringComparer.OrdinalIgnoreCase);
            resolved.Add(new ResolvedTerm(term, keys, idf, LineMatcher(forms, phrase)));
        }

        resolved.AddRange(Compounds(snapshot, resolved));
        return resolved;
    }

    private const int MaxCompounds = 8;

    /// <summary>
    /// Identifiers built from several of the terms at once: "background task provider
    /// label" is <c>BackgroundTaskProviderLabel</c>, and the file declaring it is the answer
    /// far more surely than any file that has the four words apart. Each becomes a bonus
    /// term weighted by how many of the terms it joins.
    /// </summary>
    private static IEnumerable<ResolvedTerm> Compounds(SourceSnapshot snapshot, IReadOnlyList<ResolvedTerm> terms)
    {
        var parts = terms
            .Where(t => !t.Term.Bonus && t.Keys.Count > 0)
            .Select(t => (Key: t.Keys.MaxBy(k => k.Factor).Key, t.Weight))
            .Where(p => p.Key.Length >= 3)
            .DistinctBy(p => p.Key)
            .ToList();

        if (parts.Count < 2)
        {
            return [];
        }

        var found = new List<(string Token, int Joined, double Weight, double Precision)>();
        foreach (var token in snapshot.Vocabulary)
        {
            if (token.Length < 7)
            {
                continue;
            }

            var joined = 0;
            var weight = 0.0;
            var covered = 0;
            foreach (var (key, termWeight) in parts)
            {
                if (token.Contains(key, StringComparison.Ordinal))
                {
                    joined++;
                    weight += termWeight;
                    covered += key.Length;
                }
            }

            // How much of the identifier the terms explain: RetryPolicy is all of "retry
            // policy", RetryPolicyTests only two thirds of it.
            if (joined >= 2 && parts.All(p => p.Key != token))
            {
                found.Add((token, joined, weight, Math.Min(1.0, (double)covered / token.Length)));
            }
        }

        var totalWeight = parts.Sum(p => p.Weight);
        return found
            .OrderByDescending(f => f.Joined)
            .ThenByDescending(f => f.Precision)
            .ThenBy(f => snapshot.DocumentFrequency(f.Token))
            .Take(MaxCompounds)
            .Select(f => new ResolvedTerm(
                new ExploreTerm(f.Token, 1.2 * f.Weight / totalWeight * f.Precision * f.Precision, Bonus: true),
                [(f.Token, 1.0)],
                Idf(snapshot, f.Token),
                LineMatcher([f.Token], phrase: false)));
    }

    /// <summary>Drops a plural or verb ending so "retries" finds retry and "uploading" upload.</summary>
    internal static string Stem(string token)
    {
        foreach (var suffix in new[] { "ies", "ing", "ers", "ed", "es", "er", "s" })
        {
            if (token.Length - suffix.Length >= 4 && token.EndsWith(suffix, StringComparison.Ordinal))
            {
                return token[..^suffix.Length] + (suffix == "ies" ? "y" : "");
            }
        }

        return token;
    }

    /// <summary>Short words must start a word, longer ones may sit inside a camelCase name - <see cref="CodeDiscovery.NeedsWordStart"/>.</summary>
    private static Regex LineMatcher(IEnumerable<string> forms, bool phrase)
    {
        var parts = forms.Select(f =>
        {
            var escaped = phrase ? Regex.Escape(f).Replace(@"\ ", @"\s+") : Regex.Escape(f);
            return (CodeDiscovery.NeedsWordStart(f) ? @"\b" : "") + escaped;
        });

        return new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static double Idf(SourceSnapshot snapshot, string key)
    {
        var n = snapshot.Files.Count;
        var df = snapshot.DocumentFrequency(key);
        return Math.Log(1 + (n - df + 0.5) / (df + 0.5));
    }

    // ---------------------------------------------------------------- ranking

    /// <summary>A file in the running, and why: the terms it matched, or the file it is related to.</summary>
    internal sealed class Ranked(SourceFile file)
    {
        public SourceFile File { get; } = file;
        public double Score { get; set; }
        public double MatchedWeight { get; set; }
        public HashSet<int> Matched { get; } = [];
        public string Reason { get; set; } = "";

        /// <summary>For a related file: the declaration it is here for.</summary>
        public SourceSymbol? Anchor { get; set; }

        /// <summary>For a related file: the name whose uses it is here for.</summary>
        public string? UsesName { get; set; }
    }

    /// <summary>
    /// Every indexed file scored for the terms at once. Per term: BM25 on its tokens (the
    /// best of a term's tokens, so its variants do not add up), a lift for a file named
    /// after it and a bigger one for a file declaring it. Then breadth: a file matching
    /// most of the terms beats one repeating a single term. Tests and docs step aside
    /// unless asked about, setup questions lift manifests and entry points, and the
    /// model's path hints lift the files it named. Files far below the leader are dropped.
    /// </summary>
    internal static List<Ranked> Rank(SourceSnapshot snapshot, IReadOnlyList<ResolvedTerm> terms, IReadOnlyList<string> pathHints, bool setup)
    {
        var files = snapshot.Files;
        var ranked = new Dictionary<int, Ranked>();
        var totalWeight = terms.Where(t => !t.Term.Bonus).Sum(t => t.Weight);
        var aboutTests = terms.Any(t => t.Term.Text.StartsWith("test", StringComparison.OrdinalIgnoreCase));

        Ranked At(int id) => ranked.TryGetValue(id, out var r) ? r : ranked[id] = new Ranked(files[id]);

        for (var ti = 0; ti < terms.Count; ti++)
        {
            var term = terms[ti];
            var perFile = new Dictionary<int, double>();

            foreach (var (key, factor) in term.Keys)
            {
                if (snapshot.PostingOf(key) is not { } posting)
                {
                    continue;
                }

                var idf = Idf(snapshot, key);
                for (var j = 0; j < posting.Files.Count; j++)
                {
                    var id = posting.Files[j];
                    double tf = posting.Counts[j];
                    var length = files[id].TokenCount / snapshot.AverageTokens;
                    var score = factor * idf * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * length));
                    if (!perFile.TryGetValue(id, out var existing) || score > existing)
                    {
                        perFile[id] = score;
                    }
                }

                // Declared here: where a name is defined beats where it is merely used. Only
                // for the term itself - RetryPolicyTests declares a name RetryPolicy prefixes,
                // not RetryPolicy.
                if (factor < PrimaryFactor)
                {
                    continue;
                }

                foreach (var location in snapshot.Definitions(key))
                {
                    var id = snapshot.IdOf(location.File);
                    if (id >= 0)
                    {
                        var lift = factor * idf * (location.Symbol.Kind == SourceSymbolKind.Type ? 1.6 : 1.0);
                        perFile[id] = perFile.GetValueOrDefault(id) + lift;
                    }
                }
            }

            // Named after it: "the settings page" is SettingsPage.xaml before it is any line.
            var nameKeys = term.Keys.Where(k => k.Key.Length >= 4 && k.Factor >= PrimaryFactor).Select(k => k.Key).ToList();
            if (nameKeys.Count > 0)
            {
                for (var id = 0; id < files.Count; id++)
                {
                    var file = files[id];
                    var lift = nameKeys.Any(k => file.NameKey == k) ? 2.5
                        : nameKeys.Any(k => file.NameKey.Contains(k, StringComparison.Ordinal)) ? 1.6
                        : nameKeys.Any(k => file.DirectoryKey.Contains(k, StringComparison.Ordinal)) ? 0.4
                        : 0;
                    if (lift > 0)
                    {
                        perFile[id] = perFile.GetValueOrDefault(id) + lift * Math.Max(term.Idf, 1);
                    }
                }
            }

            foreach (var (id, score) in perFile)
            {
                var entry = At(id);
                entry.Score += term.Weight * score;
                if (entry.Matched.Add(ti) && !term.Term.Bonus)
                {
                    entry.MatchedWeight += term.Weight;
                }
            }
        }

        foreach (var entry in ranked.Values)
        {
            // Breadth over repetition.
            entry.Score *= 0.4 + 0.6 * (totalWeight > 0 ? entry.MatchedWeight / totalWeight : 0);
        }

        var top = ranked.Values.Select(r => r.Score).DefaultIfEmpty(0).Max();
        var unit = Math.Max(top, 1);

        // "How do I run this" is about setup and nothing else; "a running explore" is
        // about an explore, and its real matches must lead.
        var onlySetup = setup && terms.Where(t => !t.Term.Bonus).All(t => SearchAgentLoop.IsSetupQuestion(t.Term.Text));

        // "How do I run this": the manifests and entry points, which rarely contain the words.
        // Over the matches for a pure setup question, beside them otherwise.
        if (setup)
        {
            for (var id = 0; id < files.Count; id++)
            {
                var path = files[id].RelativePath;
                var depth = path.Count(c => c == Path.DirectorySeparatorChar);
                var name = Path.GetFileName(path);
                if (depth > 2)
                {
                    continue;
                }

                var lift = CodeDiscovery.SetupFile.IsMatch(name) ? 1.0 : CodeDiscovery.EntryFile.IsMatch(name) ? 0.75 : 0;
                if (lift > 0 && onlySetup)
                {
                    At(id).Score += unit * (lift + (2 - depth) * 0.1);
                }
                else if (lift > 0)
                {
                    // Raised into view, never past the best real match.
                    var entry = At(id);
                    entry.Score = Math.Max(entry.Score, unit * 0.5 * (lift + (2 - depth) * 0.1));
                }
            }
        }

        // The model's guess at the very file, or the folder it is in.
        foreach (var hint in pathHints)
        {
            for (var id = 0; id < files.Count; id++)
            {
                var path = files[id].RelativePath;
                if (CodeDiscovery.IsExactPathHint(hint, path))
                {
                    At(id).Score += unit * 1.2;
                }
                else if (hint.Length > 2 && path.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    At(id).Score += unit * 0.4;
                }
            }
        }

        foreach (var entry in ranked.Values)
        {
            // Halved, not docked: a test names every identifier of the code it covers. A
            // README is what a setup question wants, so it is spared then.
            var readme = onlySetup && Path.GetFileName(entry.File.RelativePath).StartsWith("readme", StringComparison.OrdinalIgnoreCase);
            if (!aboutTests && !readme && entry.File.IsTestOrDoc)
            {
                entry.Score *= 0.5;
            }
        }

        var ordered = ranked.Values
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.File.RelativePath.Length)
            .ThenBy(r => r.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Relative, not absolute: what counts as weak depends on how good the best is. A
        // single-term search is a references lookup, where every user counts.
        var floor = ordered.Count == 0 ? 0 : ordered[0].Score * (terms.Count(t => !t.Term.Bonus) <= 1 ? 0.05 : 0.25);
        return ordered.Where(r => r.Score >= floor).ToList();
    }

    // ---------------------------------------------------------------- related source

    /// <summary>
    /// The source the leading files lean on and the source that leans on them: the
    /// declarations of names their shown code uses, defined in another file, and the
    /// files that use the types they are named after or that were searched for. Scored
    /// by how specific the name is, so a call to a one-off helper counts and a call to a
    /// name declared in twenty places does not.
    /// </summary>
    internal List<Ranked> Related(
        SourceSnapshot snapshot,
        IReadOnlyList<(Ranked Ranked, DiscoveredFile File)> seeds,
        IReadOnlyList<ResolvedTerm> terms)
    {
        if (MaxRelated <= 0 || seeds.Count == 0)
        {
            return [];
        }

        var aboutTests = terms.Any(t => t.Term.Text.StartsWith("test", StringComparison.OrdinalIgnoreCase));
        var taken = seeds.Select(s => s.Ranked.File).ToHashSet();
        var termKeys = terms.SelectMany(t => t.Keys.Select(k => k.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var related = new Dictionary<SourceFile, Ranked>();

        Ranked At(SourceFile file) => related.TryGetValue(file, out var r) ? r : related[file] = new Ranked(file);

        for (var s = 0; s < Math.Min(MaxSeeds, seeds.Count); s++)
        {
            var (seed, shown) = seeds[s];
            var seedFactor = 1.0 - s * 0.25;
            var seedName = Path.GetFileName(seed.File.RelativePath);
            var ownNames = seed.File.Symbols.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seedTokens = seed.File.TermKeys.ToHashSet(StringComparer.Ordinal);

            // Outgoing: what the shown code calls or constructs, declared elsewhere. Only from
            // code - "Windows" in a README or "Debug" in a script is prose, not a reference.
            var used = !IsCode(seed.File.RelativePath) ? [] : shown.Ranges
                .SelectMany(r => Identifier.Matches(r.Code).Select(m => m.Value))
                .Where(n => n.Length >= 4 && !ownNames.Contains(n))
                .Distinct(StringComparer.Ordinal);

            foreach (var name in used)
            {
                // Case matters here: "backoff" in a comment is prose, Backoff( is the method.
                var definitions = snapshot.Definitions(name)
                    .Where(d => d.Symbol.Name == name && d.File != seed.File && (aboutTests || !d.File.IsTestOrDoc))
                    .ToList();
                var definingFiles = definitions.Select(d => d.File).Distinct().Count();
                if (definingFiles is 0 or > 3)
                {
                    continue;
                }

                var idf = Idf(snapshot, name.ToLowerInvariant());
                foreach (var definition in definitions.GroupBy(d => d.File).Select(g => g.OrderBy(d => d.Symbol.Kind).First()))
                {
                    if (taken.Contains(definition.File) || !IsReference(definition, seedTokens))
                    {
                        continue;
                    }

                    var weight = idf * seedFactor * (definition.Symbol.Kind == SourceSymbolKind.Type ? 1.0 : 0.5) / definingFiles;
                    var entry = At(definition.File);
                    entry.Score += weight;
                    if (entry.Anchor is null || weight > entry.MatchedWeight)
                    {
                        entry.MatchedWeight = weight;
                        entry.Anchor = definition.Symbol;
                        entry.UsesName = null;
                        entry.Reason = $"defines {definition.Symbol.Name}, used by {seedName}";
                    }
                }
            }

            // Incoming: who uses the type the seed is named after, or a searched-for name it declares.
            var primary = seed.File.Symbols
                .Where(x => x.Kind == SourceSymbolKind.Type &&
                            (string.Equals(x.Name, seed.File.NameKey, StringComparison.OrdinalIgnoreCase) || termKeys.Contains(x.Name)))
                .Select(x => x.Name)
                .Distinct(StringComparer.Ordinal)
                .Take(3);

            foreach (var name in primary)
            {
                var key = name.ToLowerInvariant();
                if (snapshot.PostingOf(key) is not { } posting)
                {
                    continue;
                }

                var idf = Idf(snapshot, key);
                var users = Enumerable.Range(0, posting.Files.Count)
                    .Select(j => (File: snapshot.Files[posting.Files[j]], Count: posting.Counts[j]))
                    .Where(u => u.File != seed.File && !taken.Contains(u.File) && (aboutTests || !u.File.IsTestOrDoc))
                    .OrderByDescending(u => u.Count)
                    .Take(5);

                foreach (var (file, count) in users)
                {
                    var weight = idf * seedFactor * 0.6 * Math.Min(count, 3) / 3.0;
                    var entry = At(file);
                    entry.Score += weight;
                    if (entry.Anchor is null && weight > entry.MatchedWeight)
                    {
                        entry.MatchedWeight = weight;
                        entry.UsesName = name;
                        entry.Reason = $"uses {name} from {seedName}";
                    }
                }
            }
        }

        var ordered = related.Values.OrderByDescending(r => r.Score).ThenBy(r => r.File.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var floor = ordered.Count == 0 ? 0 : ordered[0].Score * 0.3;
        return ordered.Where(r => r.Score >= floor).Take(MaxRelated).ToList();
    }

    private static readonly Regex Identifier = new(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a name the seed uses really points at this declaration. A type is its own
    /// evidence. A member is not - <c>process.HasExited</c> is not ConPty's HasExited - so
    /// it counts only when the seed also names a type declared in that file, and only when
    /// the name is specific (two words or more, not <c>Size</c> or <c>Content</c>).
    /// </summary>
    private static bool IsReference(SymbolLocation definition, HashSet<string> seedTokens)
    {
        if (definition.Symbol.Kind == SourceSymbolKind.Type)
        {
            return true;
        }

        return SourceTokens.Of(definition.Symbol.Name).Count >= 3 &&
               definition.File.Symbols.Any(s => s.Kind == SourceSymbolKind.Type && seedTokens.Contains(s.Name.ToLowerInvariant()));
    }

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte", ".py", ".go", ".rs",
        ".java", ".kt", ".kts", ".scala", ".swift", ".m", ".mm", ".c", ".h", ".cc", ".cpp", ".hpp", ".cxx", ".rb",
        ".php", ".dart", ".lua", ".ex", ".exs", ".razor", ".cshtml",
    };

    internal static bool IsCode(string path) => CodeExtensions.Contains(Path.GetExtension(path));

    // ---------------------------------------------------------------- results

    private DiscoveryResult Build(
        SourceSnapshot snapshot, IReadOnlyList<Ranked> ranked, IReadOnlyList<ResolvedTerm> terms,
        bool confirmed, int round, bool isFinal, bool setup)
    {
        var seeds = ranked
            .Select(r => (Ranked: r, File: ToFile(r, terms, confirmed, setup)))
            .Where(p => p.File is not null)
            .Select(p => (p.Ranked, File: p.File!))
            .ToList();

        var related = Related(snapshot, seeds, terms)
            .Select(r => ToFile(r, terms, confirmed: false, setup))
            .OfType<DiscoveredFile>();

        var highlights = terms.Where(t => t.Weight >= 0.5 && !t.Term.Bonus).Select(t => t.Term.Text).ToList();
        if (highlights.Count == 0)
        {
            highlights = terms.Where(t => !t.Term.Bonus).Select(t => t.Term.Text).ToList();
        }

        return new DiscoveryResult
        {
            Files = [.. seeds.Select(s => s.File), .. related],
            Keywords = highlights.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Round = round,
            IsFinal = isFinal,
        };
    }

    /// <summary>The ranked file with its best slices; null when it can no longer be read.</summary>
    internal static DiscoveredFile? ToFile(Ranked ranked, IReadOnlyList<ResolvedTerm> terms, bool confirmed, bool setup)
    {
        var lines = ranked.File.ReadLines();
        if (lines.Length == 0)
        {
            return null;
        }

        var weights = LineWeights(ranked.File, lines, terms, setup, ranked.UsesName);
        IReadOnlyList<DiscoveredRange> ranges;

        if (ranked.Anchor is { } anchor && anchor.Line <= lines.Length)
        {
            ranges = [DeclarationRange(lines, anchor.Line)];
        }
        else
        {
            ranges = Widen(lines, CodeDiscovery.Slice(lines, weights), ranked.File.Symbols);
        }

        return new DiscoveredFile
        {
            FilePath = ranked.File.FullPath,
            RelativePath = ranked.File.RelativePath,
            MatchCount = weights.Count,
            Confirmed = confirmed,
            Ranges = ranges,
            Reason = ranked.Reason,
        };
    }

    /// <summary>
    /// What each line is worth: the summed specificity of the terms on it, more on a line
    /// that declares something, and - for a setup question - a run/build line in full.
    /// </summary>
    internal static Dictionary<int, int> LineWeights(SourceFile file, string[] lines, IReadOnlyList<ResolvedTerm> terms, bool setup, string? usesName = null)
    {
        var declarations = file.Symbols.Select(s => s.Line).ToHashSet();
        var setupFile = setup && (CodeDiscovery.SetupFile.IsMatch(Path.GetFileName(file.RelativePath)) ||
                                  CodeDiscovery.EntryFile.IsMatch(Path.GetFileName(file.RelativePath)));
        var uses = usesName is null ? null : new Regex(@"\b" + Regex.Escape(usesName) + @"\b", RegexOptions.CultureInvariant);
        var weights = new Dictionary<int, int>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > 2000)
            {
                continue;
            }

            double weight = 0;
            foreach (var term in terms)
            {
                try
                {
                    if (term.Line.IsMatch(line))
                    {
                        weight += term.Weight * Math.Max(term.Idf, 0.2);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                }
            }

            if (uses is not null && uses.IsMatch(line))
            {
                weight += 1;
            }

            if (weight > 0 && declarations.Contains(i + 1))
            {
                weight *= 1.6;
            }

            if (setupFile && CodeDiscovery.SetupLine.IsMatch(line))
            {
                weight = Math.Max(weight, 3);
            }

            if (weight > 0)
            {
                weights[i + 1] = Math.Max(1, (int)Math.Round(weight * 100));
            }
        }

        return weights;
    }

    /// <summary>
    /// Ranges grown to the code unit they sit in: a slice that starts a few lines into a
    /// method starts at its declaration instead, and a slice that starts at a method or
    /// property runs to its end when that fits - so a result reads as the whole function.
    /// </summary>
    internal static IReadOnlyList<DiscoveredRange> Widen(string[] lines, IReadOnlyList<DiscoveredRange> ranges, IReadOnlyList<SourceSymbol> symbols)
    {
        var widened = new List<DiscoveredRange>(ranges.Count);
        var previousEnd = 0;

        foreach (var range in ranges)
        {
            var start = range.StartLine;
            var end = range.EndLine;
            var firstMatch = range.MatchLines.Count > 0 ? range.MatchLines.Min() : start;

            // The nearest declaration whose doc comment or body holds the first match: the
            // slice starts there, not on the tail of whatever came before it.
            var enclosing = symbols
                .Where(s => s.Line >= start - 12 && s.Line <= firstMatch + 6 && CommentStart(lines, s.Line) <= firstMatch &&
                            CommentStart(lines, s.Line) > previousEnd)
                .OrderByDescending(s => s.Line)
                .FirstOrDefault();

            if (enclosing is not null && end - CommentStart(lines, enclosing.Line) < MaxRangeLines)
            {
                start = CommentStart(lines, enclosing.Line);
                if (enclosing.Kind != SourceSymbolKind.Type && BlockEnd(lines, enclosing.Line) is var blockEnd && blockEnd - start < MaxRangeLines)
                {
                    end = Math.Max(end, blockEnd);
                }
            }

            start = Math.Max(start, previousEnd + 1);
            end = Math.Min(Math.Max(end, start), lines.Length);
            previousEnd = end;

            widened.Add(range with
            {
                StartLine = start,
                EndLine = end,
                Code = Code(lines, start, end),
            });
        }

        return widened;
    }

    /// <summary>A declaration and its body, with the doc comment above it, within the range cap.</summary>
    internal static DiscoveredRange DeclarationRange(string[] lines, int line)
    {
        var start = CommentStart(lines, line);

        var end = Math.Min(BlockEnd(lines, line), start + MaxRangeLines - 1);
        return new DiscoveredRange
        {
            StartLine = start,
            EndLine = end,
            Code = Code(lines, start, end),
            MatchLines = [line],
        };
    }

    /// <summary>Where a declaration's doc comment and attributes begin: up to six lines above it.</summary>
    private static int CommentStart(string[] lines, int line)
    {
        var start = Math.Min(line, lines.Length);
        while (start > 1 && line - start < 6 && IsComment(lines[start - 2]))
        {
            start--;
        }

        return start;
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('#') ||
               trimmed.StartsWith("/*", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith('[');
    }

    /// <summary>
    /// The last line of the block a declaration opens: braces balanced when the language
    /// uses them, else the indentation dropping back - Python, YAML. Capped, so a class
    /// returns its opening stretch rather than the whole file.
    /// </summary>
    internal static int BlockEnd(string[] lines, int line)
    {
        var cap = Math.Min(lines.Length, line + MaxRangeLines - 1);
        var depth = 0;
        var opened = false;

        for (var i = line; i <= cap; i++)
        {
            var text = lines[i - 1];
            foreach (var c in StripStrings(text))
            {
                if (c == '{')
                {
                    depth++;
                    opened = true;
                }
                else if (c == '}')
                {
                    depth--;
                }
            }

            if (opened && depth <= 0)
            {
                return i;
            }

            // Expression-bodied or one-line: ends where it ends.
            if (!opened && i == line && text.TrimEnd().EndsWith(';'))
            {
                return i;
            }

            // No brace by the third line: an indentation language.
            if (!opened && i >= line + 2)
            {
                var indent = Indent(lines[line - 1]);
                var last = line;
                for (var j = line + 1; j <= cap; j++)
                {
                    if (lines[j - 1].Trim().Length == 0)
                    {
                        continue;
                    }

                    if (Indent(lines[j - 1]) <= indent)
                    {
                        break;
                    }

                    last = j;
                }

                return last;
            }
        }

        return cap;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    /// <summary>The line without string and char literals or a trailing // comment, so their braces do not count.</summary>
    private static string StripStrings(string line)
    {
        var builder = new StringBuilder(line.Length);
        char quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                quote = c;
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                break;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Code(string[] lines, int start, int end) =>
        string.Join("\n", lines[(start - 1)..end].Select(l => TextClip.Truncate(l, 300)));

    // ---------------------------------------------------------------- the model's pick

    private static string BuildJudgePrompt(string query, IReadOnlyList<string> keywords, IReadOnlyList<Ranked> candidates, IReadOnlyList<ResolvedTerm> terms)
    {
        const int maxChars = 14_000;
        const int linesPerCandidate = 6;

        var builder = new StringBuilder();
        builder.AppendLine($"Question: {query}");
        builder.AppendLine($"Searched: {string.Join(", ", keywords)}");
        builder.AppendLine();

        if (candidates.Count == 0)
        {
            builder.AppendLine("No files matched.");
            return builder.ToString();
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var file = candidates[i].File;
            var block = new StringBuilder();
            block.Append($"[{i + 1}] ").AppendPath(file.RelativePath);

            var declared = file.Symbols.Where(s => s.Kind == SourceSymbolKind.Type).Select(s => s.Name).Distinct().Take(5).ToList();
            if (declared.Count > 0)
            {
                block.Append($" (declares {string.Join(", ", declared)})");
            }

            block.AppendLine();

            // The lines covering the most of the terms say the most about the file; a file
            // here by its name alone shows its declarations, so it is judged by content.
            var lines = file.ReadLines();
            var weights = LineWeights(file, lines, terms, setup: false);
            var shown = weights.Count > 0
                ? weights.OrderByDescending(w => w.Value).ThenBy(w => w.Key).Take(linesPerCandidate).Select(w => w.Key).Order()
                : file.Symbols.Take(linesPerCandidate).Select(s => s.Line);

            foreach (var number in shown)
            {
                if (number >= 1 && number <= lines.Length)
                {
                    block.AppendLine($"  {number}: {TextClip.Truncate(lines[number - 1].Trim(), 160)}");
                }
            }

            if (builder.Length + block.Length > maxChars)
            {
                break;
            }

            builder.Append(block);
        }

        return builder.ToString();
    }
}
