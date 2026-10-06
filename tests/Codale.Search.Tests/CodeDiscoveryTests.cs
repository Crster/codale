namespace Codale.Search.Tests;

/// <summary>
/// The rules discovery runs on: path hints, the file list the keyword step sees, how
/// matched lines become slices, and which paths are noise, tests or docs. The search
/// pipeline built on them is covered by <see cref="SourceExplorerTests"/>.
/// </summary>
public sealed class CodeDiscoveryTests
{
    [Fact]
    public void Model_paths_match_whole_files_in_either_slash_style()
    {
        var path = Path.Combine("src", "App", "Settings.cs");

        Assert.True(CodeDiscovery.IsExactPathHint(CodeDiscovery.NormalisePathHint("src/App/Settings.cs"), path));
        Assert.True(CodeDiscovery.IsExactPathHint(CodeDiscovery.NormalisePathHint("./src/App/Settings.cs"), path));
        Assert.True(CodeDiscovery.IsExactPathHint(CodeDiscovery.NormalisePathHint("App\\Settings.cs"), path));
        Assert.False(CodeDiscovery.IsExactPathHint(CodeDiscovery.NormalisePathHint("ings.cs"), path));
        Assert.False(CodeDiscovery.IsExactPathHint(CodeDiscovery.NormalisePathHint("src/App"), path));
    }

    [Fact]
    public void A_big_project_lists_fewer_files_per_folder_to_fit()
    {
        var files = Enumerable.Range(0, 400).Select(i => Path.Combine("src", $"Component{i:000}.cs"))
            .Append(Path.Combine("assets", "logo.png"))
            .ToList();

        var list = CodeDiscovery.BuildFileList(files, maxChars: 2000);

        Assert.True(list.Length <= 2000);
        Assert.Contains("src: Component000.cs", list);
        Assert.Contains("more", list);
        Assert.DoesNotContain("logo.png", list);
    }

    private static Dictionary<int, int> Weighted(params int[] matchLines) => matchLines.ToDictionary(n => n, _ => 1);

    [Fact]
    public void Distant_matches_become_separate_ranges_in_file_order()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();

        var ranges = CodeDiscovery.Slice(lines, Weighted(10, 12, 150));

        Assert.Equal(2, ranges.Count);
        Assert.Equal((7, 15), (ranges[0].StartLine, ranges[0].EndLine));
        Assert.Equal([10, 12], ranges[0].MatchLines);
        Assert.Equal((147, 153), (ranges[1].StartLine, ranges[1].EndLine));
    }

    [Fact]
    public void A_name_only_match_shows_the_top_of_the_file()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"line {i}").ToArray();

        var range = Assert.Single(CodeDiscovery.Slice(lines, Weighted()));

        Assert.Equal((1, 30), (range.StartLine, range.EndLine));
        Assert.Empty(range.MatchLines);
    }

    [Fact]
    public void A_stray_weak_match_is_not_shown_beside_a_strong_cluster()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();

        var ranges = CodeDiscovery.Slice(lines, new Dictionary<int, int> { [10] = 1000, [11] = 1000, [150] = 80 });

        Assert.Equal(10, Assert.Single(ranges).MatchLines[0]);
    }

    [Fact]
    public void Filler_words_are_dropped_from_the_question() =>
        Assert.Equal(["snake", "speed"], CodeDiscovery.QuestionWords("how does the project set the snake speed"));

    [Theory]
    [InlineData(@"node_modules\electron\index.js", true)]
    [InlineData(@"web\node_modules\x\a.js", true)]
    [InlineData(@"src\bin\Debug\a.dll", true)]
    [InlineData(@"src\binary.js", false)]
    [InlineData(@"main.js", false)]
    public void Dependency_and_output_folders_are_noise(string path, bool expected) =>
        Assert.Equal(expected, CodeDiscovery.IsNoisePath(path));

    [Theory]
    [InlineData(@"tests\Codale.Search.Tests\Foo.cs", true)]
    [InlineData(@"src\Codale.Search.Tests\Foo.cs", true)]
    [InlineData(@"src\RetryPolicyTests.cs", true)]
    [InlineData(@"web\player.test.ts", true)]
    [InlineData(@"README.md", true)]
    [InlineData(@"src\latest.js", false)]
    [InlineData(@"src\Inspector.cs", false)]
    [InlineData(@"src\Contest.cs", false)]
    public void Test_and_doc_paths_are_recognised(string path, bool expected) =>
        Assert.Equal(expected, CodeDiscovery.IsTestOrDoc(path));
}
