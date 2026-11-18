using System.Text.Json;

namespace Codale.Smart.Tests;

public sealed class SmartRoleTests
{
    private static SmartExperience Create() => new(
        () => throw new InvalidOperationException("Native runtime must not be created."),
        (_, _) => throw new InvalidOperationException("Models must not be downloaded."));
    private static SmartRequestContext Context(string task = "caller-label") => new(Environment.CurrentDirectory, Environment.CurrentDirectory, task);

    [Fact]
    public void Background_work_is_offered_only_read_tools()
    {
        var names = SmartTools.For(background: true).Select(t => JsonDocument.Parse(JsonSerializer.Serialize(t)).RootElement.GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain("run_shell", names);
        Assert.Contains("read_file", names);
        Assert.Contains(SmartTools.For(background: false), t => JsonSerializer.Serialize(t).Contains("run_shell"));
    }

    [Fact]
    public async Task Read_only_execution_refuses_shell_commands()
    {
        var outcome = await new SmartTools().ExecuteAsync(Context(), new("run_shell", new Dictionary<string, string> { ["command"] = "echo hi" }),
            default, readOnly: true);
        Assert.Equal("error", outcome.State);
    }

    [Theory]
    [InlineData("Login Form Validation", "Login Form Validation")]
    [InlineData("\"Fix Flaky Upload Test.\"", "Fix Flaky Upload Test")]
    [InlineData("Title: Settings Redesign\nmore text", "Settings Redesign")]
    [InlineData("", null)]
    [InlineData("This title has far too many words to be a session name at all", null)]
    public void Titles_are_short_single_lines(string raw, string? expected) =>
        Assert.Equal(expected, SmartExperience.ParseTitle(raw));

    [Fact]
    public async Task Requests_on_different_lanes_run_side_by_side()
    {
        await using var smart = Create();
        var slow = new TaskCompletionSource<string>();
        await smart.ConfigureRemoteAsync("test", (task, _, _, _, _) => task == "commit" ? slow.Task : Task.FromResult("{\"command\":\"git status\"}"),
            laneFor: task => task == "shell" ? "decision" : "general");
        var commit = smart.GenerateAsync(Context("commit"), "s", "diff", 64, text => text);
        var shell = await smart.ShellCommandAsync(Context(), "show status", "pwsh.exe").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("git status", shell?.Value);
        Assert.False(commit.IsCompleted);
        slow.SetResult("feat: done");
        Assert.Equal("feat: done", (await commit)?.Value);
    }
}