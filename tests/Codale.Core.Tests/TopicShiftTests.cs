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
}
