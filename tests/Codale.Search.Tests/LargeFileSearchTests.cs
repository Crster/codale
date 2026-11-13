using System.Text;

namespace Codale.Search.Tests;

/// <summary>
/// Files past the full-index size (generated code, logs) must still be found by a search,
/// without their names leaking into the workspace model that drives completion.
/// </summary>
public sealed class LargeFileSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-large-").FullName;

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

    private SourceIndex Index() =>
        new(_root, new SourceIndexOptions { Watch = false, MaxAge = TimeSpan.FromHours(1), MaxFileBytes = 100_000 });

    private void WriteLarge()
    {
        var builder = new StringBuilder();
        for (var i = 0; builder.Length < 400_000; i++)
        {
            builder.Append("public sealed class Generated").Append(i).Append("\n{\n    private readonly int _value")
                .Append(i).Append(" = ").Append(i).Append(";\n}\n\n");
        }

        File.WriteAllText(Path.Combine(_root, "big.cs"), builder.ToString());
        File.WriteAllText(Path.Combine(_root, "small.cs"), "public class Small\n{\n    public int Count;\n}\n");
    }

    [Fact]
    public async Task A_file_past_the_full_index_size_is_still_found()
    {
        WriteLarge();
        using var index = Index();

        var snapshot = await index.GetSnapshotAsync();
        var big = Assert.Single(snapshot.Files, f => f.RelativePath == "big.cs");
        Assert.True(big.IsLarge);
        Assert.False(big.IsRetained);
        Assert.Empty(big.Symbols);

        var result = await new SourceExplorer(index, null) { MaxRelated = 0 }.RunAsync("private readonly");
        Assert.Contains(result.Files, f => f.RelativePath == "big.cs");

        var phrase = await new SourceExplorer(index, null) { MaxRelated = 0 }.RunAsync("\"private readonly\"");
        Assert.Contains(phrase.Files, f => f.RelativePath == "big.cs");
    }

    [Fact]
    public async Task A_large_file_stays_out_of_the_workspace_model()
    {
        WriteLarge();
        using var index = Index();
        await index.GetSnapshotAsync();

        Assert.Contains("small.cs", index.Model.Paths());
        Assert.DoesNotContain("big.cs", index.Model.Paths());
    }
}
