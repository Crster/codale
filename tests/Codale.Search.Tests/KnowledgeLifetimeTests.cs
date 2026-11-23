using System.Diagnostics;

using Codale.Db;
using Codale.Search.Engine;

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
        using var store = new CodaleDatabase(Path.Combine(_directory, "project.db"));
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
        using var store = new CodaleDatabase(Path.Combine(_directory, "project.db"));
        var index = new ProjectKnowledgeIndex(store);
        index.Dispose();

        var refresh = index.RefreshAsync(_root);

        Assert.True(refresh.IsCompletedSuccessfully);
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        using var store = new CodaleDatabase(Path.Combine(_directory, "project.db"));
        var index = new ProjectKnowledgeIndex(store);

        index.Dispose();
        index.Dispose();
    }

    [Fact]
    public void ASearchAfterDisposeAnswersWithoutThrowing()
    {
        using var store = new CodaleDatabase(Path.Combine(_directory, "project.db"));
        var index = new ProjectKnowledgeIndex(store);
        index.Dispose();

        Assert.NotNull(index.Search(_root, "class C1"));
    }

    [Fact]
    public async Task IndexingReportsBusyThenIdleEvenWhenSourceFilesAreReused()
    {
        using var store = new CodaleDatabase(Path.Combine(_directory, "busy.db"));
        using var index = new ProjectKnowledgeIndex(store);
        var states = new List<bool>();
        index.IndexingChanged += () => states.Add(index.IsIndexing);
        await index.RefreshAsync(_root);
        var source = SourceIndex.For(_root);
        var unchanged = source.Current!.Files[0];
        source.Invalidate();
        await index.RefreshAsync(_root);
        Assert.Same(unchanged, source.Current!.Files[0]);
        Assert.Equal(new[] { true, false, true, false }, states);
    }

    [Fact]
    public async Task ARefreshThatFinishesBeforeDisposeLeavesNoWorkBehind()
    {
        using var store = new CodaleDatabase(Path.Combine(_directory, "project.db"));
        var index = new ProjectKnowledgeIndex(store);
        await index.RefreshAsync(_root);

        var clock = Stopwatch.StartNew();
        index.Dispose();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Dispose took {clock.Elapsed} after a finished refresh.");
    }
}
