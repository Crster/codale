using Codale.Core.Text;

namespace Codale.Core.Tests;

public sealed class PromptWordsTests
{
    [Theory]
    [InlineData("build the proj", 14, "proj")]
    [InlineData("Compile", 7, "Compile")]
    [InlineData("call Foo.Ba", 11, "Ba")]
    public void The_word_the_caret_ends_is_found(string text, int caret, string expected)
    {
        Assert.Equal(expected, PromptWords.Before(text, caret)?.Text);
    }

    [Theory]
    [InlineData("build the proj", 12)] // inside a word
    [InlineData("build the ", 10)] // after a space
    [InlineData("see @src/Cha", 12)] // a mention
    [InlineData("/comp", 5)] // a command
    [InlineData("open https://exam", 17)] // a link
    [InlineData("in src/Codale.App", 17)] // a path
    [InlineData("take 12", 7)] // a number
    public void Mentions_commands_paths_and_mid_word_carets_are_left_alone(string text, int caret)
    {
        Assert.Null(PromptWords.Before(text, caret));
    }

    [Fact]
    public void The_previous_word_skips_spaces_and_dots()
    {
        Assert.Equal("dotnet", PromptWords.Previous("with dotnet bui", 12));
        Assert.Equal("Foo", PromptWords.Previous("Foo.Ba", 4));
        Assert.Null(PromptWords.Previous("bui", 0));
    }

    [Theory]
    [InlineData("comp", "compile", "compile")]
    [InlineData("Comp", "compile", "Compile")]
    [InlineData("COMP", "compile", "COMPILE")]
    [InlineData("sour", "SourceIndex", "SourceIndex")]
    public void A_plain_suggestion_follows_the_typed_casing(string typed, string suggestion, string expected)
    {
        Assert.Equal(expected, PromptWords.MatchCase(typed, suggestion));
    }
}
