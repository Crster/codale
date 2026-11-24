using Codale.Core.Projects;
using Codale.Db;
using Codale.Search.Engine;

namespace Codale.Search.Tests;

public sealed class KnowledgeSourceSeamTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codale-source-seam-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class RecordingSources : ISourceIndexes
    {
        public List<string> Roots { get; } = [];

        public SourceIndex For(string root)
        {
            Roots.Add(root);
            return SourceIndex.For(root);
        }
    }

    [Fact]
    public async Task RefreshReadsTheSourceIndexThroughTheInjectedProvider()
    {
        var root = Path.Combine(_directory, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.cs"), "class A {}");
        using var store = new CodaleDatabase(Path.Combine(_directory, "project.db"));
        var sources = new RecordingSources();
        using var index = new ProjectKnowledgeIndex(store, sources);

        await index.RefreshAsync(root);

        Assert.Contains(root, sources.Roots);
    }

    [Fact]
    public void TheDefaultProviderIsTheSharedCache()
    {
        var root = Path.Combine(_directory, "shared");
        Directory.CreateDirectory(root);

        Assert.Same(SourceIndex.For(root), SharedSourceIndexes.Instance.For(root));
    }

    [Fact]
    public async Task TurnEndAfterWatcherTimeoutReusesUnchangedSourceAndFindsMissedEdits()
    {
        var root = ProjectPaths.Normalize(Directory.CreateDirectory(Path.Combine(_directory, "project")).FullName);
        File.WriteAllText(Path.Combine(root, "original.cs"), "class Original {}");
        using var source = new SourceIndex(root, new() { Watch = false, MaxAge = TimeSpan.FromHours(1) });
        using var store = new CodaleDatabase(Path.Combine(_directory, "project.db"));
        var clock = new RefreshTime();
        using var index = new ProjectKnowledgeIndex(store, new FixedSource(source)) { RefreshClock = clock };
        await index.RefreshAsync(root);
        var first = source.Current!;

        index.BeginAgentTurn(root);
        clock.Advance(TimeSpan.FromSeconds(31));
        index.EndAgentTurn(root);
        await index.RefreshAsync(root);

        var checkedSnapshot = source.Current!;
        Assert.NotSame(first, checkedSnapshot);
        Assert.Equal(first.BuiltAt, checkedSnapshot.BuiltAt);
        Assert.Equal(0, checkedSnapshot.FilesRead);
        Assert.Same(first.Find("original.cs"), checkedSnapshot.Find("original.cs"));

        index.BeginAgentTurn(root);
        File.WriteAllText(Path.Combine(root, "new.cs"), "class MissedWatcherEdit {}");
        clock.Advance(TimeSpan.FromSeconds(31));
        index.EndAgentTurn(root);
        await index.RefreshAsync(root);

        Assert.Equal(1, source.Current!.FilesRead);
        Assert.Same(checkedSnapshot.Find("original.cs"), source.Current.Find("original.cs"));
        Assert.Contains(index.Search(root, "MissedWatcherEdit"), chunk => chunk.Path == "new.cs");
    }

    private sealed class FixedSource(SourceIndex source) : ISourceIndexes
    {
        public SourceIndex For(string root) => source;
    }

    private sealed class RefreshTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
    }
}
