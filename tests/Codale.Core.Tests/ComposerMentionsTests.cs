using Codale.Core.Helper;

namespace Codale.Core.Tests;

public sealed class ComposerMentionsTests
{
    [Theory]
    // Empty composer: the mention, then a space to keep typing.
    [InlineData("", 0, 0, "@a.png", "@a.png |")]
    // Right after text and punctuation: separated from it.
    [InlineData("going to rites.", 15, 0, "@b7a3ce.png", "going to rites. @b7a3ce.png |")]
    // After a space already typed: no double space.
    [InlineData("fix the menu ", 13, 0, "@1d2115.png", "fix the menu @1d2115.png |")]
    // In the middle of a word: split on both sides.
    [InlineData("menuhall", 4, 0, "@a.png", "menu @a.png |hall")]
    // Before an existing space: that space is reused and the caret moves past it.
    [InlineData("fix the menu of the hall", 12, 0, "@a.png", "fix the menu @a.png |of the hall")]
    // Back to back: the second mention does not fuse with the first.
    [InlineData("@a.png", 6, 0, "@b.png", "@a.png @b.png |")]
    // Several at once, joined by single spaces.
    [InlineData("see", 3, 0, "@a.png @b.png", "see @a.png @b.png |")]
    public void A_mention_is_always_a_word_of_its_own(string text, int caret, int selection, string mention, string expected)
    {
        var (result, at) = ComposerMentions.Insert(text, caret, selection, mention);
        Assert.Equal(expected, result.Insert(at, "|"));
    }

    [Fact]
    public void A_selection_is_replaced_by_the_mention()
    {
        var (result, at) = ComposerMentions.Insert("look at THIS now", 8, 4, "@a.png");
        Assert.Equal("look at @a.png |now", result.Insert(at, "|"));
    }
}
