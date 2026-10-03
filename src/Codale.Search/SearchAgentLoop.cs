using System.Text;
using System.Text.RegularExpressions;

using Codale.Core.Helper;
using Codale.Core.Projects;
using Codale.Core.Text;

using Microsoft.Extensions.FileSystemGlobbing;

namespace Codale.Search;

/// <summary>What the model is asked to do, and what it may do it with.</summary>
public interface ISearchModel
{
    /// <summary>
    /// Returns exactly one tool call. Implementations must return only a call that is valid for
    /// <paramref name="tools"/> (null when the model produced none) rather than trusting the model to
    /// produce valid JSON on its own.
    /// </summary>
    Task<ToolCall?> NextCallAsync(
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);

    /// <summary>
    /// Every call of one reply, for a model that can make several at once; the same
    /// validity rule as <see cref="NextCallAsync"/>. Empty means the model produced none.
    /// </summary>
    async Task<IReadOnlyList<ToolCall>> NextCallsAsync(
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct) =>
        await NextCallAsync(systemPrompt, conversation, tools, ct).ConfigureAwait(false) is { } call ? [call] : [];

    /// <summary>
    /// Free-form generation for the final write-up. Optional: a model that cannot write
    /// prose leaves the answer to the one-line summary and the cited sections.
    /// </summary>
    Task<string> WriteAsync(string systemPrompt, string prompt, CancellationToken ct) =>
        Task.FromResult("");

    /// <summary>A call with this prompt and these tools is coming: a model slow to start may get ready. Never blocks.</summary>
    void Prewarm(string systemPrompt, IReadOnlyList<ToolDefinition> tools)
    {
    }
}

public enum SearchStepKind
{
    Tool,
    Answer,
    Note,
}

/// <summary>One line of the visible trace: what the loop did and what came back.</summary>
public sealed record SearchStep
{
    public required int Number { get; init; }
    public required SearchStepKind Kind { get; init; }
    public required string Description { get; init; }
    public int ResultCount { get; init; }
}

/// <summary>
/// A contiguous slice of one file the answer points at - the part of package.json or
/// Program.cs that actually handles the thing asked about, not just a matching line.
/// </summary>
public sealed record SearchSection
{
    public string FilePath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Code { get; init; } = "";

    /// <summary>Why this slice was picked: "read by the model", "project setup", "N matches".</summary>
    public string Reason { get; init; } = "";
}

public sealed record SearchAnswer
{
    public string Summary { get; init; } = "";

    /// <summary>Markdown write-up: the direct answer, steps, commands and notes.</summary>
    public string Explanation { get; init; } = "";

    public IReadOnlyList<SearchSection> Sections { get; init; } = [];

    /// <summary>The terms that were searched for, so the UI can highlight them.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<SearchHit> Hits { get; init; } = [];
    public IReadOnlyList<SearchStep> Trace { get; init; } = [];

    /// <summary>True when the loop hit its step or time budget instead of answering.</summary>
    public bool StoppedEarly { get; init; }
}

/// <summary>
/// Semantic search as exploration rather than retrieval: the helper model is handed the
/// query and a small set of tools, and works the repository the way a person would.
/// </summary>
/// <remarks>
/// There is no index, so nothing to build and nothing to go stale. The trade is that
/// every search costs model time, and a small fast model driving a multi-step loop is the
/// weakest link in the design. Everything here exists to contain that:
///
/// * The controller, not the model, owns the budget - step cap, wall clock, cancellation.
/// * A deterministic first grep runs before the model gets a turn, so even a degenerate
///   loop returns real hits.
/// * Tool results are clipped hard and the history is compacted, because the context
///   window is small and raw tool output would fill it in two steps.
/// * Repeated identical calls are refused rather than obeyed, since looping on one grep
///   is the characteristic small-model failure.
/// </remarks>
public sealed partial class SearchAgentLoop
{
    public const int DefaultMaxSteps = 12;

    /// <summary>The most calls of one reply that run; a model that fans out wider is cut here.</summary>
    private const int MaxCallsPerStep = 4;

    /// <summary>How long the closing answer-only call may take once the loop has stopped.</summary>
    private static readonly TimeSpan ConcludeBudget = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The wall-clock default. Single one-shot CLI calls cost several seconds (Claude haiku ~4s); a budget a few minutes
    /// wide lets a full loop of several steps plus retries fit with room to spare.
    /// </summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(5);

    private readonly RipgrepSearch _search;
    private readonly string _root;
    private readonly ISearchModel _model;

    public SearchAgentLoop(string root, ISearchModel model)
    {
        _root = root;
        _model = model;
        _search = new RipgrepSearch(root);
    }

    public int MaxSteps { get; init; } = DefaultMaxSteps;

    public TimeSpan Budget { get; init; } = DefaultBudget;

    /// <summary>Called with each step as it is recorded, so a caller can show the search live.</summary>
    public Action<SearchStep>? OnStep { get; init; }

    public static IReadOnlyList<ToolDefinition> Tools { get; } =
    [
        new ToolDefinition
        {
            Name = "grep",
            Description = "Search file contents with a regular expression.",
            Parameters =
            [
                new ToolParameter { Name = "pattern", Description = "regular expression", Required = true },
                new ToolParameter { Name = "glob", Description = "limit to files matching this glob" },
            ],
        },
        new ToolDefinition
        {
            Name = "find_files",
            Description = "List files whose path matches a glob.",
            Parameters =
            [
                new ToolParameter { Name = "glob", Description = "for example **/*.cs", Required = true },
            ],
        },
        new ToolDefinition
        {
            Name = "read_file",
            Description = "Read a range of lines from one file.",
            Parameters =
            [
                new ToolParameter { Name = "path", Description = "path relative to the project", Required = true },
                new ToolParameter { Name = "start", Description = "first line", Type = ToolParameterType.Integer },
                new ToolParameter { Name = "end", Description = "last line", Type = ToolParameterType.Integer },
            ],
        },
        new ToolDefinition
        {
            Name = "answer",
            Description = "Finish and report what was found. Call this as soon as the question can be answered.",
            Parameters =
            [
                new ToolParameter { Name = "summary", Description = "the answer in a sentence or two", Required = true },
            ],
        },
    ];

    public async Task<SearchAnswer> RunAsync(string query, CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        var trace = new List<SearchStep>();
        var hits = new List<SearchHit>();
        var findings = new StringBuilder();
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stepNumber = 0;
        var consecutiveRepeats = 0;
        var reads = new List<(string Path, int Start, int End)>();

        // Deterministic opening move: whatever the model does next, the user already has
        // something useful, and the model has real context instead of an empty history.
        var seed = await SeedGrepAsync(query, budget.Token).ConfigureAwait(false);
        hits.AddRange(seed);
        Record(new SearchStep
        {
            Number = ++stepNumber,
            Kind = SearchStepKind.Tool,
            Description = $"grep \"{KeywordsFrom(query)}\"",
            ResultCount = seed.Count,
        });
        findings.AppendLine($"grep \"{KeywordsFrom(query)}\" returned {seed.Count} matches:");
        findings.AppendLine(Summarise(seed));

        // The files a person would open first - README, manifests, entry points. Folded
        // into the findings rather than traced as a step: it costs no model time, and it
        // is what lets "how do I run this" land on package.json instead of every "run".
        var landmarks = await FindLandmarksAsync(ct).ConfigureAwait(false);
        if (landmarks.Count > 0)
        {
            findings.AppendLine("Key project files:");
            findings.AppendLine(string.Join("\n", landmarks));
        }

        var systemPrompt = BuildSystemPrompt();

        var repeatedOut = false;

        while (stepNumber < MaxSteps && !budget.IsCancellationRequested && !repeatedOut)
        {
            IReadOnlyList<ToolCall> calls;

            try
            {
                calls = await _model.NextCallsAsync(
                    systemPrompt,
                    BuildConversation(query, findings.ToString()),
                    Tools,
                    budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (calls.Count == 0)
            {
                break;
            }

            // Independent calls of one reply run together - one round trip, not one per
            // call. An answer beside them lands after them, so the reads it rests on are cited.
            foreach (var call in calls.Where(c => c.Tool != "answer").Take(MaxCallsPerStep))
            {
                if (stepNumber >= MaxSteps || budget.IsCancellationRequested)
                {
                    break;
                }

                var signature = call.Tool + "|" + call.Arguments.ToString();
                if (!attempted.Add(signature))
                {
                    // Repeating a call verbatim is the classic small-model loop; tell the
                    // model rather than silently running it again. Live runs showed a small
                    // model will keep re-proposing the same call until the step cap - three
                    // refusals in a row and the loop stops spending the budget on it.
                    consecutiveRepeats++;

                    findings.AppendLine(
                        "That exact call was already made. Change the arguments, use a different tool, or call answer.");

                    Record(new SearchStep
                    {
                        Number = ++stepNumber,
                        Kind = SearchStepKind.Note,
                        Description = $"skipped repeated {call.Tool}",
                    });

                    if (consecutiveRepeats >= 3)
                    {
                        repeatedOut = true;
                        break;
                    }

                    continue;
                }

                consecutiveRepeats = 0;

                var (description, results, text) = await ExecuteAsync(call, budget.Token).ConfigureAwait(false);

                // A successful read ends with file content; the refusals end with a full stop.
                if (call.Tool == "read_file" && !text.EndsWith('.') &&
                    call.GetString("path") is { Length: > 0 } readPath)
                {
                    var start = Math.Max(1, call.GetInt("start") ?? 1);
                    reads.Add((readPath, start, call.GetInt("end") ?? start + 60));
                }

                hits.AddRange(results);
                findings.AppendLine(text);

                Record(new SearchStep
                {
                    Number = ++stepNumber,
                    Kind = SearchStepKind.Tool,
                    Description = description,
                    ResultCount = results.Count,
                });
            }

            if (calls.FirstOrDefault(c => c.Tool == "answer") is { } answer)
            {
                return await AnswerAsync(answer.GetString("summary") ?? "").ConfigureAwait(false);
            }
        }

        // The loop ran out of steps or time without an answer. One last call that may
        // only answer turns what was found into a conclusion, rather than handing back
        // raw hits; it runs on the caller's token, not the spent budget.
        if (hits.Count > 0 && await ConcludeAsync(query, findings.ToString(), ct).ConfigureAwait(false) is { } conclusion)
        {
            return await AnswerAsync(conclusion).ConfigureAwait(false);
        }

        // Still no answer: return what was found rather than nothing. The write-up runs
        // on the caller's token too, so it still gets its turn.
        return await ComposeAsync(
            query,
            hits.Count > 0 ? "Stopped before reaching an answer; showing what was found." : "Nothing found.",
            Rank(hits), reads, landmarks, trace, stoppedEarly: true, ct).ConfigureAwait(false);

        async Task<SearchAnswer> AnswerAsync(string summary)
        {
            Record(new SearchStep
            {
                Number = ++stepNumber,
                Kind = SearchStepKind.Answer,
                Description = summary,
            });

            return await ComposeAsync(query, summary, Rank(hits), reads, landmarks, trace, stoppedEarly: false, ct)
                .ConfigureAwait(false);
        }

        void Record(SearchStep step)
        {
            trace.Add(step);
            OnStep?.Invoke(step);
        }
    }

    private const string ConcludeSystemPrompt =
        """
        You searched a code repository to answer a question and the search is over.
        Call answer now with the best answer the findings support: name the files,
        types and methods involved. If the findings only answer part of the question,
        say which part, and what is still open.
        """;

    /// <summary>
    /// The closing call when the loop stopped without an answer: only the answer tool is
    /// offered, so the model can only conclude. Null when it still produced nothing usable.
    /// </summary>
    private async Task<string?> ConcludeAsync(string query, string findings, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ConcludeBudget);

        try
        {
            var call = await _model.NextCallAsync(
                ConcludeSystemPrompt,
                BuildConversation(query, findings),
                [.. Tools.Where(t => t.Name == "answer")],
                limit.Token).ConfigureAwait(false);

            return call?.Tool == "answer" && call.GetString("summary") is { Length: > 0 } summary ? summary : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HelperModelException)
        {
            return null;
        }
    }

    private async Task<SearchAnswer> ComposeAsync(
        string query,
        string summary,
        IReadOnlyList<SearchHit> hits,
        IReadOnlyList<(string Path, int Start, int End)> reads,
        IReadOnlyList<string> landmarks,
        IReadOnlyList<SearchStep> trace,
        bool stoppedEarly,
        CancellationToken ct)
    {
        var sections = BuildSections(query, hits, reads, landmarks);
        var explanation = "";

        if (sections.Count > 0 && !ct.IsCancellationRequested)
        {
            try
            {
                explanation = CleanWriteUp(await _model.WriteAsync(
                    WriteUpSystemPrompt, BuildWriteUpPrompt(query, summary, sections), ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
            }
        }

        return new SearchAnswer
        {
            Summary = summary,
            Explanation = explanation,
            Sections = sections,
            Hits = hits,
            Trace = trace,
            StoppedEarly = stoppedEarly,
        };
    }

    private const int MaxSections = 6;
    private const int MaxSectionLines = 40;

    /// <summary>
    /// Picks the slices worth showing: what the model chose to read first, then - for
    /// run/build/setup questions - the relevant part of the README and manifests, then
    /// the densest cluster of hits in each file that matched.
    /// </summary>
    internal IReadOnlyList<SearchSection> BuildSections(
        string query,
        IReadOnlyList<SearchHit> hits,
        IReadOnlyList<(string Path, int Start, int End)> reads,
        IReadOnlyList<string> landmarks)
    {
        var picked = new List<(string Path, int Start, int End, string Reason)>();

        foreach (var (path, start, end) in reads)
        {
            picked.Add((path, start, Math.Min(end, start + MaxSectionLines - 1), "read by the model"));
        }

        if (IsSetupQuestion(query))
        {
            foreach (var landmark in landmarks.Where(IsSetupFile).Take(3))
            {
                var anchor = FindAnchor(landmark);
                picked.Add((landmark, anchor, anchor + 29, "project setup"));
            }
        }

        var byFile = hits
            .GroupBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count());

        foreach (var file in byFile)
        {
            var lines = file.Select(h => h.LineNumber).Order().ToList();

            // One slice per file: the densest run of hits, so a file with matches at the
            // top and bottom does not turn into two half-relevant fragments.
            var (runStart, runEnd, runCount) = (lines[0], lines[0], 1);
            var best = (Start: runStart, End: runEnd, Count: runCount);

            foreach (var line in lines.Skip(1))
            {
                (runStart, runEnd, runCount) = line - runEnd <= 8
                    ? (runStart, line, runCount + 1)
                    : (line, line, 1);

                if (runCount > best.Count)
                {
                    best = (runStart, runEnd, runCount);
                }
            }

            var from = Math.Max(1, best.Start - 4);
            picked.Add((file.Key, from, Math.Min(best.End + 6, from + MaxSectionLines - 1),
                        file.Count() == 1 ? "1 match" : $"{file.Count()} matches"));
        }

        var sections = new List<SearchSection>();

        foreach (var (path, start, end, reason) in picked)
        {
            if (sections.Count >= MaxSections)
            {
                break;
            }

            // An overlap with an earlier pick of the same file adds nothing new.
            var relative = NormaliseRelative(path);
            if (sections.Any(s => string.Equals(s.RelativePath, relative, StringComparison.OrdinalIgnoreCase) &&
                                  start <= s.EndLine && end >= s.StartLine))
            {
                continue;
            }

            if (LoadSection(path, start, end, reason) is { } section)
            {
                sections.Add(section);
            }
        }

        return sections;
    }

    private string NormaliseRelative(string path)
    {
        try
        {
            return Path.GetRelativePath(_root, Path.GetFullPath(path, _root));
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private SearchSection? LoadSection(string relativePath, int start, int end, string reason)
    {
        try
        {
            var full = Path.GetFullPath(relativePath, _root);
            if (!ProjectPaths.IsInside(_root, full) || SensitivePaths.IsSensitive(relativePath) || !File.Exists(full))
            {
                return null;
            }

            var lines = File.ReadLines(full).Skip(start - 1).Take(end - start + 1).ToList();
            if (lines.Count == 0)
            {
                return null;
            }

            return new SearchSection
            {
                FilePath = full,
                RelativePath = Path.GetRelativePath(_root, full),
                StartLine = start,
                EndLine = start + lines.Count - 1,
                Code = string.Join("\n", lines.Select(l => TextClip.Truncate(l, 200))),
                Reason = reason,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static readonly string[] LandmarkNames =
    [
        "README.md", "README", "package.json", "run.ps1", "run.sh", "Makefile", "Dockerfile",
        "docker-compose.yml", "docker-compose.yaml", "pyproject.toml", "requirements.txt", "setup.py",
        "Cargo.toml", "go.mod", "global.json", "launchSettings.json", "index.html", "vite.config.ts",
        "vite.config.js", "Program.cs", "main.py", "main.go", "manage.py", "app.py",
        "*.sln", "*.slnx", "*.csproj",
    ];

    /// <summary>README, manifests and entry points within four levels of the root, shallowest first.</summary>
    /// <remarks>
    /// One ripgrep listing glob-matched in memory, rather than a managed tree walk:
    /// the walk ignored .gitignore, so .venv, target and dist were traversed on every
    /// landmark scan - up to thirteen full walks per search against the budget.
    /// </remarks>
    internal async Task<IReadOnlyList<string>> FindLandmarksAsync(CancellationToken ct = default)
    {
        try
        {
            var files = await _search.ListFilesAsync(ct: ct).ConfigureAwait(false);
            var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var depth in new[] { "", "*/", "*/*/", "*/*/*/" })
            {
                foreach (var name in LandmarkNames)
                {
                    matcher.AddInclude(depth + name);
                }
            }

            return matcher
                .Match(_root, files)
                .Files
                .Select(f => f.Path.Replace('/', Path.DirectorySeparatorChar))
                .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(25)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    internal static bool IsSetupQuestion(string query) => SetupQuestion().IsMatch(query);

    [GeneratedRegex(@"\b(run|runs|running|start|launch|build|compile|install|setup|set up|deploy|debug|serve|execute)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SetupQuestion();

    [GeneratedRegex(@"^#+.*\b(run|start|build|install|setup|getting started|usage)", RegexOptions.IgnoreCase)]
    private static partial Regex SetupHeading();

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    private static bool IsSetupFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("README", StringComparison.OrdinalIgnoreCase) ||
               name is "package.json" or "run.ps1" or "run.sh" or "Makefile" or "Dockerfile" or
                   "pyproject.toml" or "launchSettings.json" or "docker-compose.yml";
    }

    /// <summary>Where a setup file gets interesting: package.json's scripts, a README's run/build heading.</summary>
    private int FindAnchor(string relativePath)
    {
        try
        {
            var number = 0;
            foreach (var line in File.ReadLines(Path.GetFullPath(relativePath, _root)))
            {
                if (++number > 400)
                {
                    break;
                }

                if (line.Contains("\"scripts\"", StringComparison.Ordinal) || SetupHeading().IsMatch(line))
                {
                    return Math.Max(1, number - 1);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return 1;
    }

    private const string WriteUpSystemPrompt =
        """
        You explain a code repository to a developer. Answer the question using ONLY the
        file excerpts given. Write Markdown in this shape:

        A one or two sentence direct answer.

        ## Steps
        Numbered steps. Put every command in a fenced code block. Omit this section if
        the question is not about doing something.

        ## Where it lives
        Bullets: `path` - what that part of the file does for this question.

        ## Notes
        Prerequisites, caveats, alternatives. Omit if there are none.

        Never invent files, commands or settings the excerpts do not show. If the
        excerpts do not answer the question, say so plainly. Stay under 250 words.
        """;

    private static string BuildWriteUpPrompt(string query, string summary, IReadOnlyList<SearchSection> sections)
    {
        // The write-up shares the model's small context with its own output; the
        // excerpts get what is left after room for a few hundred tokens of answer.
        const int maxChars = 7000;

        var builder = new StringBuilder();
        builder.AppendLine($"Question: {query}");
        if (summary.Length > 0)
        {
            builder.AppendLine($"Search conclusion: {summary}");
        }
        builder.AppendLine();
        builder.AppendLine("Excerpts:");

        foreach (var section in sections)
        {
            var block = $"--- {section.RelativePath} (lines {section.StartLine}-{section.EndLine})\n{section.Code}\n";
            if (builder.Length + block.Length > maxChars)
            {
                break;
            }

            builder.Append(block);
        }

        return builder.ToString();
    }

    /// <summary>Drops a reasoning block if the model emitted one anyway.</summary>
    internal static string CleanWriteUp(string text) => ThinkBlock().Replace(text, "").Trim();

    private async Task<(string Description, IReadOnlyList<SearchHit> Results, string Text)> ExecuteAsync(
        ToolCall call, CancellationToken ct)
    {
        switch (call.Tool)
        {
            case "grep":
            {
                var pattern = call.GetString("pattern") ?? "";
                var glob = call.GetString("glob");

                string? error = null;
                var results = await CollectAsync(new SearchQuery
                {
                    Text = pattern,
                    IsRegex = true,
                    Glob = glob,
                    MaxResults = 30,
                }, ct, message => error = message).ConfigureAwait(false);

                // The model has to hear that its pattern was rejected, or it reads
                // "0 matches" and concludes the code is not there.
                return ($"grep \"{pattern}\"{(glob is null ? "" : $" in {glob}")}",
                        results,
                        error is not null
                            ? $"grep \"{pattern}\" failed: {error}"
                            : $"grep \"{pattern}\" returned {results.Count} matches:\n{Summarise(results)}");
            }

            case "find_files":
            {
                var glob = call.GetString("glob") ?? "*";
                var files = await FindFilesAsync(glob, ct).ConfigureAwait(false);

                return ($"find_files {glob}",
                        [],
                        $"find_files {glob} returned {files.Count} paths:\n{string.Join("\n", files.Take(50))}");
            }

            case "read_file":
            {
                var path = call.GetString("path") ?? "";
                var start = call.GetInt("start") ?? 1;
                var end = call.GetInt("end") ?? start + 60;

                var text = ReadLines(path, start, end);
                return ($"read {path}:{start}-{end}", [], text);
            }

            default:
                return ($"unknown tool {call.Tool}", [], $"There is no tool called {call.Tool}.");
        }
    }

    private async Task<IReadOnlyList<SearchHit>> CollectAsync(
        SearchQuery query, CancellationToken ct, Action<string>? onError = null)
    {
        var results = new List<SearchHit>();

        try
        {
            await foreach (var hit in _search.SearchAsync(query, ct).ConfigureAwait(false))
            {
                // What the loop collects is what the model is shown.
                if (!SensitivePaths.IsSensitive(hit.RelativePath))
                {
                    results.Add(hit);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RipgrepSearchException ex)
        {
            onError?.Invoke(ex.Message);
        }

        return results;
    }

    private Task<IReadOnlyList<SearchHit>> SeedGrepAsync(string query, CancellationToken ct) =>
        CollectAsync(new SearchQuery
        {
            Text = KeywordsFrom(query),
            IsRegex = true,
            MaxResults = 30,
        }, ct);

    /// <summary>
    /// Turns a question into a regex of its content words, so the opening grep is about
    /// the subject rather than about "where", "the" and "we".
    /// </summary>
    public static string KeywordsFrom(string query)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "where", "what", "which", "how", "why", "who", "when", "does", "do", "is", "are",
            "the", "a", "an", "we", "i", "it", "in", "on", "at", "of", "for", "to", "and",
            "or", "our", "this", "that", "handle", "handled", "code", "find", "show", "me",
        };

        var words = query
            .Split(new[] { ' ', '\t', '\n', '?', '.', ',', '!', ':', ';', '(', ')', '"', '\'' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stop.Contains(w))
            .Select(System.Text.RegularExpressions.Regex.Escape)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        return words.Count == 0 ? System.Text.RegularExpressions.Regex.Escape(query.Trim()) : string.Join("|", words);
    }

    private async Task<IReadOnlyList<string>> FindFilesAsync(string glob, CancellationToken ct = default)
    {
        try
        {
            // The model likes this tool; a managed walk per step burned the search budget
            // enumerating ignored trees. One ripgrep listing, glob-matched in memory.
            var files = await _search.ListFilesAsync(ct: ct).ConfigureAwait(false);
            var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
            matcher.AddInclude(glob);

            return matcher
                .Match(_root, files)
                .Files
                .Select(f => f.Path.Replace('/', Path.DirectorySeparatorChar))
                .Take(200)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <summary>Reads a clipped window of a file: never more than the context can take.</summary>
    private string ReadLines(string relativePath, int start, int end)
    {
        const int maxLines = 80;
        const int maxChars = 4000;

        try
        {
            var full = Path.GetFullPath(relativePath, _root);

            // Refuse to wander outside the project.
            if (!ProjectPaths.IsInside(_root, full))
            {
                return $"{relativePath} is outside the project.";
            }

            // What is read here is sent to the model's endpoint; credentials stay home.
            if (SensitivePaths.IsSensitive(relativePath) || SensitivePaths.IsSensitive(full))
            {
                return $"{relativePath}{SensitivePaths.RefusalSuffix}";
            }

            if (!File.Exists(full))
            {
                return $"{relativePath} does not exist.";
            }

            start = Math.Max(1, start);
            end = Math.Min(end, start + maxLines - 1);

            var builder = new StringBuilder();
            builder.AppendLine($"{relativePath}:{start}-{end}");

            var number = 0;
            foreach (var line in File.ReadLines(full))
            {
                number++;
                if (number < start)
                {
                    continue;
                }

                if (number > end || builder.Length > maxChars)
                {
                    break;
                }

                builder.AppendLine($"{number}: {line}");
            }

            return builder.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Could not read {relativePath}.";
        }
    }

    private static string Summarise(IReadOnlyList<SearchHit> hits)
    {
        if (hits.Count == 0)
        {
            return "(no matches)";
        }

        // Kept tight on purpose: every findings line rides along in the prompt for the
        // rest of the loop, and prefill on a long context is what makes steps slow. The
        // ranked hit list the user sees is separate and keeps everything.
        var builder = new StringBuilder();

        foreach (var hit in hits.Take(15))
        {
            var line = hit.Line.Trim();
            builder.AppendLine($"{hit.RelativePath}:{hit.LineNumber}: {TextClip.Truncate(line, 100)}");
        }

        if (hits.Count > 15)
        {
            builder.AppendLine($"(+{hits.Count - 15} more matches)");
        }

        return builder.ToString();
    }

    /// <summary>Files with more matches first; duplicates of the same line removed.</summary>
    private static IReadOnlyList<SearchHit> Rank(IReadOnlyList<SearchHit> hits)
    {
        var distinct = hits
            .GroupBy(h => (h.FilePath, h.LineNumber))
            .Select(g => g.First())
            .ToList();

        var weight = distinct
            .GroupBy(h => h.FilePath)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return distinct
            .OrderByDescending(h => weight[h.FilePath])
            .ThenBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.LineNumber)
            .Take(100)
            .ToList();
    }

    private static string BuildSystemPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are searching a code repository to answer a question.");
        builder.AppendLine($"Use the findings so far to decide the next calls. Make up to {MaxCallsPerStep} calls at once when they do not depend on each other (for example a grep and the reads of files already found): each reply costs time, so batch.");
        builder.AppendLine("Call answer as soon as you can answer; do not keep searching once you know.");
        builder.AppendLine("Before answering, read_file the lines that actually handle the question, so they can be shown.");
        builder.AppendLine("For questions about running, building or setup, look at the key project files first.");
        builder.AppendLine();
        builder.AppendLine("Tools:");

        foreach (var tool in Tools)
        {
            builder.AppendLine(tool.ToPromptLine());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the model's view of the search: the question plus compacted findings.
    /// </summary>
    /// <remarks>
    /// Deliberately not a growing message history. A small model's context cannot
    /// hold raw tool output from a dozen steps, so the findings are kept as a running
    /// digest and trimmed from the front when they get long - the recent steps are the
    /// ones that inform the next call.
    /// </remarks>
    private static string BuildConversation(string query, string findings)
    {
        const int maxFindings = 6000;

        if (findings.Length > maxFindings)
        {
            findings = "(earlier findings trimmed)\n" + findings[^maxFindings..];
        }

        return $"Question: {query}\n\nFindings so far:\n{findings}";
    }
}
