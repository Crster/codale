using Codale.Core.Features;

namespace Codale.Core.Tests;

public sealed class TopicShiftTests
{
    private static readonly ITextEmbedder Embedder = HashedTextEmbedder.Instance;

    [Fact]
    public void The_first_prompt_of_a_chat_is_never_new()
    {
        Assert.False(TopicShift.IsNewTopic("fix the login redirect", [], Embedder));
    }

    [Theory]
    [InlineData("how is the connector secret encrypted?")]
    [InlineData("also add an index to it")]
    [InlineData("it still fails")]
    [InlineData("yes do it")]
    [InlineData("the connectors drawer should show the status")]
    public void Follow_ups_and_shared_subjects_stay_on_topic(string prompt)
    {
        Assert.False(TopicShift.IsNewTopic(prompt, ["where does the new connector table located?"], Embedder));
    }

    [Theory]
    [InlineData("the terminal flickers when resizing the window")]
    [InlineData("fix the login redirect after oauth")]
    public void An_unrelated_subject_is_new(string prompt)
    {
        Assert.True(TopicShift.IsNewTopic(prompt, ["where does the new connector table located?", "add a status column to connectors"], Embedder));
    }

    [Theory]
    [InlineData("commit all changes")]
    [InlineData("continue in full permission mode")]
    public void Workflow_commands_have_no_subject_and_are_never_new(string prompt)
    {
        Assert.False(SearchText.HasTopic(prompt));
        Assert.False(TopicShift.IsNewTopic(prompt, ["redesign the garrison minimap"], Embedder));
    }

    [Theory]
    [InlineData("improve the garrison setup. @0b7497.png", "garrison,setup")]
    [InlineData(@"fix the menu of the hall. @""C:\Users\A B\paste\1d2115.txt""", "menu,hall")]
    [InlineData("I dont see any changes.", "")]
    public void Retrieval_terms_skip_attachments_and_request_phrasing(string prompt, string expected)
    {
        Assert.Equal(expected, string.Join(",", SearchText.RetrievalTerms(prompt)));
    }

    [Fact]
    public void A_mentioned_source_file_still_steers_retrieval()
    {
        Assert.Contains("garrison", SearchText.RetrievalTerms("tidy up @client/web/js/garrison_view.js"));
        Assert.True(SearchText.HasTopic("commit @server/game/garrison.go"));
    }
}
