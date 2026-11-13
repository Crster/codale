using Codale.Core.Text;

namespace Codale.Core.Tests;

public sealed class PromptPillsTests
{
    [Theory]
    [InlineData("look at @shot.png please", "@shot.png", PromptPillKind.Image, "shot.png")]
    [InlineData("@spec.pdf", "@spec.pdf", PromptPillKind.Pdf, "spec.pdf")]
    [InlineData("see @notes.md, then", "@notes.md", PromptPillKind.Text, "notes.md")]
    [InlineData("read @\"C:\\My Files\\a b.txt\" ok", "@\"C:\\My Files\\a b.txt\"", PromptPillKind.Text, "a b.txt")]
    [InlineData("(@log.txt)", "@log.txt", PromptPillKind.Text, "log.txt")]
    public void An_attachable_file_mention_is_a_pill(string text, string pillText, PromptPillKind kind, string title)
    {
        var pill = Assert.Single(PromptPills.Find(text));
        Assert.Equal(pillText, pill.Text);
        Assert.Equal(pillText, text.Substring(pill.Start, pill.Length));
        Assert.Equal(kind, pill.Kind);
        Assert.Equal(title, pill.Title);
    }

    [Theory]
    [InlineData("ping @john")]
    [InlineData("@media (max-width: 400px)")]
    [InlineData("@app.route('/')")]
    [InlineData("mail me at me@site.png")]
    [InlineData("just @")]
    [InlineData("@\"unterminated.png")]
    [InlineData("in @src/Views/ now")]
    [InlineData("run @tools/build")]
    [InlineData("see https://github.com/owner/repo")]
    public void Anything_but_an_attachable_file_mention_stays_text(string text) =>
        Assert.Empty(PromptPills.Find(text));

    [Fact]
    public void Several_pills_come_back_in_order()
    {
        var pills = PromptPills.Find("@a.png @b.pdf and @c.txt");
        Assert.Equal(["@a.png", "@b.pdf", "@c.txt"], pills.Select(p => p.Text));
    }

    [Theory]
    [InlineData("  @a.png  ", true)]
    [InlineData("see @a.png", false)]
    public void Whole_is_set_only_when_the_text_is_one_pill(string text, bool whole) =>
        Assert.Equal(whole, PromptPills.Whole(text) is not null);

    [Theory]
    [InlineData("a.png", "@a.png")]
    [InlineData("my shot.png", "@\"my shot.png\"")]
    public void Mention_quotes_paths_with_spaces(string path, string mention) =>
        Assert.Equal(mention, PromptPills.Mention(path));
}
