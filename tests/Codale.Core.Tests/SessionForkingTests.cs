using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class SessionForkingTests
{
    private static AssistantMessageCompleted Reply(string text) =>
        new() { MessageId = "m", Model = "x", Text = text };

    [Fact]
    public void The_source_keeps_the_conversation_and_lists_files_changed()
    {
        var events = new AgentEvent[]
        {
            new UserMessageRecorded("fix the login redirect"),
            Reply("Found it in AuthController."),
            new ToolCallCompleted
            {
                ToolUseId = "t",
                FileChange = new FileChange { Kind = FileChangeKind.Update, FilePath = @"C:\proj\src\AuthController.cs" },
            },
        };

        var source = SessionForking.BuildSource("Login fix", events, @"C:\proj");

        Assert.Contains("# Login fix", source);
        Assert.Contains("### User\nfix the login redirect", source);
        Assert.Contains("### Assistant\nFound it in AuthController.", source);
        Assert.Contains("- `src\\AuthController.cs`", source);
    }

    [Fact]
    public void Subagent_replies_are_left_out()
    {
        var events = new AgentEvent[]
        {
            new UserMessageRecorded("go"),
            new AssistantMessageCompleted { MessageId = "m", Model = "x", Text = "sub chatter", ParentToolUseId = "tool" },
        };

        Assert.DoesNotContain("sub chatter", SessionForking.BuildSource("t", events, @"C:\proj"));
    }

    [Fact]
    public void Over_budget_keeps_the_opening_and_the_latest_turns()
    {
        var events = new List<AgentEvent> { new UserMessageRecorded("THE GOAL") };
        for (var i = 0; i < 40; i++)
        {
            events.Add(Reply($"reply {i} " + new string('x', 400)));
        }

        events.Add(new UserMessageRecorded("LATEST ASK"));

        var source = SessionForking.BuildSource("t", events, @"C:\proj", budget: 3_000);

        Assert.Contains("THE GOAL", source);
        Assert.Contains("LATEST ASK", source);
        Assert.Contains("omitted", source);
        Assert.DoesNotContain("reply 10 ", source);
        Assert.True(source.Length < 4_500);
    }

    [Fact]
    public void A_long_message_is_clipped()
    {
        var events = new AgentEvent[] { new UserMessageRecorded(new string('a', 5_000)) };

        Assert.True(SessionForking.BuildSource("t", events, @"C:\proj").Length < 2_000);
    }

    [Fact]
    public void The_context_block_is_stripped_back_to_the_users_words()
    {
        var sent = new UserTurn("now add tests") { ContextPrefix = SessionForking.ContextBlock("## Goal\nShip it") }.ProviderText;

        Assert.Contains("Ship it", sent);
        Assert.Equal("now add tests", UserTurn.StripHostInstructions(sent));
    }

    [Fact]
    public void The_context_block_and_the_ask_prefix_strip_together()
    {
        var sent = new UserTurn("why?") { ContextPrefix = SessionForking.ContextBlock("brief"), AskOnly = true }.ProviderText;

        Assert.Equal("why?", UserTurn.StripHostInstructions(sent));
    }

    [Fact]
    public void Text_without_a_context_block_is_untouched()
    {
        Assert.Equal("plain", SessionForking.StripContext("plain"));
    }
}
