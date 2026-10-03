using System.Diagnostics;

namespace Codale.Core.Processes;

/// <summary>Process helpers shared by every project that spawns children.</summary>
public static class ProcessUtil
{
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
