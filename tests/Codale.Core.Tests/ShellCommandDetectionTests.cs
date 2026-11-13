using Codale.Core.Shell;

namespace Codale.Core.Tests;

public sealed class ShellCommandDetectionTests
{
    [Theory]
    // PowerShell
    [InlineData("Get-ChildItem -Force")]
    [InlineData("gci *.json")]
    [InlineData("$env:TEMP")]
    [InlineData("$x = 1")]
    [InlineData("& 'C:\\tools\\x.exe' --help")]
    [InlineData(".\\build.ps1")]
    [InlineData("foreach ($f in ls) { $f.Name }")]
    // cmd
    [InlineData("dir /a")]
    [InlineData("cd ..")]
    [InlineData("type notes.txt")]
    [InlineData("echo %PATH%")]
    [InlineData("cmd /c echo hi")]
    // bash
    [InlineData("ls -la")]
    [InlineData("grep -rn foo src")]
    [InlineData("FOO=1 make build")]
    [InlineData("export PATH=$PATH:/opt/bin")]
    [InlineData("./run.sh")]
    [InlineData("cat log.txt | wc -l")]
    // programs from the database
    [InlineData("npx prisma migrate deploy")]
    [InlineData("git status --short")]
    [InlineData("npm run dev")]
    [InlineData("find . -name '*.json'")]
    public void Commands_are_detected(string text) =>
        Assert.True(ShellCommandClassifier.IsShellCommand(text));

    [Theory]
    [InlineData("list all files")]
    [InlineData("how many lines are in log.txt")]
    [InlineData("show me the largest files")]
    [InlineData("what's in this folder")]
    [InlineData("find all json files in the subfolders")]
    [InlineData("delete the build folder?")]
    [InlineData("")]
    public void Requests_are_not_commands(string text) =>
        Assert.False(ShellCommandClassifier.IsShellCommand(text));

    [Fact]
    public void An_executable_in_the_folder_is_a_command()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codale-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "mytool.cmd"), "@echo off");
            Assert.True(ShellCommandClassifier.IsShellCommand("mytool deploy now", directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("C:\\Users\\me\\AppData\\Local\\Microsoft\\WindowsApps\\pwsh.exe npx prisma migrate deploy", "npx prisma migrate deploy")]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" git status", "git status")]
    [InlineData("bash ls -la", "ls -la")]
    [InlineData("pwsh -NoProfile -Command x", "pwsh -NoProfile -Command x")]
    [InlineData("cmd /c dir", "cmd /c dir")]
    [InlineData("git status --short", "git status --short")]
    public void A_leading_shell_is_dropped(string command, string expected) =>
        Assert.Equal(expected, ShellCommandClassifier.StripShellPrefix(command));
}
