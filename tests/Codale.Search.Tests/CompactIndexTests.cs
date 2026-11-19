using Codale.Core.Projects;
using Codale.Db;
using Codale.Db.Search;

namespace Codale.Search.Tests;

public sealed class CompactIndexTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("codale-compact-").FullName;

    [Fact]
    public async Task Git_history_is_loaded_on_demand_without_blocking_search()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(root, ".git", "HEAD"), new string('a', 40));
        await File.WriteAllTextAsync(Path.Combine(root, "Writer.cs"), "public class Writer {}");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<IReadOnlyList<ProjectKnowledgeIndex.FileCommit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var db = new CodaleDb(Path.Combine(_temp, "index"));
        using var index = new ProjectKnowledgeIndex(db)
        {
            ReadFileHistory = (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                return finish.Task;
            }
        };
        await index.RefreshAsync(root);
        Assert.Equal(0, calls);
        Assert.NotEmpty(await index.SearchAsync(root, "Writer").WaitAsync(TimeSpan.FromSeconds(5)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        finish.SetResult([new(new string('b', 40), "Retain journal entries", 1)]);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lexical_search_works_with_the_model_off_or_unavailable(bool enabled)
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "Writer.cs"), "public class Writer {}");
        var embedder = new UnavailableEmbedder();
        using var db = new CodaleDb(Path.Combine(_temp, "index"), new() { Embedder = embedder });
        using (var index = new ProjectKnowledgeIndex(db)
        {
            CompactEmbeddingsEnabled = () => enabled
        })
        {
            await index.RefreshAsync(root);
            Assert.Equal("Writer.cs", Assert.Single(await index.SearchAsync(root, "Writer", maxChunksPerFile: 1)).Path);
            // Embedding runs in the background, off the search path; give it time to try before disposing.
            var limit = DateTime.UtcNow.AddSeconds(5);
            while (enabled && Volatile.Read(ref embedder.Calls) == 0 && DateTime.UtcNow < limit) await Task.Delay(10);
        }
        Assert.Equal(enabled ? 1 : 0, embedder.Calls);
    }

    private sealed class UnavailableEmbedder : IEmbedder
    {
        public string Model => "unavailable-test-model";
        public int Calls;
        public float[] Embed(string text)
        {
            Interlocked.Increment(ref Calls);
            throw new HttpRequestException("Provider unavailable");
        }
    }

    [Fact]
    public async Task Profile_search_prefers_exact_declarations_and_finds_documentation_subjects()
    {
        var root = ProjectPaths.Normalize(Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName);
        await File.WriteAllTextAsync(Path.Combine(root, "Writer.cs"), "/// <summary>Durable journal recovery</summary>\npublic class Writer {}");
        await File.WriteAllTextAsync(Path.Combine(root, "Other.cs"), "// Many mentions: Writer Writer Writer\npublic class Other {}");
        using var db = new CodaleDb(Path.Combine(_temp, "index"));
        using var index = new ProjectKnowledgeIndex(db);
        await index.RefreshAsync(root);
        Assert.Equal("Writer.cs", index.Search(root, "Writer")[0].Path);
        Assert.Equal("Writer.cs", index.Search(root, "durable journal recovery")[0].Path);
        Assert.Equal(2, db.Open("compact-source-v1").Open(root).ChildCount());
        Assert.False(db.Open("source").Open(root).Open("chunk").Descendants().Any());
    }

    [Fact]
    public async Task A_named_declaration_counts_once_per_file_and_prose_capitals_name_nothing()
    {
        var root = ProjectPaths.Normalize(Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName);
        await File.WriteAllTextAsync(Path.Combine(root, "BranchWatch.cs"),
            "// Same branch check when HEAD moves between refreshes\npublic class BranchWatch\n{\n    public bool Head() => true;\n    public bool SameBranch() => Head();\n}");
        // Two declarations of one name are still one file declaring it.
        await File.WriteAllTextAsync(Path.Combine(root, "Noise.cs"), """
            public class A
            {
                public void Head()
                {
                }
            }

            public class B
            {
                public void Head()
                {
                }
            }
            """);
        // "Auto Mode" in a request is a label the user sees, not a reference to these members.
        await File.WriteAllTextAsync(Path.Combine(root, "Modes.cs"), """
            public class Modes
            {
                public void Auto()
                {
                }

                public void Mode()
                {
                }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "ChatRouting.cs"), "// Route the chat turn by the auto mode the user picked\npublic class ChatRouting { public void RouteByMode() { } }");
        using var db = new CodaleDb(Path.Combine(_temp, "index"));
        using var index = new ProjectKnowledgeIndex(db);
        await index.RefreshAsync(root);
        Assert.Equal("BranchWatch.cs", index.Search(root, "same branch check when HEAD moves between refreshes")[0].Path);
        Assert.Equal("ChatRouting.cs", index.Search(root, "route the chat turn by the Auto Mode")[0].Path);
    }

    [Fact]
    public void Binary_oversized_and_minified_files_are_not_source()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName;
        void Write(string name, string text) => File.WriteAllText(Path.Combine(root, name), text);
        Write("Code.cs", "class A {}");
        Write("Empty.cs", "");
        Write("Binary.cs", "a\0b");
        Write("Huge.cs", new string('x', ProjectKnowledgeIndex.MaxFileBytes + 1));
        Write("Bundle.js", string.Join(' ', Enumerable.Range(0, 60_000).Select(i => "a" + i)));
        // A long hand-written file has short lines: it is source however big it is.
        Write("Long.cs", string.Join("\n", Enumerable.Range(0, 12_000).Select(i => $"    var value{i} = Compute({i});")));
        Assert.Equal("class A {}", ProjectKnowledgeIndex.ReadSource(root, "Code.cs"));
        Assert.NotNull(ProjectKnowledgeIndex.ReadSource(root, "Long.cs"));
        foreach (var name in (string[])["Empty.cs", "Binary.cs", "Huge.cs", "Bundle.js", "Missing.cs"])
            Assert.Null(ProjectKnowledgeIndex.ReadSource(root, name));
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);
}
