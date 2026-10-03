using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>Pins how the host's built-in MCP servers reach Claude.</summary>
public sealed class McpServersTests
{
    private static readonly McpServerSpec Browser = new() { Name = "codale-browser", Command = "b.exe", AutoApprove = true };
    private static readonly McpServerSpec Computer = new() { Name = "codale-computer", Command = "c.exe", RequireApproval = true };

    [Fact]
    public void Claude_gets_the_servers_as_mcp_config_and_an_ask_rule_for_the_desktop()
    {
        var args = new ClaudeSessionOptions { WorkingDirectory = ".", McpServers = [Browser, Computer] }
            .BuildArguments().ToList();

        var config = args[args.IndexOf("--mcp-config") + 1];
        Assert.Contains("codale-browser", config);
        Assert.Contains("codale-computer", config);

        var settings = args[args.IndexOf("--settings") + 1];
        Assert.Contains("mcp__codale-computer", settings);
        Assert.DoesNotContain("codale-browser", settings);
    }

    [Fact]
    public void Auto_approved_server_tools_run_unasked_except_its_ask_tools()
    {
        var tasks = new McpServerSpec { Name = "codale-tasks", Command = "t.exe", AutoApprove = true, AskTools = ["start_task"] };
        McpServerSpec[] servers = [tasks, Computer];

        Assert.True(McpServerSpec.RunsUnasked(servers, "mcp__codale-tasks__explore"));
        Assert.False(McpServerSpec.RunsUnasked(servers, "mcp__codale-tasks__start_task"));
        Assert.False(McpServerSpec.RunsUnasked(servers, "mcp__codale-computer__click"));
        Assert.False(McpServerSpec.RunsUnasked(servers, "Bash"));
    }

    [Fact]
    public void Claude_without_servers_gets_neither_flag()
    {
        var args = new ClaudeSessionOptions { WorkingDirectory = "." }.BuildArguments();

        Assert.DoesNotContain("--mcp-config", args);
        Assert.DoesNotContain("--settings", args);
    }

    [Fact]
    public void Ask_tools_prompt_even_when_the_server_is_auto_approved()
    {
        var browser = Browser with { AskTools = ["browser_evaluate", " ", "browser_upload"] };

        var json = McpServerSpec.ToClaudeSettings([browser, Computer], new ClaudeHookSettings())!;
        var ask = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("permissions").GetProperty("ask")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(["mcp__codale-computer", "mcp__codale-browser__browser_evaluate", "mcp__codale-browser__browser_upload"], ask);
    }

    [Fact]
    public void Ask_tools_on_an_approval_required_server_add_no_redundant_rule()
    {
        var computer = Computer with { AskTools = ["click"] };

        var json = McpServerSpec.ToClaudeSettings([computer], new ClaudeHookSettings())!;

        Assert.DoesNotContain("mcp__codale-computer__click", json);
        Assert.Contains("mcp__codale-computer", json);
    }

    [Fact]
    public void Browser_only_needs_no_ask_settings()
    {
        var args = new ClaudeSessionOptions { WorkingDirectory = ".", McpServers = [Browser] }.BuildArguments();

        Assert.Contains("--mcp-config", args);
        Assert.DoesNotContain("--settings", args);
    }
}
