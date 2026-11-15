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
        using var store = new CodaleDb(Path.Combine(_directory, "project.db"));
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
}