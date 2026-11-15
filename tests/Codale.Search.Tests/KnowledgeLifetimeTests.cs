using System.Diagnostics;
using Codale.Core.Agents;
using Codale.Db;

namespace Codale.Search.Tests;

/// <summary>Resource lifetime of the project knowledge index: disposal during work, use after disposal, and repeated disposal.</summary>
public sealed class KnowledgeLifetimeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-knowledge-lifetime-").FullName;
    private readonly string _root;

    public KnowledgeLifetimeTests()
    {
        _root = Path.Combine(_directory, "project");
        Directory.CreateDirectory(_root);
        for (var i = 0; i < 300; i++)
        {
            var folder = Path.Combine(_root, $"group{i % 10}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"file{i}.cs"), $"namespace N{i}; class C{i} {{ void M() {{ }} }}");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task DisposingDuringARefreshStopsItPromptly()
    {
        using var store = new CodaleDb(Path.Combine(_directory, "project.db"));
        var index = new ProjectKnowledgeIndex(store);
        var refresh = index.RefreshAsync(_root);

        var clock = Stopwatch.StartNew();
        index.Dispose();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"Dispose took {clock.Elapsed}.");
        await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(refresh.IsCompleted, "The refresh was still running after Dispose returned.");
    }

    [Fact]
    public void RefreshAfterDisposeDoesNothing()
    {
        using var store = new CodaleDb(Path.Combine(_directory, "project.db"));
        var index = new ProjectKnowledgeIndex(store);
        index.Dispose();

        var refresh = index.RefreshAsync(_root);

        Assert.True(refresh.IsCompletedSuccessfully);
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        using var store = new CodaleDb(Path.Combine(_directory, "project.db"));
        var index = new ProjectKnowledgeIndex(store);

        index.Dispose();
        index.Dispose();
    }

    [Fact]
    public void ARetrievalAfterDisposeAnswersWithoutThrowing()
    {
        using var store = new CodaleDb(Path.Combine(_directory, "project.db"));
        var index = new ProjectKnowledgeIndex(store);
        index.Dispose();

        var result = index.Retrieve(_root, "class C1", "", new HashSet<string>());

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ARefreshThatFinishesBeforeDisposeLeavesNoWorkBehind()
    {
        using var store = new CodaleDb(Path.Combine(_directory, "project.db"));
        var index = new ProjectKnowledgeIndex(store);
        await index.RefreshAsync(_root);

        var clock = Stopwatch.StartNew();
        index.Dispose();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Dispose took {clock.Elapsed} after a finished refresh.");
    }
}
