using System.Diagnostics;

namespace Codale.Commands.Tests;

/// <summary>
/// The generated invocation for each script kind, run for real through the same shell a
/// terminal tab uses, so a quoting or wrapping mistake fails here rather than in the app.
/// </summary>
public sealed class ShellScriptRunTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-script-run-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("ps", "powershell", "Write-Output 'PS-RAN'")]
    [InlineData("cmd", "batch", "@echo off\r\necho CMD-RAN")]
    [InlineData("sh", "shell", "echo SH-RAN")]
    public void A_saved_script_runs_under_the_terminal_shell(string name, string kind, string body)
    {
        var service = new ProjectCommandService(_ => Path.Combine(_root, "data", "workspace-db"));
        service.Save(_root, null, new ProjectCommandEdit(name, body, ScriptKind: kind));

        var command = Assert.Single(service.Saved(_root));

        // The same shape a terminal tab uses: the saved line handed to cmd's /c.
        var psi = new ProcessStartInfo("cmd.exe", $"/d /c \"{command.Command}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root,
        };

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(output.Contains("-RAN"), $"{name} stdout: {output} stderr: {error} exit: {process.ExitCode}");
    }
}
