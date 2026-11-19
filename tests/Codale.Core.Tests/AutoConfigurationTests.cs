using Codale.Core.Helper;
using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class AutoConfigurationTests
{
    [Theory]
    [InlineData("set this chat tier to Lite", "lite")]
    [InlineData("Please switch model tier to Plus.", "plus")]
    [InlineData("use pro model for this chat", "pro")]
    [InlineData("why should I use pro model?", null)]
    [InlineData("set tier to lite and fix the parser", null)]
    [InlineData("set the project theme to dark", null)]
    public void Only_complete_unambiguous_host_tier_commands_change_a_setting(string text, string? tier) =>
        Assert.Equal(tier, AutoConfiguration.Tier(text));

    [Fact]
    public void Recalled_memory_and_conversation_are_historical_and_strip_from_provider_history()
    {
        var block = SessionBriefing.ConversationBlock("I prefer compact spacing");
        Assert.Contains("Historical", block);
        Assert.Contains("current user request takes precedence", block);
        Assert.DoesNotContain("Edit straight away", block);
        Assert.Equal("why?", UserTurn.StripHostInstructions(new UserTurn("why?") { AskOnly = true, ContextPrefix = block }.ProviderText));
    }
}
