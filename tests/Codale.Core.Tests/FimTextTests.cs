using Codale.Core.Background;

namespace Codale.Core.Tests;

public sealed class FimTextTests
{
    [Theory]
    [InlineData("foo(bar);", ");", "foo(bar")]
    [InlineData("items.Count)", ")", "items.Count")]
    [InlineData("a + b", "b);", "a + b")]
    [InlineData("return value;\nnext line", "", "return value;")]
    [InlineData("\nnext line", "", "")]
    [InlineData("   ", "", "")]
    [InlineData("x => x.Name", "", "x => x.Name")]
    public void The_proposal_is_one_line_without_what_follows_the_caret(string raw, string suffix, string expected) =>
        Assert.Equal(expected, FimText.FirstLine(raw, suffix));

    [Fact]
    public void Only_the_rest_of_the_caret_line_counts_as_a_repeat()
    {
        Assert.Equal("Save()", FimText.FirstLine("Save()", "\n}"));
        Assert.Equal("Save(", FimText.FirstLine("Save()", ")\n}"));
    }

    [Fact]
    public void Clipping_keeps_whole_lines_on_the_far_side()
    {
        const string text = "one\ntwo\nthree\nfour";
        Assert.Equal("three\nfour", FimText.Clip(text, 11, fromEnd: true));
        Assert.Equal("one\ntwo", FimText.Clip(text, 9, fromEnd: false));
        Assert.Equal(text, FimText.Clip(text, 100, fromEnd: true));
    }

    [Fact]
    public void The_shell_prefix_reads_as_a_script_ending_in_what_is_typed()
    {
        var prefix = FimText.ShellPrefix("pwsh", @"C:\repo", ["git status", "dotnet build"], ["src", "README.md"], "git ch");
        Assert.Equal("# shell: pwsh\n# cwd: C:\\repo\n# files here: src, README.md\ngit status\ndotnet build\ngit ch", prefix);
        Assert.StartsWith(":: shell: cmd", FimText.ShellPrefix("cmd", "C:\\", [], [], "dir"));
        Assert.EndsWith("\ndir", FimText.ShellPrefix("cmd", "C:\\", [], [], "dir"));
    }
}
