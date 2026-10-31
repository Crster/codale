using Codale.Core.Text;

namespace Codale.Core.Tests;

public sealed class ComposerCompletionTests
{
    [Theory]
    [InlineData("fix this /rev", 13, '/', "rev")]
    [InlineData("fix @src later", 8, '@', "src")]
    [InlineData("/rev fix this", 4, '/', "rev")]
    [InlineData("line\n@", 6, '@', "")]
    public void Finds_the_token_at_the_caret(string text, int caret, char trigger, string query)
    {
        var token = ComposerCompletion.FindToken(text, caret);
        Assert.NotNull(token);
        Assert.Equal(trigger, token.Value.Trigger);
        Assert.Equal(query, token.Value.Query);
    }

    [Theory]
    [InlineData("email@example.com")]
    [InlineData("https://example.com")]
    [InlineData("fix @src later")]
    public void Ignores_non_trigger_tokens(string text) =>
        Assert.Null(ComposerCompletion.FindToken(text, text.Length));

    [Fact]
    public void Command_button_preserves_prompt_and_places_caret_before_it()
    {
        var edit = ComposerCompletion.InsertTrigger("fix this", 8, 0, '/');
        Assert.Equal(new ComposerCompletion.Edit("/ fix this", 1), edit);
        Assert.Equal(new ComposerCompletion.Edit("/review fix this", 16),
            ComposerCompletion.Apply(edit.Text, edit.Caret, "/review", true));
    }

    [Fact]
    public void Typed_command_moves_to_front_and_preserves_prompt()
    {
        Assert.Equal("/review fix this", ComposerCompletion.Apply("fix this /rev", 13, "/review", true).Text);
    }

    [Fact]
    public void Reference_button_and_completion_preserve_suffix_and_quote_spaces()
    {
        var edit = ComposerCompletion.InsertTrigger("fix this", 4, 0, '@');
        Assert.Equal(new ComposerCompletion.Edit("fix @ this", 5), edit);
        Assert.Equal(new ComposerCompletion.Edit("fix @\"src/my file.cs\" this", 22),
            ComposerCompletion.Apply(edit.Text, edit.Caret, "src/my file.cs", false));
    }

    [Fact]
    public void Completion_replaces_the_whole_token_in_the_middle()
    {
        Assert.Equal("fix @src/test.cs later", ComposerCompletion.Apply("fix @src/old later", 8, "src/test.cs", false).Text);
    }
}
