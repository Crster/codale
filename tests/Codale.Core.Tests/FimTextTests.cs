using Codale.Core.Text;

namespace Codale.Core.Tests;

public sealed class FimTextTests
{
    [Theory]
    [InlineData("foo(bar);", ");", "foo(bar")]
    [InlineData("items.Count)", ")", "items.Count")]
    [InlineData("a + b", "b);", "a + b")]
    [InlineData("return value;\nnext line", "", "return value;")]
    [InlineData("\nnext line", "", "next line")]
    [InlineData("   ", "", "")]
    [InlineData("x => x.Name", "", "x => x.Name")]
    [InlineData("u => u.Id == id, ct);", ", ct);\n}", "u => u.Id == id")]
    public void The_proposal_is_one_line_without_what_follows_the_caret(string raw, string suffix, string expected) =>
        Assert.Equal(expected, FimText.FirstLine(raw, suffix));

    [Fact]
    public void Leading_whitespace_is_dropped_when_the_caret_already_follows_whitespace()
    {
        Assert.Equal("var item = items[i];", FimText.FirstLine("    var item = items[i];", "\n}", "{\n    "));
        Assert.Equal("a + b", FimText.FirstLine(" a + b", ";", "int x = "));
        Assert.Equal(" + b", FimText.FirstLine(" + b", ";", "int x = a"));
    }

    [Fact]
    public void Only_the_rest_of_the_caret_line_counts_as_a_repeat()
    {
        Assert.Equal("Save()", FimText.FirstLine("Save()", "\n}"));
        Assert.Equal("Save(", FimText.FirstLine("Save()", ")\n}"));
    }

    [Theory]
    [InlineData("It looks like you're writing a loop", true)]
    [InlineData("// No code output needed; replace the placeholder with a statement.", true)]
    [InlineData("```csharp", true)]
    [InlineData("total += items[i];", false)]
    [InlineData("Console.WriteLine(\"Hello, World!\");", false)]
    public void Chat_replies_are_not_completions(string text, bool prose) => Assert.Equal(prose, FimText.LooksLikeProse(text));

    [Fact]
    public void Clipping_keeps_whole_lines_on_the_far_side()
    {
        const string text = "one\ntwo\nthree\nfour";
        Assert.Equal("three\nfour", FimText.Clip(text, 11, fromEnd: true));
        Assert.Equal("one\ntwo", FimText.Clip(text, 9, fromEnd: false));
        Assert.Equal(text, FimText.Clip(text, 100, fromEnd: true));
    }
}
