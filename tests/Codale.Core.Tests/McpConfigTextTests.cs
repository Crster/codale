using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class McpConfigTextTests
{
    private static McpServerSpec Server(string name, string command = "node", params string[] args) =>
        new() { Name = name, Command = command, Args = args };

    [Fact]
    public void PowerShellQuotingDoublesSingleQuotes() =>
        Assert.Equal("'a''b'", McpConfigText.Quote(powershell: true, "a'b"));

    [Fact]
    public void ShellQuotingWrapsInDoubleQuotes() =>
        Assert.Equal("\"a'b\"", McpConfigText.Quote(powershell: false, "a'b"));

    [Fact]
    public void AShellEnvironmentVariableIsSetForTheCommandAndCleared() =>
        Assert.Equal("set \"NAME=value\" && run & set \"NAME=\"", McpConfigText.WithEnv(false, "NAME", "value", "run"));

    [Fact]
    public void APowerShellEnvironmentVariableIsRemovedWhenTheCommandEnds() =>
        Assert.Equal(
            "& { $env:NAME='value'; try { run } finally { Remove-Item Env:NAME -ErrorAction SilentlyContinue } }",
            McpConfigText.WithEnv(true, "NAME", "value", "run"));

    [Fact]
    public void CodexOverridesSetTheCommandAndArgumentsOfEachServer() =>
        Assert.Equal(
            "-c \"mcp_servers.s.command='node'\" -c \"mcp_servers.s.args=['a.js']\"",
            McpConfigText.CodexOverrides(powershell: false, [Server("s", "node", "a.js")]));

    [Fact]
    public void CodexOverridesCarryAnApprovalMode()
    {
        var prompt = new McpServerSpec { Name = "s", Command = "node", RequireApproval = true };
        var approve = new McpServerSpec { Name = "t", Command = "node", AutoApprove = true };

        var text = McpConfigText.CodexOverrides(powershell: false, [prompt, approve]);

        Assert.Contains("mcp_servers.s.default_tools_approval_mode='prompt'", text);
        Assert.Contains("mcp_servers.t.default_tools_approval_mode='approve'", text);
    }

    [Fact]
    public void CodexFeatureFlagsAreAppendedWithALeadingSpace() =>
        Assert.Equal(
            " -c \"features.computer_use=true\" -c \"features.in_app_browser=false\"",
            McpConfigText.CodexFeatureOverrides(false, new Dictionary<string, bool>
            {
                ["computer_use"] = true,
                ["in_app_browser"] = false,
            }));

    [Fact]
    public void CopilotConfigMarksEachServerLocalWithAllTools()
    {
        var text = McpConfigText.CopilotConfig([Server("s")]);

        Assert.Contains("\"type\":\"local\"", text);
        Assert.Contains("\"tools\":[\"*\"]", text);
    }

    [Fact]
    public void TheDirectiveIsNullWhenNoToolIsOn() =>
        Assert.Null(McpConfigText.Directive([Server("other")], "browser-use", "computer-use", "phone-use"));

    [Fact]
    public void TheBrowserDirectiveNamesItsToolPrefix()
    {
        var text = McpConfigText.Directive([Server("browser-use")], "browser-use", "computer-use", "phone-use");

        Assert.NotNull(text);
        Assert.StartsWith("The mcp__browser-use__* tools drive Codale's own Chromium", text);
    }

    [Fact]
    public void EachToolGetsItsOwnDirectiveInOrder()
    {
        var text = McpConfigText.Directive(
            [Server("phone-use"), Server("computer-use"), Server("browser-use")],
            "browser-use", "computer-use", "phone-use");

        Assert.NotNull(text);
        var browser = text.IndexOf("browser-use__*", StringComparison.Ordinal);
        var computer = text.IndexOf("computer-use__*", StringComparison.Ordinal);
        var phone = text.IndexOf("phone-use__*", StringComparison.Ordinal);
        Assert.True(browser < computer && computer < phone);
    }
}