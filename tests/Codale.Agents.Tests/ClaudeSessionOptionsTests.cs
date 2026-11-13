using Codale.Agents.Claude;

namespace Codale.Agents.Tests;

/// <summary>
/// Pins the spawn arguments for the host-level standing instructions: the system
/// prompt append that makes every session plan its work as task-tool steps.
/// </summary>
public sealed class ClaudeSessionOptionsTests
{
    [Fact]
    public void A_system_prompt_append_rides_as_its_flag_and_value()
    {
        var args = new ClaudeSessionOptions
        {
            WorkingDirectory = ".",
            SystemPromptAppend = "always plan first",
        }.BuildArguments().ToList();

        var flag = args.IndexOf("--append-system-prompt");
        Assert.True(flag >= 0, "the append flag is missing");
        Assert.Equal("always plan first", args[flag + 1]);
    }

    [Fact]
    public void No_append_means_no_flag()
    {
        var args = new ClaudeSessionOptions { WorkingDirectory = "." }.BuildArguments();

        Assert.DoesNotContain("--append-system-prompt", args);
    }

    [Theory]
    [InlineData(true, "--chrome", "--no-chrome")]
    [InlineData(false, "--no-chrome", "--chrome")]
    public void The_chrome_choice_rides_as_its_flag(bool chrome, string expected, string absent)
    {
        var args = new ClaudeSessionOptions { WorkingDirectory = ".", Chrome = chrome }.BuildArguments();

        Assert.Contains(expected, args);
        Assert.DoesNotContain(absent, args);
    }

    [Fact]
    public void No_chrome_choice_leaves_the_cli_setting_alone()
    {
        var args = new ClaudeSessionOptions { WorkingDirectory = "." }.BuildArguments();

        Assert.DoesNotContain("--chrome", args);
        Assert.DoesNotContain("--no-chrome", args);
    }
}
