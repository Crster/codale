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
    // Mid-line: one line, no restatement of the caret's line, no indent leaked after a bracket.
    [InlineData("best)\n{", "        if (score > ", "\n        {", "best)")]
    [InlineData("j = random.Next(i + 1);", "        var j = ", "\n", "random.Next(i + 1);")]
    [InlineData("                cards[j], cards[i]);", "        (cards[i], cards[j]) = (", "\n", "cards[j], cards[i]);")]
    [InlineData("deck);", "        var score = PlayRound(", ");\n", "deck")]
    [InlineData("\n        {", "        if (score > best)", "\n", "")]
    [InlineData("Count<CURSOR>", "        var n = items.", "\n", "Count")]
    [InlineData(" + 1;", "        var n = x", "\n", " + 1;")]
    public void Mid_line_proposals_finish_only_the_caret_line(string raw, string prefix, string suffix, string expected) =>
        Assert.Equal(expected, FimText.Tidy(raw, prefix, suffix));

    [Fact]
    public void A_block_on_an_empty_line_stops_where_the_following_code_begins()
    {
        const string suffix = "\n        var score = 0;\n        return score;\n    }";
        var raw = "var deck = NewDeck(random);\n        var score = 0;\n        return score;\n    }";
        Assert.Equal("var deck = NewDeck(random);", FimText.Tidy(raw, "{\n        ", suffix));
    }

    [Fact]
    public void An_empty_line_gets_one_statement_and_the_caret_indent_once()
    {
        var raw = "\n        if (done)\n        {\n            break;\n        }";
        Assert.Equal("if (done)", FimText.Tidy(raw, "{\n        ", "\n    }"));
        var longBlock = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"        Step{i}();"));
        Assert.Equal(1, FimText.MaxBlockLines);
        Assert.Single(FimText.Tidy(longBlock, "{\n        ", "\n}").Split('\n'));
    }

    [Fact]
    public void The_cursor_document_is_raw_code_with_a_marker()
    {
        var document = FimText.CursorDocument("src\\Game.cs", "if (key is '", "' or 'l')");
        Assert.Contains("File: src/Game.cs", document);
        Assert.Contains("if (key is '<CURSOR>' or 'l')", document);
        Assert.DoesNotContain("\\u0027", document);
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