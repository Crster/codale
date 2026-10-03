using Codale.Core.Helper;

namespace Codale.Core.Tests;

public sealed class SessionHandoffTests
{
    [Fact]
    public void Short_conversation_is_kept_whole()
    {
        var text = SessionHandoff.Condense(
        [
            new HandoffEntry(HandoffRole.User, "Add a dark mode toggle"),
            new HandoffEntry(HandoffRole.Tool, "Edit(src/Settings.cs)"),
            new HandoffEntry(HandoffRole.Assistant, "Done; the toggle is in Settings."),
            new HandoffEntry(HandoffRole.Assistant, "   "),
        ]);

        Assert.Equal("User: Add a dark mode toggle\nTool: Edit(src/Settings.cs)\nAgent: Done; the toggle is in Settings.", text);
    }

    [Fact]
    public void Long_conversation_keeps_the_opening_request_and_the_latest_turns_under_the_cap()
    {
        var entries = new List<HandoffEntry> { new(HandoffRole.User, "THE ORIGINAL GOAL") };
        for (var i = 0; i < 400; i++)
        {
            entries.Add(new HandoffEntry(HandoffRole.Assistant, $"step {i} " + new string('x', 1500)));
        }

        entries.Add(new HandoffEntry(HandoffRole.User, "THE LATEST REQUEST"));

        var text = SessionHandoff.Condense(entries);

        Assert.True(text.Length <= SessionHandoff.MaxChars);
        Assert.StartsWith("User: THE ORIGINAL GOAL", text);
        Assert.EndsWith("User: THE LATEST REQUEST", text);
        Assert.Contains("earlier entries omitted", text);
        Assert.DoesNotContain("step 3 ", text);
    }

    [Fact]
    public void A_huge_entry_is_clipped_in_the_middle()
    {
        var text = SessionHandoff.Condense([new HandoffEntry(HandoffRole.Tool, "BEGIN" + new string('y', 10_000) + "END")]);

        Assert.StartsWith("Tool: BEGIN", text);
        Assert.EndsWith("END", text);
        Assert.Contains("[...]", text);
        Assert.True(text.Length < 2_200);
    }

    [Fact]
    public void First_message_carries_the_note() =>
        Assert.Contains("## Goal", SessionHandoff.FirstMessage("## Goal\nShip it\n"));
}
