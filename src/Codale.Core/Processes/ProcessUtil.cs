using System.Diagnostics;

namespace Codale.Core.Processes;

/// <summary>Process helpers shared by every project that spawns children.</summary>
public static class ProcessUtil
{
    /// <summary>
    /// <see cref="Process.Start(ProcessStartInfo)"/> on the thread pool. Starting a process takes a
    /// process-wide lock; a UI thread blocked on it while another start holds it pumps messages, and
    /// WinUI re-entered from inside an event handler fails fast (0xc000027b), which is how a launch
    /// that spawns several CLIs at once crashed. Every caller that may sit on the UI thread uses this.
    /// </summary>
    public static Task<Process?> StartAsync(ProcessStartInfo startInfo) =>
        Task.Run(() => Process.Start(startInfo));

    /// <summary>
    /// Kills <paramref name="process"/> and its descendants. Never throws: the process
    /// having already exited, or access being denied, both mean there is nothing to do.
    /// </summary>
    public static void TryKillTree(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone or not ours to kill.
        }
    }
}
