using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.Core.Tests;

public sealed class FeatureCatchUpTests
{
    private static readonly HandoffEntry[] TwoChats =
    [
        new(HandoffRole.User, "add retries", "a"),
        new(HandoffRole.User, "rename the key", "b"),
        new(HandoffRole.Assistant, "renamed it", "b"),
        new(HandoffRole.TurnEnd, "success", "b"),
        new(HandoffRole.Tool, "Edit(src/Uploader.cs)", "a"),
    ];

    [Fact]
    public void Several_chats_condense_to_one_block_each_with_their_state()
    {
        var text = SessionHandoff.Condense(TwoChats, runningChats: new HashSet<string> { "a" });

        var first = text.IndexOf("### Conversation 1 (still running", StringComparison.Ordinal);
        var second = text.IndexOf("### Conversation 2 (finished)", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, text);
        Assert.True(text.IndexOf("Tool: Edit(src/Uploader.cs)", StringComparison.Ordinal) < second, "chat a's tool call stays in its own block");
        Assert.DoesNotContain("success", text);
    }

    [Fact]
    public void A_turn_that_never_ended_reads_as_interrupted_once_its_chat_is_gone()
    {
        Assert.Equal(ConversationState.Interrupted, SessionHandoff.StateOf([.. TwoChats.Where(e => e.Chat == "a")], "a", null));
        Assert.Equal(ConversationState.Finished, SessionHandoff.StateOf([.. TwoChats.Where(e => e.Chat == "b")], "b", null));
    }

    [Fact]
    public void One_finished_conversation_reads_as_before()
    {
        HandoffEntry[] legacy = [new(HandoffRole.User, "fix it"), new(HandoffRole.Assistant, "fixed")];

        Assert.Equal("User: fix it\nAgent: fixed", SessionHandoff.Condense(legacy));
    }

    [Fact]
    public void Only_turn_ends_is_no_news()
    {
        Assert.False(FeatureCatchUp.HasWork([new HandoffEntry(HandoffRole.TurnEnd, "success", "b")]));
        Assert.True(FeatureCatchUp.HasWork(TwoChats));
    }

    [Fact]
    public void The_note_lists_the_other_chats_open_now()
    {
        var note = FeatureCatchUp.BuildNote("- renamed `uploads.retry`", [new FeatureChat("b", "f", "Settings rename", true, "rename the key")]);

        Assert.StartsWith("## Meanwhile, in other chats on this feature", note);
        Assert.Contains("\"Settings rename\" (working now): last asked \"rename the key\"", note);
    }

    [Fact]
    public void The_brief_input_names_the_other_chats_and_marks_running_work()
    {
        var input = FeatureBrief.BuildInput(
            "Uploads", "", TwoChats, [], [], "make retries back off", [new FeatureChat("a", "f", "Retries", true, "add retries")]);

        Assert.Contains("<other_chats>", input);
        Assert.Contains("still running", input);
        Assert.EndsWith("</request>", input);
    }
}
