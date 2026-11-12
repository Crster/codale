using System.Diagnostics;
using Codale.Core.Projects;
using Codale.Db;
using Xunit.Abstractions;

namespace Codale.Search.Tests;

/// <summary>
/// Opt-in measurement (set CODALE_MEMORY_PROJECT to a project root): how much managed heap the project's index holds
/// after indexing it and answering a few questions. Prints one line; asserts nothing about the numbers.
/// </summary>
public sealed class MemoryFootprintTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-footprint-").FullName;

    [Fact]
    public async Task Footprint_of_a_real_project_index()
    {
        var project = Environment.GetEnvironmentVariable("CODALE_MEMORY_PROJECT");
        if (string.IsNullOrWhiteSpace(project)) return;
        var root = ProjectPaths.Normalize(project);

        using var db = new CodaleDb(Path.Combine(_directory, "db"));
        using var index = new ProjectKnowledgeIndex(db);
        var before = Heap();
        var clock = Stopwatch.StartNew();
        await index.RefreshAsync(root);
        var indexed = clock.Elapsed;
        var afterIndex = Heap();

        var files = index.SourceHashes(root).Count;
        var chunks = index.SourceChunksForPath(root, "src", 0).Count;
        foreach (var query in new[] { "refresh token", "owner picker", "chart layout", "commit message", "terminal output" })
            index.Retrieve(root, query, "", new HashSet<string>());
        var afterQueries = Heap();

        output.WriteLine($"files={files} indexed_in={indexed.TotalSeconds:F1}s " +
                         $"index_heap={Mb(afterIndex - before)}MB after_queries_heap={Mb(afterQueries - before)}MB " +
                         $"process_working_set={Mb(Environment.WorkingSet)}MB");
        Assert.True(files > 0);

        // Attribution: the database alone, fed the same chunks, one component at a time.
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        using var alone = new CodaleDb(Path.Combine(_directory, "alone"));
        using var aloneIndex = new ProjectKnowledgeIndex(alone);
        var baseline = Heap();
        foreach (var file in sources)
        {
            var rel = Path.GetRelativePath(root, file);
            var text = File.ReadAllText(file);
            aloneIndex.ReplaceSource("w", rel, ProjectKnowledgeIndex.Hash(text), Codale.Core.Features.FileChunker.Chunk(text, rel));
        }
        var withText = Heap();
        var nodes = alone.Open("source").Open("w").Open("chunk").Descendants().ToList();
        var chunkCount = nodes.Count;
        var textBytes = sources.Sum(f => (long)File.ReadAllText(f).Length * 2);
        foreach (var node in nodes) node.Embed(node.Location.ToString());
        var withVectors = Heap();
        // Read speed on the same data: keyword match, vector query, value read.
        var chunkRoot = alone.Open("source").Open("w");
        KeywordTerm[] terms = [new("token"), new("refresh", Prefix: true), new("index")];
        chunkRoot.Match("source", terms, 40);
        chunkRoot.Query("refresh token", 40);
        const int rounds = 200;
        clock.Restart();
        for (var i = 0; i < rounds; i++) chunkRoot.Match("source", terms, 40);
        var matchMs = clock.Elapsed.TotalMilliseconds / rounds;
        clock.Restart();
        for (var i = 0; i < rounds; i++) chunkRoot.Query("refresh token", 40);
        var queryMs = clock.Elapsed.TotalMilliseconds / rounds;
        clock.Restart();
        foreach (var node in nodes) node.Read();
        var readMicros = clock.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, nodes.Count);
        var afterReads = Heap();
        clock.Restart();
        alone.Dispose();
        using (var reopened = new CodaleDb(Path.Combine(_directory, "alone")))
            output.WriteLine($"speed match={matchMs:F3}ms query={queryMs:F3}ms read={readMicros:F1}us reopen={clock.Elapsed.TotalMilliseconds:F0}ms held_after_queries={(afterReads - baseline) / 1048576.0:F1}MB");
        output.WriteLine($"attribution chunks={chunkCount} source_text={Mb(textBytes)}MB text_and_keywords_and_tree={(withText - baseline) / 1048576.0:F1}MB vectors={(withVectors - withText) / 1048576.0:F1}MB");
    }

    private static long Heap()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true);
    }

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
