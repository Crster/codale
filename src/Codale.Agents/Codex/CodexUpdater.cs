using System.Diagnostics;
using System.Text;

using Codale.Core.Processes;

namespace Codale.Agents.Codex;

/// <summary>Runs npm outside Codale, after shutdown has released the Codex executable.</summary>
public static class CodexUpdater
{
    public static async Task StartAfterExitAsync(string logPath)
    {
        var npm = CliLocator.Find("npm.cmd")
            ?? throw new InvalidOperationException("npm was not found. Install Node.js before updating Codex.");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var readyPath = logPath + ".ready-" + Guid.NewGuid().ToString("N");
        var script = BuildScript(Environment.ProcessId, npm, logPath, readyPath);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(logPath)!,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        // This process must not belong to a terminal/task job: disposing the workspace kills those.
        using var process = await ProcessUtil.StartAsync(start)
            ?? throw new InvalidOperationException("Could not start the Codex updater.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(readyPath))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"The Codex updater could not start. See {logPath}.");
                }

                await Task.Delay(50, timeout.Token);
            }
        }
        catch
        {
            ProcessUtil.TryKillTree(process);
            throw;
        }
        finally
        {
            File.Delete(readyPath);
        }
    }

    internal static string BuildScript(int ownerPid, string npm, string logPath, string? readyPath = null,
        int shutdownTimeoutSeconds = 120) => $$"""
        $ErrorActionPreference = 'Stop'
        $logPath = {{Quote(logPath)}}
        try {
            'Waiting for Codale to close before updating Codex.' | Set-Content -LiteralPath $logPath
            $owner = Get-Process -Id {{ownerPid}} -ErrorAction SilentlyContinue
            {{(readyPath is null ? "" : $"'ready' | Set-Content -LiteralPath {Quote(readyPath)}")}}
            if ($null -ne $owner) {
                $owner | Wait-Process -Timeout {{shutdownTimeoutSeconds}} -ErrorAction Stop
            }
            'Updating Codex...' | Add-Content -LiteralPath $logPath
            # Windows PowerShell treats native stderr as an error record. npm emits warnings there.
            $ErrorActionPreference = 'Continue'
            & {{Quote(npm)}} i -g '@openai/codex@latest' *>> $logPath
            $updateExitCode = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            if ($updateExitCode -ne 0) {
                throw "npm exited with code $updateExitCode. Codex was not updated."
            }
            'Codex updated successfully. You can reopen Codale.' | Add-Content -LiteralPath $logPath
            exit 0
        } catch {
            "Codex update failed: $($_.Exception.Message)" | Add-Content -LiteralPath $logPath
            exit 1
        }
        """;

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
