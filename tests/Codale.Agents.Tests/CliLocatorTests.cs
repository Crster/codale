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
    public void ActiveCli_follows_preferred_provider()
    {
        try
        {
            CliLocator.PreferredCliProvider = () => "agy";
            Assert.Equal("agy", CliLocator.ActiveCli.Id);
            Assert.Equal("Antigravity", CliLocator.ActiveCli.DisplayName);

            CliLocator.PreferredCliProvider = () => "claude";
            Assert.Equal("claude", CliLocator.ActiveCli.Id);
            Assert.Equal("Claude", CliLocator.ActiveCli.DisplayName);
        }
        finally
        {
            CliLocator.PreferredCliProvider = null;
        }
    }

    [Fact]
    public void Agy_executable_builds_antigravity_stream_json_arguments()
    {
        var options = new ClaudeSessionOptions
        {
            WorkingDirectory = ".",
            Executable = "agy.exe",
            PermissionMode = "auto",
            Model = "gemini-3.8-flash-high",
            Effort = "high",
            ResumeSessionId = "conv-1234",
            AdditionalDirectories = ["dir1", "dir2"],
        };

        var args = options.BuildArguments().ToList();

        Assert.Contains("--input-format", args);
        Assert.Equal("stream-json", args[args.IndexOf("--input-format") + 1]);
        Assert.Contains("--output-format", args);
        Assert.Equal("stream-json", args[args.IndexOf("--output-format") + 1]);
        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.Contains("--model", args);
        Assert.Equal("gemini-3.8-flash-high", args[args.IndexOf("--model") + 1]);
        Assert.Contains("--effort", args);
        Assert.Equal("high", args[args.IndexOf("--effort") + 1]);
        Assert.Contains("--conversation", args);
        Assert.Equal("conv-1234", args[args.IndexOf("--conversation") + 1]);
        Assert.Contains("--add-dir", args);
    }

    [Fact]
    public void Agy_plan_mode_passes_plan_flag()
    {
        var options = new ClaudeSessionOptions
        {
            WorkingDirectory = ".",
            Executable = "agy",
            PermissionMode = "plan",
        };

        var args = options.BuildArguments().ToList();

        Assert.Contains("--mode", args);
        Assert.Equal("plan", args[args.IndexOf("--mode") + 1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
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
