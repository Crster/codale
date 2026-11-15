namespace Codale.Terminal.Tests;

/// <summary>
/// The notebook input's ghost suggestion under PowerShell: PSReadLine history parsing, turning a
/// tab completion into a whole-line suggestion, and the warm PowerShell process that answers.
/// </summary>
public sealed class PowerShellSuggestionsTests
{
    [Fact]
    public void History_is_newest_first_once_each_with_backtick_lines_joined()
    {
        var history = PowerShellSuggestions.ParseHistory(
        [
            "git status",
            "Get-ChildItem `",
            "  -Recurse",
            "",
            "dotnet build",
            "git status",
        ]);

        Assert.Equal(["git status", "dotnet build", "Get-ChildItem \n  -Recurse"], history);
    }

    [Theory]
    [InlineData("Get-ChildI", 0, 10, new[] { "Get-ChildItem" }, "Get-ChildItem")]
    [InlineData("get-childi", 0, 10, new[] { "Get-ChildItem" }, "get-childitem")]
    [InlineData("cd sr", 3, 2, new[] { @".\src" }, "cd src")]
    [InlineData(@"cd .\sr", 3, 4, new[] { @".\src" }, @"cd .\src")]
    [InlineData("Get-ChildItem -Rec", 14, 4, new[] { "-Recurse" }, "Get-ChildItem -Recurse")]
    public void A_completion_that_carries_the_word_further_becomes_the_line(string typed, int index, int length, string[] matches, string expected) =>
        Assert.Equal(expected, PowerShellSuggestions.CompleteLine(typed, index, length, matches));

    [Theory]
    [InlineData("cd My", 3, 2, new[] { @"'.\My Folder'" })]
    [InlineData("Get-ChildItem -", 14, 1, new[] { "-Path" })]
    [InlineData("git ", 4, 0, new[] { @".\src" })]
    [InlineData("Get-ChildItem", 0, 13, new[] { "Get-ChildItem" })]
    public void A_completion_that_does_not_extend_what_is_typed_is_not_shown(string typed, int index, int length, string[] matches) =>
        Assert.Null(PowerShellSuggestions.CompleteLine(typed, index, length, matches));

    [Fact]
    public void A_one_item_answer_from_Windows_PowerShell_still_reads()
    {
        Assert.Equal("Get-ChildItem", PowerShellCompletionHost.Parse("Get-ChildI", """{"length":10,"index":0,"matches":"Get-ChildItem","id":1}"""));
    }

    [Theory]
    [InlineData("pwsh.exe")]
    [InlineData("powershell.exe")]
    public async Task A_real_PowerShell_completes_a_command_name(string exe)
    {
        var shell = TerminalSession.InstalledShells().FirstOrDefault(s => Path.GetFileName(s.Path).Equals(exe, StringComparison.OrdinalIgnoreCase)).Path;
        if (shell is null)
        {
            return;
        }

        var host = PowerShellCompletionHost.For(shell, loadProfile: false);
        var line = await host.CompleteLineAsync("Get-ChildI", Path.GetTempPath(), CancellationToken.None);
        Assert.Equal("Get-ChildItem", line);

        // A second request goes to the same, now warm, process.
        var parameter = await host.CompleteLineAsync("Get-ChildItem -Recu", Path.GetTempPath(), CancellationToken.None);
        Assert.Equal("Get-ChildItem -Recurse", parameter);
    }

    [Fact]
    public async Task A_real_PowerShell_completes_a_folder_in_the_requested_directory()
    {
        var shell = TerminalSession.InstalledShells().FirstOrDefault(s => PowerShellSuggestions.IsPowerShell(s.Path)).Path;
        if (shell is null)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "codale-ps-complete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sourcery"));
        try
        {
            var line = await PowerShellCompletionHost.For(shell, loadProfile: false).CompleteLineAsync("cd sour", root, CancellationToken.None);
            Assert.Equal("cd sourcery", line);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}