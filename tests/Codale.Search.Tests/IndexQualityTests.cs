using System.Diagnostics;
using System.Runtime.InteropServices;
using Codale.Core.Projects;
using Codale.Db;
using Xunit.Abstractions;

namespace Codale.Search.Tests;

/// <summary>
/// Opt-in (set CODALE_QUALITY_PROJECT to a project root): indexes a copy of the project and reports what the index
/// costs (CPU, memory, disk) and whether it answers the three things it exists for - search, as-you-type
/// suggestions and the ghost-text header - with the right files and names.
/// </summary>
public sealed class IndexQualityTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("codale-quality-").FullName;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    private static IoCounters Io()
    {
        GetProcessIoCounters(Process.GetCurrentProcess().Handle, out var c);
        return c;
    }

    private static double Mb(long bytes) => bytes / 1048576.0;

    private string CopyProject(string source)
    {
        var target = Path.Combine(_work, "project");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => p is "bin" or "obj" or ".git" or ".vs" or "node_modules" or "packages") || new FileInfo(file).Length > 1_000_000) continue;
            var to = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        return ProjectPaths.Normalize(target);
    }

    [Fact]
    public async Task Index_costs_and_answers()
    {
        var project = Environment.GetEnvironmentVariable("CODALE_QUALITY_PROJECT");
        if (string.IsNullOrWhiteSpace(project)) return;
        var root = CopyProject(project);
        var sourceFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();

        // ---- cost of the first index
        using var db = new CodaleDb(Path.Combine(_work, "db"));
        using var index = new ProjectKnowledgeIndex(db);
        GC.Collect();
        var heap0 = GC.GetTotalMemory(true);
        var io0 = Io();
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        await index.RefreshAsync(root);
        var firstWall = clock.Elapsed;
        var cpu1 = Process.GetCurrentProcess().TotalProcessorTime;
        var io1 = Io();
        var heap1 = GC.GetTotalMemory(false);
        var peak = Process.GetCurrentProcess().PeakWorkingSet64;

        // ---- cost of a refresh when nothing changed (what every file-watch tick pays)
        clock.Restart();
        await index.RefreshAsync(root);
        var idleWall = clock.Elapsed;
        var cpu2 = Process.GetCurrentProcess().TotalProcessorTime;
        var io2 = Io();

        // ---- cost of one edited file
        var edited = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).First();
        File.AppendAllText(edited, "\npublic class QualityProbeAddedClass { }\n");
        await Task.Delay(700); // the file watcher marks the scan stale
        clock.Restart();
        await index.RefreshAsync(root);
        var editWall = clock.Elapsed;
        var io3 = Io();
        var cpu3 = Process.GetCurrentProcess().TotalProcessorTime;

        var dbBytes = Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}project{Path.DirectorySeparatorChar}")).Sum(f => new FileInfo(f).Length);
        output.WriteLine($"files={sourceFiles} first: wall={firstWall.TotalSeconds:F1}s cpu={(cpu1 - cpu0).TotalSeconds:F1}s read={Mb((long)(io1.ReadBytes - io0.ReadBytes)):F0}MB write={Mb((long)(io1.WriteBytes - io0.WriteBytes)):F0}MB heap_delta={Mb(heap1 - heap0):F0}MB peak_ws={Mb(peak):F0}MB store_on_disk={Mb(dbBytes):F1}MB");
        output.WriteLine($"unchanged refresh: wall={idleWall.TotalMilliseconds:F0}ms cpu={(cpu2 - cpu1).TotalMilliseconds:F0}ms read={Mb((long)(io2.ReadBytes - io1.ReadBytes)):F1}MB write={Mb((long)(io2.WriteBytes - io1.WriteBytes)):F1}MB");
        output.WriteLine($"one edited file: wall={editWall.TotalMilliseconds:F0}ms cpu={(cpu3 - cpu2).TotalMilliseconds:F0}ms read={Mb((long)(io3.ReadBytes - io2.ReadBytes)):F1}MB write={Mb((long)(io3.WriteBytes - io2.WriteBytes)):F1}MB");

        // ---- search: natural-language queries and the file that should come back
        var searches = new (string Query, string Expect)[]
        {
            ("where is the status bar indexer chip", "WorkspaceHealthViewModel"),
            ("how does the code editor show completion popup", "CodeEditor.Completion"),
            ("ghost text inline completion", "CodeEditor.Ghost"),
            ("project knowledge index refresh", "ProjectKnowledgeIndex"),
            ("workspace model suggest prefix", "WorkspaceModel"),
            ("search view model run query", "SearchViewModel"),
            ("completion context header declarations", "CompletionContext"),
            ("file chunker splits text into chunks", "FileChunker"),
            ("git diff gutter markers", "DiffGutter"),
            ("terminal view", "TerminalView"),
        };
        var searchHits = 0;
        var searchMs = 0.0;
        foreach (var (query, expect) in searches)
        {
            clock.Restart();
            var found = index.Search(root, query, "", 8, 1);
            searchMs += clock.Elapsed.TotalMilliseconds;
            var rank = found.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Path.Contains(expect, StringComparison.OrdinalIgnoreCase));
            var hit = rank.c is not null;
            if (hit) searchHits++;
            output.WriteLine($"search [{(hit ? "top" + (rank.i + 1) : "MISS")}] \"{query}\" -> {string.Join(", ", found.Take(4).Select(c => Path.GetFileName(c.Path)))}");
        }

        // Typing: a lookup per identifier change, as the ghost-text path makes. Each also asks for a refresh.
        var cpuT = Process.GetCurrentProcess().TotalProcessorTime;
        var ioT = Io();
        clock.Restart();
        for (var i = 0; i < 40; i++) await index.SearchAsync(root, $"SourceIndex Suggest prefix{i} previousWord RefreshAsync", "", 4, 1);
        output.WriteLine($"40 typing lookups: wall={clock.Elapsed.TotalMilliseconds:F0}ms cpu={(Process.GetCurrentProcess().TotalProcessorTime - cpuT).TotalMilliseconds:F0}ms read={Mb((long)(Io().ReadBytes - ioT.ReadBytes)):F1}MB write={Mb((long)(Io().WriteBytes - ioT.WriteBytes)):F1}MB");

        output.WriteLine($"SEARCH hits={searchHits}/{searches.Length} avg_ms={searchMs / searches.Length:F1}");

        // ---- as-you-type suggestions: a prefix, the previous word, and a name that should be offered
        using var scan = new SourceIndex(root, new SourceIndexOptions { ModelPath = Path.Combine(_work, "model.json") });
        await scan.GetSnapshotAsync();
        var suggestions = new (string Prefix, string? Previous, string Expect)[]
        {
            ("WorkspaceH", null, "WorkspaceHealthViewModel"),
            ("Project", "new", "ProjectKnowledgeIndex"),
            ("Source", "new", "SourceIndex"),
            ("Complet", null, "CompletionContext"),
            ("IsIndex", null, "IsIndexing"),
            ("Refresh", null, "RefreshAsync"),
            ("QualityProbe", null, "QualityProbeAddedClass"),
            ("Searc", "private readonly", "SearchViewModel"),
        };
        var suggestHits = 0;
        var suggestMs = 0.0;
        foreach (var (prefix, previous, expect) in suggestions)
        {
            clock.Restart();
            var list = scan.Suggest(prefix, previous);
            suggestMs += clock.Elapsed.TotalMilliseconds;
            var at = list.ToList().FindIndex(s => s.Text.Equals(expect, StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && at < 10) suggestHits++;
            output.WriteLine($"suggest [{(at < 0 ? "MISS" : "#" + (at + 1))}] '{prefix}' after '{previous}' -> {string.Join(", ", list.Take(5).Select(s => s.Text))}");
        }

        output.WriteLine($"SUGGEST hits(top10)={suggestHits}/{suggestions.Length} avg_ms={suggestMs / suggestions.Length:F2}");

        // ---- ghost text: does the header carry the declarations the next line needs?
        var snapshot = scan.Current;
        var file = Path.Combine(root, "src", "Codale.App", "Views", "EditorTab.xaml.cs");
        const string nearby = "var index = Codale.Search.SourceIndex.For(ProjectRoot);\nvar list = index.Suggest(prefix, previousWord);\nvar knowledge = new ProjectKnowledgeIndex(db);\nknowledge.RefreshAsync(";
        var related = index.Search(root, nearby, "", 4, 1).Select(c => (c.Path, c.Text)).ToList();
        clock.Restart();
        var header = Codale.Search.CompletionContext.Build(snapshot, root, file, "csharp", nearby, related);
        output.WriteLine($"ghost header {header.Length} chars in {clock.Elapsed.TotalMilliseconds:F1}ms (+related lookup):\n{header}");
        Assert.Contains("ProjectKnowledgeIndex", header);
        Assert.True(searchHits >= searches.Length * 0.7, "search should return the expected file for most queries");
        Assert.True(suggestHits >= suggestions.Length * 0.7, "suggestions should offer the expected name for most prefixes");
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
