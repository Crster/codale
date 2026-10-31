using System.Diagnostics;
using System.Text;

using Codale.Agents.Codex;
using Codale.Core.Processes;

namespace Codale.Agents.Tests;

public class CodexUpdaterTests
{
    [Theory]
    [InlineData(0, true, "Codex updated successfully")]
    [InlineData(7, true, "Codex update failed: npm exited with code 7")]
    [InlineData(0, false, "Codex update failed:")]
    public async Task Update_waits_for_shutdown_and_records_the_real_npm_result(
        int exitCode, bool closeOwner, string result)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"Codale updater's 测试 {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var npm = Path.Combine(folder, "npm.cmd");
        var log = Path.Combine(folder, "update.log");
        var ready = Path.Combine(folder, "ready");
        await File.WriteAllTextAsync(npm, $"@echo off\r\necho fake npm %*\r\nexit /b {exitCode}\r\n");
        using var owner = StartPowerShell("Start-Sleep -Seconds 30");
        using var updater = StartPowerShell(CodexUpdater.BuildScript(owner.Id, npm, log, ready,
            shutdownTimeoutSeconds: 3));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(ready))
            {
                await Task.Delay(50, timeout.Token);
            }

            Assert.False(updater.HasExited);
            Assert.False(owner.HasExited);
            Assert.DoesNotContain("fake npm", await File.ReadAllTextAsync(log, timeout.Token));

            if (closeOwner)
            {
                ProcessUtil.TryKillTree(owner);
                await owner.WaitForExitAsync(timeout.Token);
            }

            await updater.WaitForExitAsync(timeout.Token);

            Assert.Equal(closeOwner && exitCode == 0 ? 0 : 1, updater.ExitCode);
            var output = await File.ReadAllTextAsync(log, timeout.Token);
            if (closeOwner)
            {
                Assert.Contains("fake npm i -g @openai/codex@latest", output);
            }
            else
            {
                Assert.DoesNotContain("fake npm", output);
            }
            Assert.Contains(result, output);
        }
        finally
        {
            ProcessUtil.TryKillTree(owner);
            ProcessUtil.TryKillTree(updater);
            await owner.WaitForExitAsync();
            await updater.WaitForExitAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Process StartPowerShell(string script)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return Process.Start(start)!;
    }
}
