using System.Text.Json;
using System.Text.RegularExpressions;
using Codale.Db;
using Xunit.Abstractions;

namespace Codale.Search.Tests;

/// <summary>
/// Retrieval quality on this repository's own source. Each labelled query in Eval/queries.json names the file and the
/// symbol a person asking it wants; labels are checked against the source so a stale one fails loudly. Scores guard
/// against regressions and print as a table (run with <c>--logger "console;verbosity=detailed"</c> to see it).
/// </summary>
public sealed partial class RetrievalEvalTests : IDisposable
{
    private sealed record Label(string Query, string Path, string Symbol);

    private readonly ITestOutputHelper _output;
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-eval-").FullName;
    private readonly string _corpus;
    private readonly List<Label> _labels;

    public RetrievalEvalTests(ITestOutputHelper output)
    {
        _output = output;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Codale.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        _corpus = Path.Combine(_directory, "corpus");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root.FullName, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin")) continue;
            var target = Path.Combine(_corpus, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        _labels = JsonSerializer.Deserialize<List<Label>>(File.ReadAllText(Path.Combine(root.FullName, "tests", "Codale.Search.Tests", "Eval", "queries.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void Every_label_names_a_symbol_that_exists_in_its_file()
    {
        foreach (var label in _labels)
        {
            var path = Path.Combine(_corpus, label.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{label.Path} is gone: update Eval/queries.json");
            Assert.True(DeclarationLine(File.ReadAllLines(path), label.Symbol) > 0, $"{label.Symbol} is not in {label.Path}");
        }
    }

    [Fact]
    public async Task Retrieval_finds_the_file_and_the_lines_a_labelled_question_is_about()
    {
        using var store = new CodaleDb(Path.Combine(_directory, "eval.db"));
        using var index = new ProjectKnowledgeIndex(store);
        await index.RefreshAsync(_corpus);

        double reciprocal = 0;
        int at1 = 0, at5 = 0, inContext = 0, lineInContext = 0, bytes = 0;
        var rows = new List<string>();
        foreach (var label in _labels)
        {
            var ranked = (await index.SearchAsync(_corpus, label.Query, maxFiles: 12)).Select(c => c.Path.Replace('\\', '/')).Distinct().ToList();
            var rank = ranked.IndexOf(label.Path) + 1;
            if (rank > 0) reciprocal += 1.0 / rank;
            if (rank == 1) at1++;
            if (rank is >= 1 and <= 5) at5++;

            var context = index.Retrieve(_corpus, label.Query, "", new HashSet<string>());
            bytes += context.Tokens;
            var fileInContext = context.Files.Contains(label.Path);
            if (fileInContext) inContext++;
            var line = DeclarationLine(File.ReadAllLines(Path.Combine(_corpus, label.Path.Replace('/', Path.DirectorySeparatorChar))), label.Symbol);
            var covered = Covers(context.Text, label.Path, line);
            if (covered) lineInContext++;
            rows.Add($"{(rank == 0 ? "-" : rank.ToString()),3} {(fileInContext ? "F" : ".")}{(covered ? "L" : ".")}  {label.Symbol,-28} {label.Query}");
        }

        var n = _labels.Count;
        _output.WriteLine("rank ctx  symbol                       query");
        rows.ForEach(_output.WriteLine);
        _output.WriteLine($"queries={n}  file@1={at1 / (double)n:P0}  file@5={at5 / (double)n:P0}  MRR={reciprocal / n:F3}  " +
                          $"context-has-file={inContext / (double)n:P0}  context-has-line={lineInContext / (double)n:P0}  avg-context-bytes={bytes / n}");

        // Floors: raise them as retrieval improves, never lower them to make a change pass.
        Assert.True(at5 / (double)n >= Floor("FILE_AT_5", 0.95), $"file@5 {at5}/{n}");
        Assert.True(lineInContext / (double)n >= Floor("LINE_IN_CONTEXT", 0.65), $"context-has-line {lineInContext}/{n}");
    }

    /// <summary>
    /// Opt-in (set CODALE_EVAL_PROJECT to a project root Codale has worked on): scores retrieval against what really
    /// happened there. Every file the agent edited, or the user opened from a search, for a prompt is a label for that
    /// prompt. The history is read from a copy of the project's database, and the scoring index is fresh and empty of
    /// learned associations, so the history cannot vouch for itself.
    /// </summary>
    [Fact]
    public async Task Real_usage_history_labels_score_retrieval()
    {
        var project = Environment.GetEnvironmentVariable("CODALE_EVAL_PROJECT");
        if (string.IsNullOrWhiteSpace(project)) return;

        var source = Codale.Core.Projects.ProjectPaths.FeatureDatabasePath(project);
        Assert.True(Directory.Exists(source), $"no Codale history for {project}");
        var copy = Path.Combine(_directory, "history");
        CopyFolder(source, copy);
        using var history = new CodaleDb(copy);
        using var historyIndex = new ProjectKnowledgeIndex(history);
        var root = Codale.Core.Projects.ProjectPaths.Normalize(project);

        // Prompt -> the files that answered it, strongest evidence only (reads are noisy as labels).
        var labelled = historyIndex.LearnedAssociations(root, 2000)
            .Where(a => Codale.Core.Features.LearnedKind.Strength(a.Kind) >= Codale.Core.Features.LearnedKind.Strength(Codale.Core.Features.LearnedKind.Open))
            .GroupBy(a => a.Prompt).Select(g => (Prompt: g.Key, Files: g.Select(a => a.Path.Replace('\\', '/')).ToHashSet())).Take(100).ToList();
        if (labelled.Count == 0) { _output.WriteLine("no edit or search-open history yet"); return; }

        using var store = new CodaleDb(Path.Combine(_directory, "real.db"));
        using var index = new ProjectKnowledgeIndex(store);
        await index.RefreshAsync(root);

        double reciprocal = 0;
        int at1 = 0, at5 = 0, scored = 0;
        foreach (var (prompt, files) in labelled)
        {
            files.RemoveWhere(f => !File.Exists(Path.Combine(root, f)));   // deleted or renamed since
            if (files.Count == 0) continue;
            scored++;
            var ranked = (await index.SearchAsync(root, prompt, maxFiles: 12)).Select(c => c.Path.Replace('\\', '/')).Distinct().ToList();
            var rank = ranked.FindIndex(files.Contains) + 1;
            if (rank > 0) reciprocal += 1.0 / rank;
            if (rank == 1) at1++;
            if (rank is >= 1 and <= 5) at5++;
        }
        if (scored == 0) { _output.WriteLine("every labelled file is gone"); return; }
        _output.WriteLine($"real usage: prompts={scored}  file@1={at1 / (double)scored:P0}  file@5={at5 / (double)scored:P0}  MRR={reciprocal / scored:F3}");
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        // The LOCK file is held by the running app and is recreated on open.
        foreach (var file in Directory.GetFiles(from).Where(f => Path.GetFileName(f) != "LOCK"))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }

    private static double Floor(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable("CODALE_EVAL_" + name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>1-based line declaring <paramref name="symbol"/>: a definition if there is one, otherwise its first whole-word mention.</summary>
    private static int DeclarationLine(string[] lines, string symbol)
    {
        var word = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(symbol) + @"(?![A-Za-z0-9_])");
        var mention = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!word.IsMatch(lines[i])) continue;
            if (mention == 0) mention = i + 1;
            if (Regex.IsMatch(lines[i], @"^\s*(?:\w+\s+)*(?:class|record|struct|interface|enum|const|[\w<>\[\],.?()]+)\s+" + Regex.Escape(symbol) + @"\b\s*(?:[(<=;{:]|$)")
                && !Regex.IsMatch(lines[i], @"^\s*(return|var|await|if|foreach|using)\b")) return i + 1;
        }
        return mention;
    }

    /// <summary>True when an excerpt header "path:start-end" in the context spans <paramref name="line"/>.</summary>
    private static bool Covers(string context, string path, int line)
    {
        foreach (Match m in ExcerptHeader().Matches(context))
            if (m.Groups["path"].Value == path && int.Parse(m.Groups["a"].Value) <= line && line <= int.Parse(m.Groups["b"].Value)) return true;
        return false;
    }

    [GeneratedRegex(@"^(?<path>\S+\.cs):(?<a>\d+)-(?<b>\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ExcerptHeader();

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
