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
    public void Permission_modes_map_onto_approval_and_sandbox_policies()
    {
        Assert.Equal(("untrusted", "readOnly"), CodexSessionOptions.PermissionPolicy("manual"));
        Assert.Equal(("on-request", "readOnly"), CodexSessionOptions.PermissionPolicy("plan"));
        Assert.Equal(("on-request", "workspaceWrite"), CodexSessionOptions.PermissionPolicy("acceptEdits"));
        Assert.Equal(("never", "workspaceWrite"), CodexSessionOptions.PermissionPolicy("auto"));
        Assert.Equal(("never", "workspaceWrite"), CodexSessionOptions.PermissionPolicy("dontAsk"));
        Assert.Equal(("never", "dangerFullAccess"), CodexSessionOptions.PermissionPolicy("bypassPermissions"));
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
    public void Foreign_modes_and_efforts_fall_back_to_the_defaults()
    {
        Assert.Equal(("never", "workspaceWrite"), CodexSessionOptions.PermissionPolicy("yolo"));
        Assert.Null(CodexSessionOptions.ClampEffort("max")); // codex has no "max"
        Assert.Equal("high", CodexSessionOptions.ClampEffort("high"));
        Assert.Null(CodexSessionOptions.ClampEffort(null));
    }
}
