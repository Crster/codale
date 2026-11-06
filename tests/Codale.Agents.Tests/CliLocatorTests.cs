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
    public void ResolveNpmShim_only_resolves_the_shim_of_its_own_cli()
    {
        var dir = Directory.CreateTempSubdirectory("codale-shim-").FullName;
        try
        {
            var codexExe = Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(codexExe)!);
            File.WriteAllText(codexExe, "");

            // npm.cmd lives beside codex.cmd in %AppData%\npm; it must stay npm.
            Assert.Null(CliLocator.ResolveNpmShim("npm", Path.Combine(dir, "npm.cmd")));
            Assert.Equal(codexExe, CliLocator.ResolveNpmShim("codex", Path.Combine(dir, "codex.cmd")));
            Assert.Null(CliLocator.ResolveNpmShim("claude", Path.Combine(dir, "claude.cmd")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Find_takes_a_bare_command_name()
    {
        // PATHEXT is appended to the name, so "npm.cmd" would look for "npm.cmd.cmd".
        if (CliLocator.Find("npm") is { } npm)
        {
            Assert.EndsWith("npm.cmd", npm, StringComparison.OrdinalIgnoreCase);
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