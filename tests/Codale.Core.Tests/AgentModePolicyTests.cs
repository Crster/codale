using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class AgentModePolicyTests
{
    [Theory]
    [InlineData("ask", "manual")]
    [InlineData("automatic", "manual")]
    [InlineData("auto", "bypassPermissions")]
    [InlineData("dontAsk", "bypassPermissions")]
    [InlineData("plan", "plan")]
    [InlineData("manual", "manual")]
    public void CliModePreservesHostEnforcement(string host, string cli) => Assert.Equal(cli, AgentModePolicy.ToCli(host));

    [Theory]
    [InlineData("ask", "manual", true)]
    [InlineData("ask", "bypassPermissions", false)]
    [InlineData("automatic", "default", true)]
    [InlineData("automatic", "bypassPermissions", false)]
    [InlineData("auto", "bypassPermissions", true)]
    public void ProviderReportsCannotSilentlyReplaceHostMode(string host, string reported, bool expected) =>
        Assert.Equal(expected, AgentModePolicy.RepresentsHostMode(host, reported));
}