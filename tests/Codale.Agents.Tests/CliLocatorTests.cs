using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public class CliLocatorTests
{
    [Fact]
    public void KnownClis_contains_most_common_agentic_clis()
    {
        var ids = CliLocator.KnownClis.Select(c => c.Id).ToList();

        Assert.Contains("claude", ids);
        Assert.Contains("agy", ids);
        Assert.Contains("gemini", ids);
        Assert.Contains("copilot", ids);
        Assert.Contains("aider", ids);
        Assert.Contains("goose", ids);
        Assert.Contains("codex", ids);
        Assert.Contains("cursor", ids);

        var agy = CliLocator.KnownClis.First(c => c.Id == "agy");
        Assert.Equal("Antigravity", agy.DisplayName);
        Assert.Equal("agy", agy.Executable);

        var codex = CliLocator.KnownClis.First(c => c.Id == "codex");
        Assert.Equal("Codex", codex.DisplayName);
        Assert.Equal("codex", codex.Executable);
        Assert.Equal("npm i -g @openai/codex", codex.InstallCommand);
        Assert.Equal("@openai/codex", CliVersionCheck.NpmPackage(codex));
    }

    [Fact]
    public void DetectInstalledClis_returns_installed_list()
    {
        var installed = CliLocator.DetectInstalledClis();
        Assert.NotNull(installed);
        // Each detected CLI must resolve to an existing executable
        foreach (var cli in installed)
        {
            Assert.True(cli.IsInstalled);
            Assert.NotNull(cli.ResolvedPath);
        }
    }

    [Fact]
    public void ToStandardMcpConfig_generates_valid_mcp_servers_json()
    {
        var spec = new McpServerSpec
        {
            Name = "test-tool",
            Command = "test.exe",
            Args = ["--arg1", "val1"],
            Env = new Dictionary<string, string> { ["KEY"] = "VALUE" }
        };

        var json = McpServerSpec.ToStandardMcpConfig([spec]);
        Assert.NotNull(json);
        Assert.Contains("test-tool", json);
        Assert.Contains("test.exe", json);
        Assert.Contains("KEY", json);
    }
}
