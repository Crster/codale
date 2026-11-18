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

    /// <summary>
    /// Ranking quality and cost of the index on this repository: the labelled questions as written, and as the bare
    /// symbol a person would type. Prints first/top-5 hits, latency, build and refresh time and database size (run
    /// with <c>--logger "console;verbosity=detailed"</c>); fails if top-5 recall drops below its floor.
    /// </summary>
    [Fact]
    public async Task Index_benchmark()
    {
        var exact = _labels.Select(l => new Label(l.Symbol, l.Path, l.Symbol)).ToArray();
        var dbPath = Path.Combine(_directory, "benchmark.db");
        using var store = new CodaleDb(dbPath);
        using var index = new ProjectKnowledgeIndex(store);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await index.RefreshAsync(_corpus);
        var initial = clock.Elapsed.TotalMilliseconds;
        foreach (var label in _labels) index.Search(_corpus, label.Query, maxFiles: 5);
        foreach (var (name, labels) in new[] { ("natural", _labels.ToArray()), ("symbol", exact) })
        {
            var at1 = 0;
            var at5 = 0;
            var timings = new List<double>();
            for (var pass = 0; pass < 3; pass++)
            foreach (var label in labels)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                var paths = index.Search(_corpus, label.Query, maxFiles: 5, maxChunksPerFile: 1)
                    .Select(c => c.Path.Replace('\\', '/')).ToArray();
                timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (pass != 0) continue;
                if (paths.FirstOrDefault() == label.Path) at1++;
                if (paths.Contains(label.Path)) at5++;
            }
            timings.Sort();
            _output.WriteLine($"{name}: first={at1}/{labels.Length}, top5={at5}/{labels.Length}, median={timings[timings.Count / 2]:F2}ms, p95={timings[(int)Math.Ceiling(timings.Count * .95) - 1]:F2}ms");
            // Floors: raise them as retrieval improves, never lower them to make a change pass.
            Assert.True(at5 / (double)labels.Length >= Floor("FILE_AT_5", 0.95), $"{name} top5 {at5}/{labels.Length}");
        }
        var probe = Path.Combine(_corpus, "refresh-probe.cs");
        await File.WriteAllTextAsync(probe, "public class RefreshProbe {}");
        // Allow the watcher to mark the shared source snapshot stale before timing the refresh.
        await Task.Delay(100);
        clock.Restart();
        await index.RefreshAsync(_corpus);
        var bytes = Directory.Exists(dbPath) ? Directory.EnumerateFiles(dbPath, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : new FileInfo(dbPath).Length;
        _output.WriteLine($"initial={initial:F0}ms, single-file refresh={clock.Elapsed.TotalMilliseconds:F0}ms, db={bytes:N0} bytes");
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

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
