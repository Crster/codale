using Codale.Core.Shell;

namespace Codale.Core.Tests;

public sealed class PlanningShellPolicyTests
{
    // Complete transport shape from the reported approval card, including doubled quotes.
    public const string ScreenshotScript = """
        $ranges = @(@('src/Codale.Smart/SmartConversation.cs',39,71), @('src/Codale.Smart/SmartExperience.cs',114,134), @('src/Codale.Smart/ModelInstaller.cs',1,16), @('src/Codale.Smart/SmartTools.cs',32,56), @('src/Codale.App/ViewModels/ChatViewModel.cs',3712,3737)); foreach ($range in $ranges) { $lines = Get-Content -LiteralPath $range[0]; Write-Output ('FILE ' + $range[0]); for ($i = $range[1]; $i -le $range[2]; $i++) { Write-Output ('{0}: {1}' -f $i, $lines[$i-1]) } }
        """;

    public static string Wrap(string script) =>
        "\"C:\\Users\\Crster\\AppData\\Local\\Microsoft\\WindowsApps\\pwsh.exe\" -Command '" + script.Replace("'", "''") + "'";

    [Fact]
    public void Screenshot_file_ranges_are_inspection_in_both_shell_tools()
    {
        foreach (var command in new[] { ScreenshotScript, Wrap(ScreenshotScript) })
        {
            Assert.True(PlanningShellPolicy.AllowsInspection(command));
            Assert.True(PlanningShellPolicy.AllowsToolInspection("Bash", command));
            Assert.True(PlanningShellPolicy.AllowsToolInspection("PowerShell", command));
        }
    }

    [Fact]
    public void Screenshot_script_is_marked_safe_with_a_verified_read_reason()
    {
        var assessment = ShellCommandAnalyzer.Assess(Wrap(ScreenshotScript));
        Assert.Equal(ShellRisk.Safe, assessment.Risk);
        Assert.Equal("Reads files and prints selected lines", assessment.Reason);
    }

    [Theory]
    [InlineData("$p = 'some folder/a.cs'; $lines = Get-Content -LiteralPath $p; $lines[0..2]")]
    [InlineData("$lines = Get-Content a; for ($i = 0; $i -lt $lines.Count; $i++) { Write-Output \"$i`: $($lines[$i])\" }")]
    [InlineData("$paths = @('a', 'b'); foreach ($p in $paths) { Get-Content $p | Select-Object -First 10 }")]
    [InlineData("Get-Content 'file with ''quote''.cs'")]
    [InlineData("Write-Output 'literal $x; $(Remove-Item a) > b'")]
    [InlineData("rg -n 'foo(bar)' src; git status --short")]
    [InlineData("Get-Content a && Get-Content b")]
    public void Allows_structured_read_only_scripts_plain_and_wrapped(string command)
    {
        Assert.True(PlanningShellPolicy.AllowsInspection(command));
        Assert.True(PlanningShellPolicy.AllowsInspection(Wrap(command)));
    }

    [Theory]
    [InlineData("$lines = Get-Content a; Set-Content a changed")]
    [InlineData("$lines = Remove-Item a; Write-Output $lines")]
    [InlineData("foreach ($p in @('a')) { Get-Content $p; Remove-Item $p }")]
    [InlineData("for ($i = 0; $i -lt 3; $i++) { Write-Output $i; Set-Content a x }")]
    [InlineData("Write-Output \"$(Remove-Item a)\"")]
    [InlineData("$p = 'a'; Get-Content $p > b")]
    [InlineData("$lines = Get-Content a; $lines[0] = 'changed'")]
    [InlineData("$lines = Get-Content a; $lines.Clear()")]
    [InlineData("[System.IO.File]::WriteAllText('a', 'x')")]
    [InlineData("$global:x = 'changed'; Get-Content a")]
    [InlineData("$env:PATH = 'changed'; Get-Content a")]
    [InlineData("$PSDefaultParameterValues = @(); Get-Content a")]
    [InlineData("$ErrorActionPreference = 'Ignore'; Get-Content a")]
    [InlineData("$OutputEncoding = 'x'; Get-Content a")]
    [InlineData("$cmd = 'Get-Content'; & $cmd a")]
    [InlineData(". { Get-Content a }")]
    [InlineData("function Get-Content { Remove-Item a }; Get-Content a")]
    [InlineData("Get-Content a -OutVariable global:x")]
    [InlineData("Get-Content a -OutV global:x")]
    [InlineData("Get-Content a -ov global:x")]
    [InlineData("$pre = '--pre=script.exe'; rg $pre foo src")]
    [InlineData("rg '--pre=script.exe' foo src")]
    [InlineData("rg --'pre'=script.exe foo src")]
    [InlineData("Get-Content $unknown")]
    [InlineData("foreach ($p in @('a')) { Get-Content $p")]
    [InlineData("Get-Content a &")]
    public void Rejects_nested_effects_plain_and_wrapped(string command)
    {
        Assert.False(PlanningShellPolicy.AllowsInspection(command));
        Assert.False(PlanningShellPolicy.AllowsInspection(Wrap(command)));
    }

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
