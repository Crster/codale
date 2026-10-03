using System.Text.Json;

using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public class BackgroundTaskDetectorTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("Bash", """{"command":"npm run dev","run_in_background":true}""", true)]
    [InlineData("PowerShell", """{"command":"npm run dev","run_in_background":true}""", true)]
    [InlineData("Bash", """{"command":"ls","run_in_background":false}""", false)]
    [InlineData("Bash", """{"command":"ls"}""", false)]
    [InlineData("Read", """{"run_in_background":true}""", false)]
    public void Detects_background_shells(string tool, string input, bool expected) =>
        Assert.Equal(expected, BackgroundTaskDetector.IsBackgroundShell(tool, Json(input)));

    [Fact]
    public void Parses_launch_result()
    {
        var (id, path) = BackgroundTaskDetector.ParseLaunchResult(
            "Command running in background with ID: bx7k2m. Output is being written to: C:\\Temp\\tasks\\bx7k2m.output");

        Assert.Equal("bx7k2m", id);
        Assert.Equal("C:\\Temp\\tasks\\bx7k2m.output", path);
    }

    [Fact]
    public void Launch_result_without_details_yields_nothing() =>
        Assert.Equal((null, null), BackgroundTaskDetector.ParseLaunchResult("ok"));

    [Fact]
    public void Reads_shell_id_from_bash_output_input() =>
        Assert.Equal("bx7k2m", BackgroundTaskDetector.ShellIdOf(Json("""{"bash_id":"bx7k2m"}""")));

    [Fact]
    public void Strips_ansi() =>
        Assert.Equal("ready in 300ms", BackgroundTaskDetector.StripAnsi("\u001b[32mready\u001b[0m in 300ms"));

    [Fact]
    public void Tailer_returns_only_appended_text()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "one\n");
            using var tailer = new FileTailer(path);
            Assert.Equal("one\n", tailer.Poll());
            Assert.Null(tailer.Poll());

            File.AppendAllText(path, "two\n");
            Assert.Equal("two\n", tailer.Poll());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tailer_tolerates_a_missing_file()
    {
        using var tailer = new FileTailer(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".output"));
        Assert.Null(tailer.Poll());
    }
}
