using Codale.Core.Text;

namespace Codale.Core.Tests;

public class LinkFinderTests
{
    [Theory]
    [InlineData("TerminalView.xaml:1-7,30-40", "TerminalView.xaml", 1, 7, 30, 40)]
    [InlineData("CodeEditor.xaml:1–11,26–43", "CodeEditor.xaml", 1, 11, 26, 43)]
    [InlineData("src/CodeEditor.xaml:1-11, 26-43", "src/CodeEditor.xaml", 1, 11, 26, 43)]
    [InlineData("X:/src/CodeEditor.xaml:1—11,26—43", "X:/src/CodeEditor.xaml", 1, 11, 26, 43)]
    public void FindsCompleteDisjointRanges(string input, string path, int start, int end, int nextStart, int nextEnd)
    {
        var link = Assert.Single(LinkFinder.Find($"See {input}."));
        Assert.Equal(4, link.Index);
        Assert.Equal(input.Length, link.Length);
        Assert.Equal(input, link.Text);
        Assert.Equal(path, link.Path);
        Assert.Equal(start, link.Line);
        Assert.Equal(end, link.EndLine);
        Assert.Equal(new[] { new FileLineRange(start, end), new FileLineRange(nextStart, nextEnd) }, link.LineRanges);
        Assert.Equal(link.LineRanges, LinkFinder.AsFileReference(input)!.LineRanges);
        Assert.NotNull(LinkFinder.At($"See {input}.", 4 + input.Length - 1));
    }

    [Theory]
    [InlineData("CodeEditor.xaml:1–11", 1, 11)]
    [InlineData("src/CodeEditor.xaml:26—43", 26, 43)]
    public void FindsUnicodeLineRanges(string input, int start, int end)
    {
        var link = LinkFinder.AsFileReference(input);
        Assert.NotNull(link);
        Assert.Equal(start, link.Line);
        Assert.Equal(end, link.EndLine);
    }

    [Fact]
    public void CodeSpanAllowsCustomExtensionAndSeveralRanges()
    {
        var link = LinkFinder.AsFileReference("shader.frag:1-7,30,40-50");
        Assert.NotNull(link);
        Assert.Equal("shader.frag", link.Path);
        Assert.Equal(new[] { new FileLineRange(1, 7), new FileLineRange(30, 30), new FileLineRange(40, 50) }, link.LineRanges);
        Assert.Null(LinkFinder.AsFileReference("shader.frag:1-7,30-40 extra"));
    }

    [Theory]
    [InlineData("X:/test.js:10-5", "X:/test.js", 10, 5, 0, 0)]
    [InlineData("X:/test.js:10:5", "X:/test.js", 10, 5, 0, 0)]
    [InlineData("X:/test.js:10-25", "X:/test.js", 10, 0, 25, 0)]
    [InlineData("X:/test.js:10", "X:/test.js", 10, 0, 0, 0)]
    [InlineData("X:/test.js:10:5-15", "X:/test.js", 10, 5, 0, 15)]
    [InlineData("X:/test.js:10:5-12:20", "X:/test.js", 10, 5, 12, 20)]
    [InlineData("X:/test.js(10,5)", "X:/test.js", 10, 5, 0, 0)]
    [InlineData("X:/test.js(10, 5)", "X:/test.js", 10, 5, 0, 0)]
    [InlineData("X:/test.js(10-25)", "X:/test.js", 10, 0, 25, 0)]
    [InlineData("X:/test.js(10-5)", "X:/test.js", 10, 5, 0, 0)]
    [InlineData("X:/test.js(10,5,12,20)", "X:/test.js", 10, 5, 12, 20)]
    [InlineData("./test.js:10-5", "./test.js", 10, 5, 0, 0)]
    [InlineData("../src/test.js:10-25", "../src/test.js", 10, 0, 25, 0)]
    [InlineData("src/test.js:10-5", "src/test.js", 10, 5, 0, 0)]
    [InlineData("src/test.js:10-25", "src/test.js", 10, 0, 25, 0)]
    public void FindsFilePathsWithLocations(string input, string expectedPath, int expectedLine, int expectedCol, int expectedEndLine, int expectedEndCol)
    {
        var links = LinkFinder.Find(input);
        Assert.Single(links);
        var match = links[0];
        Assert.Equal(expectedPath, match.Path);
        Assert.Equal(expectedLine, match.Line);
        Assert.Equal(expectedCol, match.Column);
        Assert.Equal(expectedEndLine, match.EndLine);
        Assert.Equal(expectedEndCol, match.EndColumn);
    }

    [Theory]
    [InlineData("test.cs:10-5", "test.cs", 10, 5, 0, 0)]
    [InlineData("test.cs:10-25", "test.cs", 10, 0, 25, 0)]
    [InlineData("test.cs:10:5", "test.cs", 10, 5, 0, 0)]
    [InlineData("test.cs 10:5", "test.cs", 10, 5, 0, 0)]
    [InlineData("test.cs 10-25", "test.cs", 10, 0, 25, 0)]
    public void FindsProseFileWithLocations(string input, string expectedPath, int expectedLine, int expectedCol, int expectedEndLine, int expectedEndCol)
    {
        var links = LinkFinder.Find(input);
        Assert.Single(links);
        var match = links[0];
        Assert.Equal(expectedPath, match.Path);
        Assert.Equal(expectedLine, match.Line);
        Assert.Equal(expectedCol, match.Column);
        Assert.Equal(expectedEndLine, match.EndLine);
        Assert.Equal(expectedEndCol, match.EndColumn);
    }

    [Theory]
    [InlineData("test.js:10-5", "test.js", 10, 5, 0, 0)]
    [InlineData("test.js:10-25", "test.js", 10, 0, 25, 0)]
    [InlineData("test.js:10:5", "test.js", 10, 5, 0, 0)]
    [InlineData("X:/test.js:10-5", "X:/test.js", 10, 5, 0, 0)]
    [InlineData("src/foo.cs:120-145", "src/foo.cs", 120, 0, 145, 0)]
    public void AsFileReferenceParsesLocation(string input, string expectedPath, int expectedLine, int expectedCol, int expectedEndLine, int expectedEndCol)
    {
        var match = LinkFinder.AsFileReference(input);
        Assert.NotNull(match);
        Assert.Equal(expectedPath, match.Path);
        Assert.Equal(expectedLine, match.Line);
        Assert.Equal(expectedCol, match.Column);
        Assert.Equal(expectedEndLine, match.EndLine);
        Assert.Equal(expectedEndCol, match.EndColumn);
    }

    [Theory]
    [InlineData("L10", 10, 0, 0, 0)]
    [InlineData("L10-L25", 10, 0, 25, 0)]
    [InlineData("L10-25", 10, 0, 25, 0)]
    [InlineData("L10C5", 10, 5, 0, 0)]
    [InlineData("L10:5", 10, 5, 0, 0)]
    [InlineData("L10C5-L12C8", 10, 5, 12, 8)]
    [InlineData("10", 10, 0, 0, 0)]
    [InlineData("10:5", 10, 5, 0, 0)]
    [InlineData("10-25", 10, 0, 25, 0)]
    [InlineData("10-5", 10, 5, 0, 0)]
    public void ParseHashLocationParsesCorrectly(string fragment, int expectedLine, int expectedCol, int expectedEndLine, int expectedEndCol)
    {
        var (line, col, endLine, endCol) = LinkFinder.ParseHashLocation(fragment);
        Assert.Equal(expectedLine, line);
        Assert.Equal(expectedCol, col);
        Assert.Equal(expectedEndLine, endLine);
        Assert.Equal(expectedEndCol, endCol);
    }
}
