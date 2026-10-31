using Codale.Agents;

namespace Codale.Agents.Tests;

public class CliVersionCheckTests
{
    [Theory]
    [InlineData("2.1.5 (Claude Code)", "2.1.5")]
    [InlineData("2.0.14\r\n", "2.0.14")]
    [InlineData("codex-cli 0.77.0", "0.77.0")]
    [InlineData("v1.2.3-beta", "1.2.3")]
    public void Parse_finds_the_version_in_cli_output(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), CliVersionCheck.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("command not found")]
    public void Parse_returns_null_when_there_is_no_version(string? text) =>
        Assert.Null(CliVersionCheck.Parse(text));

    [Theory]
    [InlineData("claude", "@anthropic-ai/claude-code", "claude update")]
    [InlineData("codex", "@openai/codex", "npm i -g @openai/codex@latest")]
    public void Updates_use_the_selected_cli_package_and_command(string cliId, string package, string command)
    {
        var cli = CliLocator.KnownClis.Single(cli => cli.Id == cliId);

        Assert.Equal(package, CliVersionCheck.NpmPackage(cli));
        Assert.Equal(command, CliVersionCheck.UpdateCommand(cli));
    }
}
