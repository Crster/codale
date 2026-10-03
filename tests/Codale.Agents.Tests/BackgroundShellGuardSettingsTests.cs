using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>Pins how the background-shell refusal reaches Claude (a settings hook) and what counts as a background launch.</summary>
public sealed class BackgroundShellGuardSettingsTests
{
    [Fact]
    public void Settings_carry_the_hook_and_the_ask_rules_together()
    {
        var servers = new[] { new McpServerSpec { Name = "codale-computer", Command = "c.exe", RequireApproval = true } };

        var json = McpServerSpec.ToClaudeSettings(servers, new ClaudeHookSettings { ShellGuardCommand = "\"C:/x/t.exe\" --pretooluse-hook" })!;
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("mcp__codale-computer", root.GetProperty("permissions").GetProperty("ask")[0].GetString());
        var hook = root.GetProperty("hooks").GetProperty("PreToolUse")[0];
        Assert.Equal("Bash|PowerShell", hook.GetProperty("matcher").GetString());
        Assert.Equal("\"C:/x/t.exe\" --pretooluse-hook", hook.GetProperty("hooks")[0].GetProperty("command").GetString());
    }

    [Fact]
    public void No_servers_and_no_guard_means_no_settings() =>
        Assert.Null(McpServerSpec.ToClaudeSettings([], new ClaudeHookSettings()));

    [Fact]
    public void Claude_arguments_register_the_guard_hook()
    {
        var args = new ClaudeSessionOptions { WorkingDirectory = ".", BackgroundShellGuardCommand = "guard.exe --pretooluse-hook" }
            .BuildArguments().ToList();

        var settings = JsonDocument.Parse(args[args.IndexOf("--settings") + 1]).RootElement;
        Assert.Equal(
            "guard.exe --pretooluse-hook",
            settings.GetProperty("hooks").GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
    }

    [Fact]
    public void Token_saver_hooks_and_deny_rules_land_in_one_settings_document()
    {
        var json = McpServerSpec.ToClaudeSettings([], new ClaudeHookSettings
        {
            ShellGuardCommand = "guard --pretooluse-hook",
            ReadGuardCommand = "guard --pretooluse-hook --read-guard 400",
            ShellOutputCommand = "guard --posttooluse-hook --digest",
            DenyRules = ["Read(**/bin/**)", " ", "Read(**/obj/**)"],
        })!;
        var root = JsonDocument.Parse(json).RootElement;

        var pre = root.GetProperty("hooks").GetProperty("PreToolUse");
        Assert.Equal("Bash|PowerShell", pre[0].GetProperty("matcher").GetString());
        Assert.Equal("Read", pre[1].GetProperty("matcher").GetString());
        Assert.Equal("guard --pretooluse-hook --read-guard 400", pre[1].GetProperty("hooks")[0].GetProperty("command").GetString());

        var post = root.GetProperty("hooks").GetProperty("PostToolUse")[0];
        Assert.Equal("Bash|PowerShell", post.GetProperty("matcher").GetString());
        Assert.Equal(45, post.GetProperty("hooks")[0].GetProperty("timeout").GetInt32());

        var deny = root.GetProperty("permissions").GetProperty("deny").EnumerateArray().Select(d => d.GetString()).ToList();
        Assert.Equal(["Read(**/bin/**)", "Read(**/obj/**)"], deny);
    }

    [Fact]
    public void Agent_definitions_are_passed_with_the_agents_flag()
    {
        var args = new ClaudeSessionOptions { WorkingDirectory = ".", Agents = """{"Explore":{"model":"haiku"}}""" }
            .BuildArguments().ToList();

        Assert.Equal("""{"Explore":{"model":"haiku"}}""", args[args.IndexOf("--agents") + 1]);
        Assert.DoesNotContain("--agents", new ClaudeSessionOptions { WorkingDirectory = "." }.BuildArguments());
    }

    [Fact]
    public void Session_hooks_keep_the_shell_guard_from_the_older_option()
    {
        var args = new ClaudeSessionOptions
        {
            WorkingDirectory = ".",
            BackgroundShellGuardCommand = "guard.exe --pretooluse-hook",
            Hooks = new ClaudeHookSettings { ShellOutputCommand = "guard.exe --posttooluse-hook" },
        }.BuildArguments().ToList();

        var hooks = JsonDocument.Parse(args[args.IndexOf("--settings") + 1]).RootElement.GetProperty("hooks");
        Assert.Equal("guard.exe --pretooluse-hook", hooks.GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
        Assert.Equal("guard.exe --posttooluse-hook", hooks.GetProperty("PostToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
    }

    [Theory]
    [InlineData("PowerShell", """{"command":"npm run dev","mode":"async"}""", true)]
    [InlineData("PowerShell", """{"command":"npm run dev","mode":"sync"}""", false)]
    [InlineData("PowerShell", """{"command":"x","detach":true}""", true)]
    [InlineData("Bash", """{"command":"x","run_in_background":true}""", true)]
    [InlineData("Read", """{"mode":"async"}""", false)]
    public void Background_launch_covers_both_providers(string tool, string input, bool expected) =>
        Assert.Equal(expected, BackgroundTaskDetector.IsBackgroundLaunch(tool, JsonDocument.Parse(input).RootElement));
}
