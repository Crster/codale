using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class McpToolPolicyTests
{
    [Theory]
    [InlineData("allow", AgentToolMode.Allow)]
    [InlineData("1", AgentToolMode.Allow)]
    [InlineData("cli", AgentToolMode.CliDefault)]
    [InlineData("block", AgentToolMode.Block)]
    [InlineData("0", AgentToolMode.Block)]
    public void StoredValuesParseToTheirMode(string stored, AgentToolMode expected) =>
        Assert.Equal(expected, McpToolPolicy.ParseMode(stored, AgentToolMode.Block));

    [Fact]
    public void AnUnknownOrMissingValueFallsBack()
    {
        Assert.Equal(AgentToolMode.CliDefault, McpToolPolicy.ParseMode(null, AgentToolMode.CliDefault));
        Assert.Equal(AgentToolMode.Allow, McpToolPolicy.ParseMode("garbage", AgentToolMode.Allow));
    }

    [Theory]
    [InlineData(AgentToolMode.Allow)]
    [InlineData(AgentToolMode.CliDefault)]
    [InlineData(AgentToolMode.Block)]
    public void FormattedModesParseBack(AgentToolMode mode) =>
        Assert.Equal(mode, McpToolPolicy.ParseMode(McpToolPolicy.FormatMode(mode), AgentToolMode.Block));

    [Fact]
    public void CliDefaultTurnsTheCliToolOn() =>
        Assert.True(McpToolPolicy.Native(AgentToolMode.CliDefault, codaleAvailable: false));

    [Fact]
    public void BlockTurnsTheCliToolOff() =>
        Assert.False(McpToolPolicy.Native(AgentToolMode.Block, codaleAvailable: true));

    [Fact]
    public void AllowTurnsTheCliToolOffSoTheAgentHasOneBrowser() =>
        Assert.False(McpToolPolicy.Native(AgentToolMode.Allow, codaleAvailable: true));

    [Fact]
    public void AllowWithoutCodalesServerLeavesTheCliAlone() =>
        Assert.Null(McpToolPolicy.Native(AgentToolMode.Allow, codaleAvailable: false));

    [Fact]
    public void ClaudeChromeFollowsTheBrowserMode() =>
        Assert.Equal(McpToolPolicy.Native(AgentToolMode.Allow, true), McpToolPolicy.ClaudeChrome(AgentToolMode.Allow, true));

    [Fact]
    public void CodexBrowserFeaturesAreSetTogetherWithTheCodexComputerFeature()
    {
        var features = McpToolPolicy.CodexFeatures(AgentToolMode.Allow, browserAvailable: true, AgentToolMode.CliDefault, computerAvailable: true);

        foreach (var name in McpToolPolicy.CodexBrowserFeatures)
        {
            Assert.False(features[name]);
        }

        Assert.True(features[McpToolPolicy.CodexComputerFeature]);
    }

    [Fact]
    public void CodexLeavesAFeatureUnsetWhenItsCodaleServerIsMissing()
    {
        var features = McpToolPolicy.CodexFeatures(AgentToolMode.Allow, browserAvailable: false, AgentToolMode.Allow, computerAvailable: false);

        Assert.Empty(features);
    }

    [Fact]
    public void ClaudeDisallowsTheBlockedToolsOnly()
    {
        Assert.Equal("mcp__claude-in-chrome,mcp__computer-use", McpToolPolicy.ClaudeDisallowedTools(AgentToolMode.Block, AgentToolMode.Block));
        Assert.Equal("mcp__computer-use", McpToolPolicy.ClaudeDisallowedTools(AgentToolMode.CliDefault, AgentToolMode.Block));
        Assert.Null(McpToolPolicy.ClaudeDisallowedTools(AgentToolMode.CliDefault, AgentToolMode.Allow));
    }

    [Fact]
    public void TheModesKeyChangesWhenAnyModeChanges()
    {
        Assert.Equal("allow|block|cli", McpToolPolicy.ModesKey(AgentToolMode.Allow, AgentToolMode.Block, AgentToolMode.CliDefault));
        Assert.NotEqual(
            McpToolPolicy.ModesKey(AgentToolMode.Allow, AgentToolMode.Block, AgentToolMode.CliDefault),
            McpToolPolicy.ModesKey(AgentToolMode.Allow, AgentToolMode.Allow, AgentToolMode.CliDefault));
    }
}
