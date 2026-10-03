using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class SessionTitlesTests
{
    [Theory]
    [InlineData("can you make the chat session more smart. when user chat. before sending it should be normalize",
        "Make the chat session more smart")]
    [InlineData("please fix the login redirect", "Fix the login redirect")]
    [InlineData("hey, could you help me refactor the git panel into view models", "Refactor the git panel into view")]
    [InlineData("why does the terminal flicker?", "Why does the terminal flicker")]
    [InlineData("fix it", "Fix it")]
    public void A_prompt_becomes_its_gist(string prompt, string expected)
    {
        Assert.Equal(expected, SessionTitles.FromPrompt(prompt));
    }

    [Fact]
    public void Titles_stay_short()
    {
        var title = SessionTitles.FromPrompt(
            "implement the complete synchronisation subsystem for distributed configuration management across regions");

        Assert.True(title.Length <= 36, title);
    }

    [Fact]
    public void Code_and_later_lines_are_left_out()
    {
        Assert.Equal("Explain this", SessionTitles.FromPrompt("explain this\n```cs\nvar x = 1;\n```"));
    }

    [Theory]
    [InlineData("Fix login redirect<|im_end|>", "Fix login redirect")]
    [InlineData("Fix login redirect <im|end>", "Fix login redirect")]
    [InlineData("\"Git panel refactor\"</s>", "Git panel refactor")]
    [InlineData("Git panel<|im_end|>\nextra words", "Git panel")]
    [InlineData("Plain title.", "Plain title")]
    public void Model_titles_lose_leaked_template_tokens(string raw, string expected) =>
        Assert.Equal(expected, SessionTitles.CleanModelTitle(raw));

    [Fact]
    public void A_prompt_of_only_filler_is_clipped_as_is()
    {
        Assert.Equal("please", SessionTitles.FromPrompt("please"));
    }
}
