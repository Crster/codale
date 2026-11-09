namespace Codale.Search.Tests;

public sealed class WorkspaceSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-search-").FullName;

    [Fact(Timeout = 30_000)]
    public async Task Stopping_at_max_results_does_not_wait_for_the_scan_to_finish()
    {
        // Far more output than a pipe buffer holds: if the search stops reading and then
        // waits for the scan to finish, the scan blocks on a full channel and this never returns.
        for (var file = 0; file < 200; file++)
        {
            File.WriteAllLines(
                Path.Combine(_root, $"f{file}.txt"),
                Enumerable.Range(0, 50).Select(i => $"snake head line {i} {new string('x', 200)}"));
        }

        var hits = new List<SearchHit>();
        await foreach (var hit in new WorkspaceSearch(_root).SearchAsync(
                           new SearchQuery { Text = "snake|head", IsRegex = true, MaxResults = 30 }))
        {
            hits.Add(hit);
        }

        Assert.Equal(30, hits.Count);
    }

    [Fact(Timeout = 30_000)]
    public async Task Gitignore_rules_apply_per_folder_with_negation_and_directory_only_patterns()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "gen"));
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.log\n!keep.log\n/build/\nlogs/\n");
        File.WriteAllText(Path.Combine(_root, "src", ".gitignore"), "gen/\n*.tmp\n");
        File.WriteAllText(Path.Combine(_root, "app.cs"), "class App {}");
        File.WriteAllText(Path.Combine(_root, "debug.log"), "x");
        File.WriteAllText(Path.Combine(_root, "keep.log"), "x");
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_root, "src", "scratch.tmp"), "x");
        File.WriteAllText(Path.Combine(_root, "src", "gen", "g.cs"), "class G {}");
        File.WriteAllText(Path.Combine(_root, "logs", "run.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "build"));
        File.WriteAllText(Path.Combine(_root, "build", "out.cs"), "x");

        var files = (await new WorkspaceSearch(_root).ListFilesAsync()).Select(f => f.Replace('\\', '/')).OrderBy(f => f).ToList();

        Assert.Equal(new[] { "app.cs", "keep.log", "src/a.cs" }, files);
    }

    [Fact(Timeout = 30_000)]
    public async Task Hidden_entries_and_binary_files_are_not_searched()
    {
        File.WriteAllText(Path.Combine(_root, "visible.txt"), "needle here");
        File.WriteAllText(Path.Combine(_root, ".secret.txt"), "needle hidden");
        File.WriteAllBytes(Path.Combine(_root, "image.bin"), [0x00, 0x01, 0x6e, 0x65, 0x65, 0x64, 0x6c, 0x65]);

        var hits = new List<SearchHit>();
        await foreach (var hit in new WorkspaceSearch(_root).SearchAsync(new SearchQuery { Text = "needle" }))
        {
            hits.Add(hit);
        }

        var hit1 = Assert.Single(hits);
        Assert.Equal("visible.txt", hit1.RelativePath);
        Assert.Equal(0, hit1.MatchStart);
        Assert.Equal(6, hit1.MatchLength);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}