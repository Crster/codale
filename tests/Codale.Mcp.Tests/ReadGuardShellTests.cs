using System.Text.Json.Nodes;

using Codale.Mcp.Tasks;

namespace Codale.Mcp.Tests;

/// <summary>The read guard's shell coverage and its limit on repeated whole-file reads.</summary>
public sealed class ReadGuardShellTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codale-guard-" + Guid.NewGuid().ToString("N"));

    public ReadGuardShellTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string State => Path.Combine(_dir, "state");

    private string WriteLines(string name, int count)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, Enumerable.Range(1, count).Select(i => $"line {i}"));
        return path;
    }

    private string Shell(string tool, string command) =>
        new JsonObject
        {
            ["session_id"] = "s1",
            ["cwd"] = _dir,
            ["tool_name"] = tool,
            ["tool_input"] = new JsonObject { ["command"] = command },
        }.ToJsonString();

    private static string Read(string path) =>
        new JsonObject { ["session_id"] = "s1", ["tool_name"] = "Read", ["tool_input"] = new JsonObject { ["file_path"] = path } }.ToJsonString();

    private static string? Reason(string? reply) =>
        reply is null ? null : JsonNode.Parse(reply)!["hookSpecificOutput"]!["permissionDecisionReason"]!.GetValue<string>();

    [Theory]
    [InlineData("Bash", "cat Big.cs")]
    [InlineData("Bash", "cat -n Big.cs")]
    [InlineData("Bash", "cd sub && cat ../Big.cs")]
    [InlineData("PowerShell", "Get-Content Big.cs")]
    [InlineData("PowerShell", "Get-Content -Path 'Big.cs' -Raw")]
    [InlineData("PowerShell", "gc Big.cs")]
    [InlineData("PowerShell", "type \"Big.cs\"")]
    public void Printing_a_big_file_through_the_shell_is_refused(string tool, string command)
    {
        WriteLines("Big.cs", 900);
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));

        var reason = Reason(ReadGuard.Evaluate(Shell(tool, command), State));

        Assert.NotNull(reason);
        Assert.StartsWith(ReadGuard.ReasonPrefix, reason);
        Assert.Contains("through the shell", reason);
    }

    [Theory]
    [InlineData("cat Big.cs | head -50")]
    [InlineData("sed -n '1,80p' Big.cs")]
    [InlineData("Get-Content Big.cs -TotalCount 40")]
    [InlineData("cat Small.cs")]
    [InlineData("cat Big.cs Small.cs")]
    [InlineData("cat $file")]
    [InlineData("dotnet build")]
    public void Ranged_small_and_other_shell_commands_pass(string command)
    {
        WriteLines("Big.cs", 900);
        WriteLines("Small.cs", 50);

        Assert.Null(ReadGuard.Evaluate(Shell("Bash", command), State));
    }

    [Fact]
    public void A_repeated_read_of_a_very_large_file_is_still_refused()
    {
        var huge = WriteLines("Huge.cs", 3000);

        Assert.NotNull(ReadGuard.Evaluate(Read(huge), State));
        var again = Reason(ReadGuard.Evaluate(Read(huge), State));

        Assert.NotNull(again);
        Assert.Contains("only read in ranges", again);
    }

    [Fact]
    public void A_repeated_read_of_a_medium_file_goes_through()
    {
        var medium = WriteLines("Medium.cs", 900);

        Assert.NotNull(ReadGuard.Evaluate(Read(medium), State));
        Assert.Null(ReadGuard.Evaluate(Read(medium), State));
    }
}
