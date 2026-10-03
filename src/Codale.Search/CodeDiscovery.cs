using System.Text;
using System.Text.RegularExpressions;

using Codale.Core.Helper;
using Codale.Core.Projects;
using Codale.Core.Text;

namespace Codale.Search;

/// <summary>A contiguous slice of a discovered file, with the lines that matched marked.</summary>
public sealed record DiscoveredRange
{
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Code { get; init; } = "";

    /// <summary>1-based line numbers inside the range that matched a keyword.</summary>
    public IReadOnlyList<int> MatchLines { get; init; } = [];
}

public sealed record DiscoveredFile
{
    public string FilePath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public int MatchCount { get; init; }
    public IReadOnlyList<DiscoveredRange> Ranges { get; init; } = [];

    /// <summary>True when the model picked this file; false for a ranked guess.</summary>
    public bool Confirmed { get; init; }
}

public sealed record DiscoveryResult
{
    public IReadOnlyList<DiscoveredFile> Files { get; init; } = [];

    /// <summary>Every term the result was found with, for highlighting.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Keyword rounds run: 0 for the instant pass, 1+ once the model is involved.</summary>
    public int Round { get; init; }

    /// <summary>False for the provisional results streamed while the model is still working.</summary>
    public bool IsFinal { get; init; }
}

/// <summary>
/// Code discovery: the model guesses what the implementation would literally contain,
/// ripgrep finds where that is, and the model picks the files that really answer the
/// question - or, if none do, guesses again. No prose is generated at any point.
/// </summary>
/// <remarks>
/// Speed comes from what the model is never asked to do. Each call is a short
/// pick - a keyword list, or candidate numbers - kept to a few
/// dozen tokens and a small context, so a search is typically two generations. Everything
/// else is ripgrep and in-memory scoring. An instant pass on the question's own words is
/// streamed before the model answers, so the list is never empty while it works.
/// </remarks>
public sealed class CodeDiscovery
{
    public const int DefaultMaxRounds = 3;

    private const int MaxCandidates = 10;
    private const int MaxProvisional = 6;

    /// <summary>A model asked to pick "the relevant ones" tends to pick them all.</summary>
    private const int MaxPicked = 4;

    /// <summary>What the keyword step is shown of the project's layout: every file when it fits.</summary>
    private const int MaxFileListChars = 16_000;

    /// <summary>Without the model's say-so, fewer guesses are better than more.</summary>
    private const int MaxUnconfirmed = 3;
    private const int MaxRangesPerFile = 3;
    private const int MaxRangeLines = 30;
    private const int RangeContext = 3;

    /// <summary>Caps concurrent ripgrep processes across all searches; one per keyword is otherwise unbounded.</summary>
    private static readonly SemaphoreSlim RipgrepGate = new(4);

    /// <summary>
    /// Dependencies, build output, lock files, minified bundles and source maps: they
    /// match everything and answer nothing. Excluded outright rather than trusted to
    /// .gitignore, which ripgrep ignores outside a git repository and which many
    /// projects never write for node_modules at all.
    /// </summary>
    internal static readonly string[] NoiseGlobs =
    [
        "**/node_modules/**", "**/bower_components/**", "**/.git/**", "**/bin/**", "**/obj/**",
        "**/dist/**", "**/out/**", "**/build/**", "**/.next/**", "**/.nuxt/**", "**/.svelte-kit/**",
        "**/target/**", "**/vendor/**", "**/.venv/**", "**/venv/**", "**/__pycache__/**",
        "**/coverage/**", "**/.vs/**", "**/.idea/**",
        "*.lock", "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "*.min.js", "*.min.css",
        "*.map", "*.svg", "*.resx", "*.Designer.cs", "*.g.cs", "*.g.i.cs",
    ];

    /// <summary>Manifests, READMEs and scripts that say how a project is run, built or set up.</summary>
    private static readonly Regex SetupFile = new(
        @"^(package\.json|readme(\.\w+)?|makefile|dockerfile|docker-compose\.ya?ml|compose\.ya?ml|pyproject\.toml|" +
        @"requirements\.txt|setup\.py|cargo\.toml|go\.mod|launchsettings\.json|global\.json|.+\.(csproj|sln|slnx)|" +
        @"(run|start|build|dev)\.(ps1|sh|bat|cmd)|(vite|webpack|next|nuxt)\.config\.[cm]?[jt]s|" +
        @"forge\.config\.[jt]s|electron-builder\.(json|ya?ml))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Entry points: where a run starts.</summary>
    private static readonly Regex EntryFile = new(
        @"^(main|index|app|server|program|manage|cli|electron)\.([cm]?js|ts|py|go|rs|cs)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The lines of a setup or entry file that are about running it.</summary>
    private static readonly Regex SetupLine = new(
        @"""(scripts|main|start|dev|build|serve|bin)""\s*:|^\s*#+\s.*\b(run|running|start|install|build|usage|getting started|setup|develop)|" +
        @"\b(npm|yarn|pnpm|npx|dotnet|python|py|node|electron|cargo|go|make|docker|docker-compose)\s+(run|start|install|build|up|serve|dev|\S+\.(js|py))\b|" +
        @"\.listen\(|createWindow|whenReady|static\s+(async\s+)?\w*\s*Main\s*\(|__name__\s*==|<OutputType>|""commandName""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _root;
    private readonly ISearchModel? _model;
    private readonly RipgrepSearch _search;

    /// <param name="model">Null runs the instant pass only: a keyword search on the question.</param>
    public CodeDiscovery(string root, ISearchModel? model)
    {
        _root = root;
        _model = model;
        _search = new RipgrepSearch(root);
    }

    public int MaxRounds { get; init; } = DefaultMaxRounds;

    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Raised with provisional results, then with each round's refinement.</summary>
    public event EventHandler<DiscoveryResult>? Progress;

    public static ToolDefinition KeywordsTool { get; } = new()
    {
        Name = "search",
        Description = "What to grep for.",
        Parameters =
        [
            new ToolParameter
            {
                Name = "keywords",
                Description = "comma separated terms the implementing code literally contains",
                Required = true,
            },
            new ToolParameter
            {
                Name = "paths",
                Description = "comma separated paths from the file list most likely to hold the answer, best first",
            },
        ],
    };

    /// <summary>
    /// Gets the model ready for a search that is about to start - the keyword step and
    /// the pick step - so neither waits on a CLI starting up. Call it while the user types.
    /// </summary>
    public static void Prewarm(ISearchModel model)
    {
        model.Prewarm(KeywordSystemPrompt, [KeywordsTool]);
        model.Prewarm(JudgeSystemPrompt, [JudgeTool]);
    }

    public static ToolDefinition JudgeTool { get; } = new()
    {
        Name = "pick",
        Description = "Pick the candidates that answer the question.",
        Parameters =
        [
            new ToolParameter
            {
                Name = "relevant",
                Description = "comma separated candidate numbers, best first; empty if none",
                Required = true,
            },
            new ToolParameter
            {
                Name = "keywords",
                Description = "only if none are relevant: new comma separated identifiers to grep",
            },
        ],
    };

    public async Task<DiscoveryResult> RunAsync(string query, CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        // Only the model reads the type list; a keyword-only search skips the scan.
        var typesScan = _model is null
            ? Task.FromResult<IReadOnlyDictionary<string, List<string>>>(new Dictionary<string, List<string>>())
            : FindTypesNamedApartAsync(ct);

        var files = (await _search.ListFilesAsync(NoiseGlobs, ct: ct).ConfigureAwait(false))
            .Where(f => !IsNoisePath(f))
            .ToList();
        var types = await typesScan.ConfigureAwait(false);
        var setup = SearchAgentLoop.IsSetupQuestion(query);
        var questionWords = QuestionWords(query);

        // The pick step comes a few seconds from now: time enough for its CLI to start.
        _model?.Prewarm(JudgeSystemPrompt, [JudgeTool]);

        // The model is the long pole, so it starts first; the instant pass runs while
        // it generates and is on screen well before the keywords come back.
        var keywordCall = _model is null
            ? Task.FromResult<ToolCall?>(null)
            : AskAsync(KeywordSystemPrompt, BuildKeywordPrompt(query, files, types), KeywordsTool, budget.Token);

        var instant = await ScoreAsync(questionWords, [], files, [], setup, ct).ConfigureAwait(false);
        var provisional = Build(instant.Take(MaxProvisional), questionWords, confirmed: false, round: 0, isFinal: _model is null);
        Progress?.Invoke(this, provisional);

        var call = await keywordCall.ConfigureAwait(false);
        if (call is null)
        {
            return provisional with { IsFinal = true };
        }

        var keywords = SplitList(call.GetString("keywords")).ToList();
        var paths = SplitList(call.GetString("paths"))
            .Select(NormalisePathHint)
            .Where(p => p.Length > 0)
            .Take(5)
            .ToList();
        if (keywords.Count == 0)
        {
            keywords = questionWords;
        }

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allTerms = new List<string>();
        List<Candidate> best = instant;
        DiscoveryResult latest = provisional;

        for (var round = 1; round <= MaxRounds && !budget.IsCancellationRequested; round++)
        {
            var fresh = keywords.Where(tried.Add).Take(10).ToList();
            if (fresh.Count == 0)
            {
                break;
            }

            allTerms.AddRange(fresh);

            var found = (await ScoreAsync(fresh, questionWords, files, paths, setup, ct).ConfigureAwait(false))
                .Where(c => !rejected.Contains(c.RelativePath))
                .ToList();

            // The instant ranking stays in the pool every round: a round whose model
            // keywords missed must not leave the judge choosing between junk. The
            // round's own hits come first; the question's own words fill the rest.
            var instantFresh = instant
                .Where(c => !rejected.Contains(c.RelativePath) && found.All(f => f.RelativePath != c.RelativePath));
            var candidates = found.Concat(instantFresh).Take(MaxCandidates).ToList();

            if (candidates.Count == 0)
            {
                // Nothing to judge: skip straight to asking for different words.
                call = await AskAsync(JudgeSystemPrompt, BuildJudgePrompt(query, fresh, candidates), JudgeTool, budget.Token)
                    .ConfigureAwait(false);
                keywords = SplitList(call?.GetString("keywords")).ToList();
                continue;
            }

            // An empty instant pass (the question's words matched nothing) must not
            // read as an unbeatable score.
            if (best.Count == 0 || candidates[0].Score > best[0].Score)
            {
                best = candidates;
            }

            latest = Build(candidates.Take(MaxProvisional), Highlights(allTerms, questionWords), confirmed: false, round, isFinal: false);
            Progress?.Invoke(this, latest);

            call = await AskAsync(JudgeSystemPrompt, BuildJudgePrompt(query, fresh, candidates), JudgeTool, budget.Token)
                .ConfigureAwait(false);
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
                return Build(picked, Highlights(allTerms, questionWords), confirmed: true, round, isFinal: true);
            }

            // A miss is the round's own keywords missing, not the instant files being
            // wrong - they stay eligible for the next round's judge.
            foreach (var candidate in found)
            {
                rejected.Add(candidate.RelativePath);
            }

            keywords = SplitList(call.GetString("keywords")).ToList();
        }

        // No confirmation: the best-scoring round is still better than nothing.
        return allTerms.Count == 0
            ? latest with { IsFinal = true }
            : Build(best.Take(MaxUnconfirmed), Highlights(allTerms, questionWords), confirmed: false, latest.Round, isFinal: true);
    }

    /// <summary>
    /// One model call. A failed or timed-out generation degrades to "no answer" so the
    /// search still returns its best grep; only the caller's own cancel propagates.
    /// </summary>
    private async Task<ToolCall?> AskAsync(
        string system, string prompt, ToolDefinition tool, CancellationToken ct)
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

    /// <param name="Weights">Per matched line, how strongly it matched - what the code slices are cut around.</param>
    private sealed record Candidate(string RelativePath, List<SearchHit> Hits, int Score, IReadOnlyDictionary<int, int> Weights);

    /// <summary>
    /// A term's matcher. Short words are where substring noise lives - "run" inside
    /// truncate, "app" inside wrapper - so they must start a word; longer identifiers
    /// may still match inside a camelCase name, which is how RetryPolicy finds Policy.
    /// </summary>
    internal static bool NeedsWordStart(string term) => term.Length <= 5 && char.IsLetterOrDigit(term[0]);

    private static Regex Matcher(string term) =>
        new((NeedsWordStart(term) ? @"\b" : "") + Regex.Escape(term), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The same rule in ripgrep's dialect, which rejects .NET's escaping of spaces.</summary>
    private static string RipgrepPattern(string term)
    {
        var escaped = new StringBuilder();
        foreach (var c in term)
        {
            if (@"\.+*?()|[]{}^$#&-~".Contains(c))
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return (NeedsWordStart(term) ? @"\b" : "") + escaped;
    }

    /// <summary>
    /// How much a term is worth, from how many files it appears in: a keyword found in
    /// two files says where to look, one found in forty says nothing.
    /// </summary>
    /// <remarks>
    /// Two views, the stricter wins: absolute buckets, so a large repository does not
    /// make a word in 200 files look specific; and an IDF share of the repository, so a
    /// small one does not make a word in 25 of its 30 files look specific.
    /// </remarks>
    internal static int Rarity(int files, int totalFiles)
    {
        var bucket = files switch
        {
            <= 2 => 1000,
            <= 6 => 750,
            <= 15 => 450,
            <= 40 => 220,
            _ => 80,
        };

        var total = Math.Max(totalFiles, files + 1);
        var idf = (int)(1000 * Math.Log((double)total / Math.Max(1, files)) / Math.Log(Math.Max(2, total)));

        return Math.Max(30, Math.Min(bucket, idf));
    }

    /// <summary>
    /// Ranks files for a set of terms. Breadth of rare terms beats repetition: a file
    /// matching three specific keywords outranks one repeating a common word fifty times,
    /// a file named after a keyword or defining it gets a lift, and tests and docs step
    /// aside unless the question is about them. Files far below the leader are dropped.
    /// </summary>
    private async Task<List<Candidate>> ScoreAsync(
        IReadOnlyList<string> terms,
        IReadOnlyList<string> weakTerms,
        IReadOnlyList<string> files,
        IReadOnlyList<string> pathHints,
        bool setup,
        CancellationToken ct)
    {
        if (terms.Count == 0 && !setup)
        {
            return [];
        }

        var hits = await GrepAsync(terms, ct).ConfigureAwait(false);
        var byFile = hits
            .Where(h => !IsNoisePath(h.RelativePath))
            .GroupBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // "How do I run this" is answered by package.json's scripts, the README's run
        // section and main.js - files that rarely contain the model's keywords, so they
        // are brought in by name and their run-related lines marked directly.
        var landmarks = setup ? Landmarks(files) : [];
        foreach (var landmark in landmarks)
        {
            if (!byFile.TryGetValue(landmark, out var list))
            {
                byFile[landmark] = list = [];
            }

            var known = list.Select(h => h.LineNumber).ToHashSet();
            list.AddRange(SetupHits(landmark).Where(h => !known.Contains(h.LineNumber)));
        }

        // Files whose name carries a keyword or a hinted fragment count even with no
        // content match - "the settings page" is SettingsPage.xaml before it is any line.
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!byFile.ContainsKey(file) &&
                (terms.Any(t => t.Length > 3 && name.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                 pathHints.Any(p => p.Length > 2 && file.Contains(p, StringComparison.OrdinalIgnoreCase))))
            {
                byFile[file] = [];
            }
        }

        var aboutTests = weakTerms.Concat(terms).Any(t => t.StartsWith("test", StringComparison.OrdinalIgnoreCase));

        var matchers = terms.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(t => t, Matcher, StringComparer.OrdinalIgnoreCase);
        var weakMatchers = weakTerms.Where(t => !matchers.ContainsKey(t)).Select(Matcher).ToList();

        // Which terms each hit line matches (and whether it reads as a definition),
        // tested once per line and shared by scoring, rarity and the per-line weights.
        var lineMatches = byFile.ToDictionary(
            f => f.Key,
            f => f.Value
                .Select(h =>
                {
                    var lineTerms = matchers.Keys.Where(t => matchers[t].IsMatch(h.Line)).ToList();
                    return (Hit: h, Terms: lineTerms, IsDefinition: lineTerms.Count > 0 && Definition.IsMatch(h.Line));
                })
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        var matched = lineMatches.ToDictionary(
            f => f.Key,
            f => matchers.Keys.Where(t => f.Value.Any(l => l.Terms.Contains(t))).ToList(),
            StringComparer.OrdinalIgnoreCase);

        var rarity = matchers.Keys.ToDictionary(
            t => t, t => Rarity(matched.Values.Count(m => m.Contains(t)), files.Count), StringComparer.OrdinalIgnoreCase);

        var scored = byFile
            .Select(pair =>
            {
                var (path, fileHits) = (pair.Key, pair.Value);
                var name = Path.GetFileNameWithoutExtension(path);
                var fileTerms = matched[path];

                var distinct = fileTerms.Sum(t => rarity[t]);
                var weak = weakMatchers.Count(m => fileHits.Any(h => m.IsMatch(h.Line)));
                var named = terms.Count(t => t.Length > 3 && name.Contains(t, StringComparison.OrdinalIgnoreCase));
                var exact = pathHints.Any(p => IsExactPathHint(p, path));
                var hinted = exact || pathHints.Any(p => p.Length > 2 && path.Contains(p, StringComparison.OrdinalIgnoreCase));
                var lines = lineMatches[path];
                var defined = fileTerms.Sum(t => lines.Any(l => l.IsDefinition && l.Terms.Contains(t))
                    ? rarity[t] * 3 / 10
                    : 0);

                // A setup line (a "scripts" block, a Run heading) weighs like a good keyword.
                var weights = lines
                    .GroupBy(l => l.Hit.LineNumber)
                    .ToDictionary(
                        g => g.Key,
                        g => Math.Max(
                            g.SelectMany(l => l.Terms).Distinct().Sum(t => rarity[t]),
                            setup && g.Any(l => SetupLine.IsMatch(l.Hit.Line)) ? 800 : 0));

                var depth = path.Count(c => c == Path.DirectorySeparatorChar);
                var landmark = !setup ? 0
                    : SetupFile.IsMatch(Path.GetFileName(path)) ? 3000
                    : EntryFile.IsMatch(Path.GetFileName(path)) ? 2200
                    : 0;

                // Shallow files win ties: the root package.json over one three folders down.
                // A file the model named from the list is its best guess at the answer; a
                // fragment ("settings") only narrows the folder.
                var score = distinct + named * 800 + (exact ? 2500 : hinted ? 1200 : 0) + defined + weak * 100 +
                            Math.Min(fileHits.Count, 30) + landmark + Math.Max(0, 4 - depth) * (landmark > 0 ? 300 : 40);

                // Halved rather than docked a fixed amount: a test names every identifier
                // of the code it covers, so it would otherwise tie with that code. A README
                // is exactly what a setup question wants, so it is spared.
                if (!aboutTests && landmark == 0 && IsTestOrDoc(path))
                {
                    score /= 2;
                }

                // Lines that matched nothing but a weak question word are not shown.
                var shown = fileHits.Where(h => weights[h.LineNumber] > 0).OrderBy(h => h.LineNumber).ToList();
                return new Candidate(path, shown, score, weights);
            })
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.RelativePath.Length)
            .ToList();

        // Relative, not absolute: what counts as weak depends on how good the best is.
        var floor = scored.Count > 0 ? scored[0].Score * 3 / 10 : 0;
        return scored.Where(c => c.Score >= floor).ToList();
    }

    /// <summary>Lines that declare something: where a keyword is defined rather than merely used.</summary>
    private static readonly Regex Definition = new(
        @"\b(class|interface|struct|enum|record|function|def|fn|func|type|const|let|var|void|public|private|protected|internal|static|export)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The model's path in this system's separators, without a leading "./".</summary>
    internal static string NormalisePathHint(string hint)
    {
        var path = hint.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return path.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path[2..] : path;
    }

    /// <summary>True when the hint names this very file: its whole path, or its tail from a folder boundary.</summary>
    internal static bool IsExactPathHint(string hint, string path) =>
        Path.HasExtension(hint) &&
        (string.Equals(hint, path, StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(Path.DirectorySeparatorChar + hint, StringComparison.OrdinalIgnoreCase));

    /// <summary>Setup and entry files within three folders of the root.</summary>
    private static List<string> Landmarks(IReadOnlyList<string> files) =>
        files
            .Where(f => f.Count(c => c == Path.DirectorySeparatorChar) <= 2 &&
                        (SetupFile.IsMatch(Path.GetFileName(f)) || EntryFile.IsMatch(Path.GetFileName(f))))
            .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
            .Take(20)
            .ToList();

    private List<SearchHit> SetupHits(string relativePath)
    {
        var hits = new List<SearchHit>();

        try
        {
            var full = Path.GetFullPath(relativePath, _root);
            var number = 0;

            foreach (var line in File.ReadLines(full))
            {
                if (++number > 2000 || hits.Count >= 30)
                {
                    break;
                }

                if (SetupLine.IsMatch(line))
                {
                    hits.Add(new SearchHit { FilePath = full, RelativePath = relativePath, LineNumber = number, Line = line });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return hits;
    }

    /// <summary>Belt and braces for <see cref="NoiseGlobs"/>: no dependency or output folder ever surfaces.</summary>
    private static readonly Regex NoiseDir = new(
        @"(^|[\\/])(node_modules|bower_components|\.git|bin|obj|dist|out|build|\.next|\.nuxt|\.svelte-kit|target|vendor|\.venv|venv|__pycache__|coverage|\.vs|\.idea)[\\/]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsNoisePath(string path) => NoiseDir.IsMatch(path);

    /// <summary>Test folders and projects, *.test.ts / FooTests.cs style names, and Markdown.</summary>
    private static readonly Regex TestOrDoc = new(
        @"(^|[\\/])(tests?|specs?|__tests__|fixtures?)[\\/]|\.tests?[\\/]|[._-](test|spec)s?\.[a-z]+$|(?-i:Tests?)\.[a-z]+$|\.md$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsTestOrDoc(string path) => TestOrDoc.IsMatch(path);

    /// <summary>
    /// One ripgrep per term, in parallel. A single alternation would let one common word
    /// fill the result cap from the first files walked, starving the specific keywords
    /// and skewing how many files each term appears in.
    /// </summary>
    private async Task<IReadOnlyList<SearchHit>> GrepAsync(IReadOnlyList<string> terms, CancellationToken ct)
    {
        const int perTerm = 250;

        var batches = await Task.WhenAll(terms
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(async term =>
            {
                var results = new List<SearchHit>();

                await RipgrepGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await foreach (var hit in _search.SearchAsync(new SearchQuery
                                   {
                                       Text = RipgrepPattern(term),
                                       IsRegex = true,
                                       MaxResults = perTerm,
                                       ExcludeGlobs = NoiseGlobs,
                                   }, ct).ConfigureAwait(false))
                    {
                        // The candidates' lines go to the model; credentials never do.
                        if (!SensitivePaths.IsSensitive(hit.RelativePath))
                        {
                            results.Add(hit);
                        }
                    }
                }
                catch (Exception ex) when (ex is RipgrepSearchException ||
                                           (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    // One term ripgrep rejects must not sink the others.
                }
                finally
                {
                    RipgrepGate.Release();
                }

                return results;
            })).ConfigureAwait(false);

        return batches
            .SelectMany(b => b)
            .GroupBy(h => (h.RelativePath.ToLowerInvariant(), h.LineNumber))
            .Select(g => g.First())
            .ToList();
    }

    private DiscoveryResult Build(
        IEnumerable<Candidate> candidates, IReadOnlyList<string> terms, bool confirmed, int round, bool isFinal) =>
        new()
        {
            Files = candidates.Select(c => ToFile(c, confirmed)).OfType<DiscoveredFile>().ToList(),
            Keywords = terms,
            Round = round,
            IsFinal = isFinal,
        };

    /// <summary>
    /// The model's keywords when it gave any; the question's own words only stand in
    /// when it did not, since marking "project" on every line is noise, not help.
    /// </summary>
    private static IReadOnlyList<string> Highlights(IReadOnlyList<string> terms, IReadOnlyList<string> questionWords) =>
        (terms.Count > 0 ? terms : questionWords).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private DiscoveredFile? ToFile(Candidate candidate, bool confirmed)
    {
        List<string> lines;
        string full;

        try
        {
            full = Path.GetFullPath(candidate.RelativePath, _root);
            if (!ProjectPaths.IsInside(_root, full) || new FileInfo(full).Length > 2_000_000)
            {
                return null;
            }

            // The last match plus context padding bounds what Slice can show; streaming
            // to that line instead of reading the whole 2MB saves most of the decode
            // when the matches sit near the top.
            var hitLines = candidate.Hits
                .Select(h => h.LineNumber)
                .Where(n => n >= 1)
                .ToList();
            var needed = hitLines.Count > 0 ? hitLines.Max() + RangeContext + 1 : MaxRangeLines;

            lines = [];
            foreach (var line in File.ReadLines(full))
            {
                lines.Add(line);
                if (lines.Count >= needed)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var array = lines.ToArray();
        var weights = candidate.Hits
            .Select(h => h.LineNumber)
            .Where(n => n >= 1 && n <= array.Length)
            .Distinct()
            .ToDictionary(n => n, n => candidate.Weights.GetValueOrDefault(n, 1));

        return new DiscoveredFile
        {
            FilePath = full,
            RelativePath = candidate.RelativePath,
            MatchCount = weights.Count,
            Confirmed = confirmed,
            Ranges = Slice(array, weights),
        };
    }

    /// <summary>
    /// The strongest clusters of matches, each padded with a little context and kept in
    /// file order. A cluster is worth the summed weight of its lines, so three lines of
    /// specific keywords beat one stray common word; clusters under 40% of the best are
    /// not shown at all. A file that matched only by name shows its opening lines.
    /// </summary>
    internal static IReadOnlyList<DiscoveredRange> Slice(string[] lines, IReadOnlyDictionary<int, int> matchWeights)
    {
        if (lines.Length == 0)
        {
            return [];
        }

        var clusters = new List<List<int>>();
        foreach (var line in matchWeights.Keys.Order())
        {
            if (clusters.Count > 0 && line - clusters[^1][^1] <= RangeContext * 2 + 1 &&
                line - clusters[^1][0] < MaxRangeLines - RangeContext * 2)
            {
                clusters[^1].Add(line);
            }
            else
            {
                clusters.Add([line]);
            }
        }

        int Worth(List<int> cluster) => cluster.Sum(n => matchWeights[n]);
        var bestWorth = clusters.Count > 0 ? clusters.Max(Worth) : 0;

        var windows = clusters.Count == 0
            ? [(Start: 1, End: Math.Min(lines.Length, MaxRangeLines), Matches: new List<int>())]
            : clusters
                .Where(c => Worth(c) * 10 >= bestWorth * 4)
                .OrderByDescending(Worth)
                .Take(MaxRangesPerFile)
                .Select(c => (Start: Math.Max(1, c[0] - RangeContext),
                              End: Math.Min(lines.Length, c[^1] + RangeContext),
                              Matches: c))
                .OrderBy(w => w.Start)
                .ToList();

        return windows
            .Select(w => new DiscoveredRange
            {
                StartLine = w.Start,
                EndLine = w.End,
                Code = string.Join("\n", lines[(w.Start - 1)..w.End].Select(l => TextClip.Truncate(l, 300))),
                MatchLines = w.Matches,
            })
            .ToList();
    }

    internal const string KeywordSystemPrompt =
        """
        You find code in a software project for a developer. You get the project's file
        list and their question or description, which may be loosely worded or misspelled.
        First work out what they really want: which feature, behaviour, screen or setting,
        and whether they ask where it is defined, how it works, or what happens when
        something occurs. Then call the search tool.

        keywords: 4 to 10 terms that literally appear in the code that implements it, for
        grep, comma separated. Each is one identifier (MessageRouter, onKeyDown,
        max_retries) or a short exact string the code contains - never a description of
        the code ("session title generation" matches nothing; SessionTitles does).
        Use this project's own vocabulary - class and file names you can see in the
        list, and the methods, properties, events, settings keys and API calls such code
        would contain - in the project's casing. For UI questions add the user-visible
        text (a button label, a placeholder), and for a key or gesture the ways this
        project's framework wires one up - key-down handlers, accelerators, key bindings,
        shortcut tables - in that framework's own names. Spell names correctly even
        when the question does not. Never generic words that match everywhere: data,
        handle, get, set, value, item, file, code, manager, service, helper.

        paths: up to 5 files copied from the list that most likely hold the answer, best
        first - the code that does it, not its tests. Leave it empty when nothing fits.
        """;

    internal const string JudgeSystemPrompt =
        """
        You check code search results for a developer's question. Each candidate is a
        file with the lines that matched the search. relevant: the numbers of the
        candidates that answer the question, best first - the code that implements or
        defines what was asked; for a "what happens when..." question, also the code that
        triggers it and the code that handles it. Usually 1 to 3, never more than 4. A
        file that only mentions, imports, tests or calls it in passing is not relevant.
        Leave it empty if none are. keywords: only when none are relevant, 3 to 8
        different terms to grep next, using how this codebase names things as the
        candidates show it.
        """;

    private static readonly HashSet<string> ListedOut = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".icns", ".ttf", ".otf", ".woff", ".woff2",
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".zip", ".7z", ".gz", ".pfx", ".snk", ".cer",
        ".mp3", ".mp4", ".wav", ".webm", ".pdf", ".db", ".sqlite",
    };

    /// <summary>
    /// The project's files, one folder per line: "src/app: Main.cs, Settings.cs". With
    /// the real names in front of it the model searches for what this codebase calls
    /// things, not what code in general might. A large project lists fewer files per
    /// folder, then only its folders, to stay within <paramref name="maxChars"/>.
    /// </summary>
    internal static string BuildFileList(IReadOnlyList<string> files, int maxChars = MaxFileListChars)
    {
        var folders = files
            .Where(f => !ListedOut.Contains(Path.GetExtension(f)))
            .GroupBy(f => (Path.GetDirectoryName(f) ?? "").Replace(Path.DirectorySeparatorChar, '/'), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Folder: g.Key.Length == 0 ? "." : g.Key, Names: g.Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        foreach (var perFolder in new[] { int.MaxValue, 40, 20, 10, 5 })
        {
            var builder = new StringBuilder();
            foreach (var (folder, names) in folders)
            {
                builder.Append(folder).Append(": ").Append(string.Join(", ", names.Take(perFolder)));
                if (names.Count > perFolder)
                {
                    builder.Append($", … +{names.Count - perFolder} more");
                }

                builder.AppendLine();
            }

            if (builder.Length <= maxChars)
            {
                return builder.ToString();
            }
        }

        // Too many folders to list their files: the folders alone, busiest first.
        var list = string.Join("\n", folders.OrderByDescending(f => f.Names.Count).Select(f => f.Folder));
        return TextClip.Truncate(list, maxChars);
    }

    /// <summary>The question plus the project's files and hidden types, so keywords and paths are this project's.</summary>
    internal static string BuildKeywordPrompt(
        string query, IReadOnlyList<string> files, IReadOnlyDictionary<string, List<string>>? types = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Project files (folder: files):");
        builder.Append(BuildFileList(files));

        if (types is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("Types declared in a file of another name (file: types):");
            builder.Append(BuildTypeList(types));
        }

        builder.AppendLine();
        builder.Append("Question: ").Append(query);
        return builder.ToString();
    }

    private const int MaxTypeListChars = 5_000;

    internal static string BuildTypeList(IReadOnlyDictionary<string, List<string>> types, int maxChars = MaxTypeListChars)
    {
        var builder = new StringBuilder();
        foreach (var (path, names) in types.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            var line = $"{path.Replace(Path.DirectorySeparatorChar, '/')}: {string.Join(", ", names)}\n";
            if (builder.Length + line.Length > maxChars)
            {
                break;
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    /// <summary>Type declarations, in ripgrep's dialect: C#, Java, Kotlin, TypeScript, Swift, Rust, Python.</summary>
    private const string TypeDeclarationPattern =
        @"^\s*(export\s+)?(default\s+)?((public|internal|private|protected|static|abstract|sealed|partial|file|readonly|data|pub)\s+)*" +
        @"(class|interface|record|struct|enum|trait)\s+[A-Z][A-Za-z0-9_]*";

    private static readonly Regex TypeDeclaration = new(
        @"\b(class|interface|record|struct|enum|trait)\s+([A-Z][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Types whose file is named after something else - <c>HelperToolCalls</c> inside
    /// IHelperModel.cs. The file list cannot show them, and they are exactly the names a
    /// search for them needs. Tests and noise folders are left out.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, List<string>>> FindTypesNamedApartAsync(CancellationToken ct)
    {
        var types = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var hit in _search.SearchAsync(new SearchQuery
                           {
                               Text = TypeDeclarationPattern,
                               IsRegex = true,
                               MatchCase = true,
                               MaxResults = 4000,
                               ExcludeGlobs = NoiseGlobs,
                           }, ct).ConfigureAwait(false))
            {
                if (IsNoisePath(hit.RelativePath) || IsTestOrDoc(hit.RelativePath) ||
                    TypeDeclaration.Match(hit.Line) is not { Success: true } match)
                {
                    continue;
                }

                var name = match.Groups[2].Value;
                if (string.Equals(name, Path.GetFileNameWithoutExtension(hit.RelativePath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!types.TryGetValue(hit.RelativePath, out var names))
                {
                    types[hit.RelativePath] = names = [];
                }

                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // The list is a hint; a search without it still works.
        }

        return types;
    }

    private string BuildJudgePrompt(string query, IReadOnlyList<string> keywords, IReadOnlyList<Candidate> candidates)
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
            var candidate = candidates[i];
            var block = new StringBuilder();
            block.AppendLine($"[{i + 1}] {candidate.RelativePath.Replace('\\', '/')}");

            // The lines that cover the most keywords say the most about the file. A file
            // that is here by its name alone shows its declarations instead, so it is
            // judged by what it contains rather than by its name.
            var shown = candidate.Hits.Count > 0
                ? candidate.Hits
                    .OrderByDescending(h => keywords.Count(k => h.Line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    .ThenBy(h => h.LineNumber)
                    .Take(linesPerCandidate)
                    .OrderBy(h => h.LineNumber)
                    .Select(h => (h.LineNumber, h.Line))
                : Declarations(candidate.RelativePath, linesPerCandidate);

            foreach (var (number, text) in shown)
            {
                var line = text.Trim();
                block.AppendLine($"  {number}: {TextClip.Truncate(line, 160)}");
            }

            if (builder.Length + block.Length > maxChars)
            {
                break;
            }

            builder.Append(block);
        }

        return builder.ToString();
    }

    private static readonly Regex DeclarationLine = new(
        @"^\s*(export\s+)?((public|private|protected|internal|static|async|abstract|override|sealed|partial)\s+)*" +
        @"(class|interface|struct|enum|record|def|function|fn|func|type|module|impl)\b|" +
        @"^\s*(public|internal|protected)\s+[\w<>\[\],?\s]+\s+\w+\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The first type and member declarations of a file: its shape at a glance.</summary>
    private List<(int LineNumber, string Line)> Declarations(string relativePath, int max)
    {
        var found = new List<(int, string)>();
        try
        {
            var number = 0;
            foreach (var line in File.ReadLines(Path.GetFullPath(relativePath, _root)))
            {
                if (++number > 600 || found.Count >= max)
                {
                    break;
                }

                if (DeclarationLine.IsMatch(line))
                {
                    found.Add((number, line));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return found;
    }

    private static IEnumerable<string> SplitList(string? text) =>
        (text ?? "")
            .Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.Trim('"', '\'', '`', '.', '(', ')'))
            .Where(k => k.Length > 1 && k.Length < 60)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The question's content words, for the instant pass and as a tie-breaker.</summary>
    internal static List<string> QuestionWords(string query)
    {
        var words = SearchAgentLoop.KeywordsFrom(query)
            .Split('|')
            .Select(Regex.Unescape)
            .Where(k => k.Length > 1)
            .ToList();

        // Words that describe any code at all; kept only if nothing else is left.
        var specific = words.Where(w => !Filler.Contains(w)).ToList();
        return specific.Count > 0 ? specific : words;
    }

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "project", "app", "application", "program", "file", "files", "work", "works", "working",
        "use", "used", "using", "implement", "implemented", "implementation", "make", "made", "way",
        "can", "should", "would", "could", "into", "from", "with", "about", "all", "any", "some",
        "thing", "things", "like", "get", "set", "there", "here", "happen", "happens", "logic",
        "feature", "function", "method", "part", "done", "does", "doing", "want", "need",
    };
}
