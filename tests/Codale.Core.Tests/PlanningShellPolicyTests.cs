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
    public void Rejects_writes_and_unrecognized_syntax(string? command) =>
        Assert.False(PlanningShellPolicy.AllowsInspection(command));
}
