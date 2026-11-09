using Codale.Core.Shell;

namespace Codale.Core.Tests;

public sealed class PlanningShellPolicyTests
{
    [Theory]
    [InlineData("Get-Content app/crons/reamazed.py; Get-Content app/services/reamazed.py; Get-Content app/crons/parcelwill.py; Get-Content migrations/0002_reamazed_schema.py; Get-Content migrations/0026_parcelwill_schema.py")]
    [InlineData("\"C:\\Users\\Crster\\pwsh.exe\" -Command 'Get-Content a.py; Get-Content b.py'")]
    [InlineData("powershell.exe -c \"Get-ChildItem src -Recurse | Select-Object -First 10\"")]
    [InlineData("Select-String -Pattern 'foo;bar' -Path 'some folder/file.cs'")]
    [InlineData("Get-Content a\nGet-Content b")]
    [InlineData("rg -n 'PermissionMode' src")]
    [InlineData("rg --files -g '*.cs' | head -n 20")]
    [InlineData("grep -n 'mode' file.cs; cat file.cs | wc -l")]
    [InlineData("git status --short; git ls-files src")]
    [InlineData("pwsh -c \"rg --files src; Get-Content README.md\"")]
    public void Allows_literal_file_inspection(string command) =>
        Assert.True(PlanningShellPolicy.AllowsInspection(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dotnet build")]
    [InlineData("git push")]
    [InlineData("python inspect.py")]
    [InlineData("Get-Content a; Set-Content a changed")]
    [InlineData("Get-Content a\rSet-Content a changed")]
    [InlineData("Get-Content a | Out-File b")]
    [InlineData("Get-Content a > b")]
    [InlineData("Get-Content a & Remove-Item b")]
    [InlineData("Get-Content $(Remove-Item b)")]
    [InlineData("Get-Content \"$(Remove-Item b)\"")]
    [InlineData("Get-Content a | ForEach-Object { Remove-Item b }")]
    [InlineData("Get-Content a `\n; Remove-Item b")]
    [InlineData("Get-Content 'a; Remove-Item b")]
    [InlineData("other.exe -Command 'Get-Content a'")]
    [InlineData("pwsh -EncodedCommand Zm9v")]
    [InlineData("pwsh -Command Get-Content a")]
    [InlineData("rg --pre python pattern src")]
    [InlineData("rg --pre=script.exe pattern src")]
    [InlineData("rg --hostname-bin=script.exe pattern src")]
    [InlineData("rg pattern src; dotnet build")]
    [InlineData("rg pattern src > results.txt")]
    [InlineData("git status; git reset --hard")]
    [InlineData("git -c core.fsmonitor=script.exe status")]
    public void Rejects_writes_and_unrecognized_syntax(string? command) =>
        Assert.False(PlanningShellPolicy.AllowsInspection(command));

    [Theory]
    [InlineData("Read")]
    [InlineData("Glob")]
    [InlineData("Grep")]
    [InlineData("LS")]
    [InlineData("WebSearch")]
    [InlineData("WebFetch")]
    public void Read_tools_need_no_planning_confirmation(string toolName) =>
        Assert.True(PlanningShellPolicy.AllowsToolInspection(toolName));

    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    public void Both_shell_tools_require_recognized_inspection(string toolName)
    {
        Assert.True(PlanningShellPolicy.AllowsToolInspection(toolName, "rg --files src"));
        Assert.False(PlanningShellPolicy.AllowsToolInspection(toolName, "dotnet build"));
        Assert.False(PlanningShellPolicy.AllowsToolInspection(toolName));
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("MultiEdit")]
    [InlineData("NotebookEdit")]
    [InlineData("Permissions")]
    [InlineData("AskUserQuestion")]
    [InlineData("ExitPlanMode")]
    [InlineData("mcp__custom__Read")]
    public void Writes_questions_and_unknown_tools_still_need_approval(string toolName) =>
        Assert.False(PlanningShellPolicy.AllowsToolInspection(toolName, "rg --files src"));
}
