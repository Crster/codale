using Codale.Agents.Claude;
using Codale.Agents.Codex;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public class AgentPermissionModeTests
{
    [Theory]
    [InlineData("manual")]
    [InlineData("acceptEdits")]
    [InlineData("auto")]
    [InlineData("bypassPermissions")]
    [InlineData("dontAsk")]
    [InlineData("plan")]
    [InlineData("unknown")]
    [InlineData("")]
    public void BothProvidersUseTheSharedVocabulary(string mode)
    {
        var expected = AgentPermissionMode.Normalize(mode);
        Assert.Equal(expected, ClaudeSessionOptions.ClampPermissionMode(mode));
        Assert.Equal(expected, CodexSessionOptions.ClampPermissionMode(mode));
    }

    [Fact]
    public void UnknownModesFallBackToAutomatic()
    {
        Assert.Equal("auto", AgentPermissionMode.Normalize(null));
        Assert.Equal("auto", AgentPermissionMode.Normalize("unknown"));
    }
}
