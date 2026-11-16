using System.Text;

using Codale.Core.Processes;

namespace Codale.Agents.Codex;

/// <summary>Runs npm (or winget, for a winget install) outside Codale, after shutdown has released the Codex executable.</summary>
public static class CodexUpdater
{

    internal static string BuildScript(int ownerPid, string npm, string logPath, string? readyPath = null,
        int shutdownTimeoutSeconds = 120, string? winget = null) => $$"""
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
            & {{Quote(npm)}} {{(winget is null ? "i -g '@openai/codex@latest'" : $"upgrade --id {winget} --source winget --exact --disable-interactivity --accept-source-agreements --accept-package-agreements")}} *>> $logPath
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