using Codale.Agents.Codex;

using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// Pins the codex spawn shape: the app-server subcommand, MCP servers as TOML config
/// overrides, and Codale's permission modes mapped onto codex approval/sandbox policies.
/// </summary>
public sealed class CodexSessionOptionsTests
{
    [Fact]
    public void Arguments_end_with_the_app_server_subcommand()
    {
        var args = new CodexSessionOptions { WorkingDirectory = "." }.BuildArguments().ToList();

        Assert.Equal("app-server", args[^1]);
    }

    [Fact]
    public void Windows_gets_the_unelevated_sandbox()
    {
        var tail = string.Join(' ', new CodexSessionOptions { WorkingDirectory = "." }.BuildArguments());

        Assert.Equal(OperatingSystem.IsWindows(), tail.Contains("-c windows.sandbox='unelevated'"));
    }

    [Fact]
    public void Mcp_servers_become_toml_config_overrides_before_the_subcommand()
    {
        var args = new CodexSessionOptions
        {
            WorkingDirectory = ".",
            McpServers =
            [
                new McpServerSpec { Name = "codale-browser", Command = "codale-browser.exe", Args = ["--pipe", "abc"], Env = new Dictionary<string, string> { ["TOKEN"] = "t1" } },
            ],
        }.BuildArguments().ToList();

        var tail = string.Join(' ', args);
        Assert.EndsWith("app-server", tail);
        Assert.Contains("-c mcp_servers.codale-browser.command='codale-browser.exe'", tail);
        Assert.Contains("-c mcp_servers.codale-browser.args=['--pipe','abc']", tail);
        Assert.Contains("-c mcp_servers.codale-browser.env={TOKEN='t1'}", tail);
    }

    [Fact]
    public void Host_managed_mcp_tools_are_approved_except_those_marked_ask()
    {
        var args = new CodexSessionOptions
        {
            WorkingDirectory = ".",
            McpServers =
            [
                new McpServerSpec
                {
                    Name = "codale-tasks",
                    Command = "codale-tasks.exe",
                    AutoApprove = true,
                    AskTools = ["start_task", " run_command ", ""],
                },
            ],
        }.BuildArguments().ToList();

        var tail = string.Join(' ', args);
        Assert.Contains("-c mcp_servers.codale-tasks.default_tools_approval_mode='approve'", tail);
        Assert.Contains("-c mcp_servers.codale-tasks.tools.start_task.approval_mode='prompt'", tail);
        Assert.Contains("-c mcp_servers.codale-tasks.tools.run_command.approval_mode='prompt'", tail);
    }

    [Fact]
    public void Approval_required_mcp_servers_prompt_every_tool()
    {
        var overrides = CodexSessionOptions.McpOverrides(
        [
            new McpServerSpec
            {
                Name = "desktop",
                Command = "desktop.exe",
                RequireApproval = true,
                AutoApprove = true,
                AskTools = ["isolated"],
            },
        ]);

        Assert.Contains(("mcp_servers.desktop.default_tools_approval_mode", "'prompt'"), overrides);
        Assert.DoesNotContain(overrides, pair => pair.Key.EndsWith(".approval_mode", StringComparison.Ordinal) && pair.Value == "'approve'");
    }

    [Fact]
    public void Unmarked_mcp_servers_inherit_the_session_approval_policy()
    {
        var overrides = CodexSessionOptions.McpOverrides([new McpServerSpec { Name = "search", Command = "search.exe" }]);

        Assert.DoesNotContain(overrides, pair => pair.Key.Contains("approval_mode", StringComparison.Ordinal));
    }

    [Fact]
    public void Permission_modes_map_onto_approval_and_sandbox_policies()
    {
        Assert.Equal(("untrusted", "readOnly"), CodexSessionOptions.PermissionPolicy("manual"));
        Assert.Equal(("on-request", "readOnly"), CodexSessionOptions.PermissionPolicy("plan"));
        Assert.Equal(("on-request", "workspaceWrite"), CodexSessionOptions.PermissionPolicy("acceptEdits"));
        Assert.Equal(("never", "dangerFullAccess"), CodexSessionOptions.PermissionPolicy("auto"));
        Assert.Equal(("never", "dangerFullAccess"), CodexSessionOptions.PermissionPolicy("dontAsk"));
        Assert.Equal(("never", "dangerFullAccess"), CodexSessionOptions.PermissionPolicy("bypassPermissions"));
        Assert.Equal(("on-request", "readOnly"), CodexSessionOptions.AskPolicy);
    }

    [Fact]
    public void Custom_provider_and_model_become_toml_config_overrides()
    {
        var options = new CodexSessionOptions
        {
            WorkingDirectory = ".",
            Model = "my-custom-model",
            CustomProvider = new CodexCustomProvider("My Gateway", "https://api.example.com/v1", "OPENAI_API_KEY", "responses"),
        };

        var args = options.BuildArguments().ToList();
        var tail = string.Join(' ', args);

        Assert.EndsWith("app-server", tail);
        Assert.Contains("-c model_provider='custom'", tail);
        Assert.Contains("-c model_providers.custom.name='My Gateway'", tail);
        Assert.Contains("-c model_providers.custom.base_url='https://api.example.com/v1'", tail);
        Assert.Contains("-c model_providers.custom.env_key='OPENAI_API_KEY'", tail);
        Assert.Contains("-c model_providers.custom.wire_api='responses'", tail);
        Assert.Contains("-c model_providers.custom.supports_websockets=false", tail);
        Assert.Contains("-c model='my-custom-model'", tail);
    }

    [Fact]
    public void Custom_provider_context_window_becomes_a_config_override()
    {
        var args = new CodexSessionOptions
        {
            WorkingDirectory = ".",
            Model = "my-custom-model",
            ContextWindow = 200_000,
        }.BuildArguments().ToList();

        Assert.Contains("-c model_context_window=200000", string.Join(' ', args));
    }

    [Fact]
    public void Missing_context_window_is_not_sent_to_codex()
    {
        var args = new CodexSessionOptions
        {
            WorkingDirectory = ".",
            ContextWindow = 0,
        }.BuildArguments().ToList();

        Assert.DoesNotContain(args, arg => arg.Contains("model_context_window", StringComparison.Ordinal));
    }

    [Fact]
    public void Foreign_modes_and_efforts_fall_back_to_the_defaults()
    {
        Assert.Equal(("never", "dangerFullAccess"), CodexSessionOptions.PermissionPolicy("yolo"));
        Assert.Null(CodexSessionOptions.ClampEffort("max")); // codex has no "max"
        Assert.Equal("high", CodexSessionOptions.ClampEffort("high"));
        Assert.Null(CodexSessionOptions.ClampEffort(null));
    }

    [Fact]
    public void Session_initialized_event_carries_context_window()
    {
        var init = new SessionInitialized
        {
            Model = "custom-model",
            WorkingDirectory = "C:\\test",
            PermissionMode = "auto",
            ContextWindow = 128_000,
        };

        Assert.Equal(128_000, init.ContextWindow);
    }

    [Fact]
    public void Agent_model_info_carries_context_window()
    {
        var info = new AgentModelInfo
        {
            Id = "custom-model",
            DisplayName = "Custom Model",
            ContextWindow = 65_536,
        };

        Assert.Equal(65_536, info.ContextWindow);
    }
}
