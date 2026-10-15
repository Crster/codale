using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class SessionBriefingTests
{
    [Fact]
    public void The_context_block_is_stripped_back_to_the_users_words()
    {
        var sent = new UserTurn("now add tests") { ContextPrefix = SessionBriefing.ContextBlock("## Goal\nShip it") }.ProviderText;

        Assert.Contains("Ship it", sent);
        Assert.Equal("now add tests", UserTurn.StripHostInstructions(sent));
    }

    [Fact]
    public void The_context_block_and_the_ask_prefix_strip_together()
    {
        var sent = new UserTurn("why?") { ContextPrefix = SessionBriefing.ContextBlock("brief"), AskOnly = true }.ProviderText;

        Assert.Equal("why?", UserTurn.StripHostInstructions(sent));
    }

    [Fact]
    public void Text_without_a_context_block_is_untouched()
    {
        Assert.Equal("plain", SessionBriefing.StripContext("plain"));
    }
}