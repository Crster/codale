using System.Runtime.InteropServices;

namespace Codale.Terminal;

/// <summary>
/// Reads the shell's descendant processes, so the tab can show which CLI is actually
/// running and whether anything is running at all. Windows exposes no parent-child
/// query through Process, so one Toolhelp snapshot supplies every process's id, parent
/// id and image name in a single call - no per-process handles to open.
/// </summary>
public static partial class ProcessTree
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const int MaxPath = 260;

    // A real shell tree is a handful deep; the cap only exists to cut a PID-reuse cycle short.
    private const int MaxDepth = 64;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[MaxPath];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    /// <summary>Every running process as pid -> (parent pid, image name without ".exe"); empty when the snapshot fails.</summary>
    private static unsafe Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var processes = new Dictionary<int, (int Parent, string Name)>();

        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return processes;
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                ReadOnlySpan<char> name = new(entry.szExeFile, MaxPath);
                var end = name.IndexOf('\0');
                name = end < 0 ? name : name[..end];
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^4];
                }

                processes[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, name.ToString());
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return processes;
    }

    /// <summary>
    /// The longest ancestor chain starting at <paramref name="rootPid"/>, root first:
    /// e.g. [pwsh, node, claude] while claude runs, [pwsh] while the shell idles.
    /// Processes that vanish mid-walk are simply left out.
    /// </summary>
    public static IReadOnlyList<string> GetCommandChain(int rootPid)
    {
        var processes = Snapshot();

        // Steps from pid up to the root, or -1 when it does not descend from it.
        int Depth(int pid)
        {
            var current = pid;
            for (var steps = 0; steps <= MaxDepth; steps++)
            {
                if (current == rootPid)
                {
                    return steps;
                }

                if (!processes.TryGetValue(current, out var info) || info.Parent == 0 || info.Parent == current)
                {
                    return -1;
                }

                current = info.Parent;
            }

            // Too deep to be real: a PID-reuse cycle.
            return -1;
        }

        var best = 0;
        var bestDepth = -1;
        foreach (var pid in processes.Keys)
        {
            var own = Depth(pid);
            if (own > bestDepth)
            {
                bestDepth = own;
                best = pid;
            }
        }

        if (bestDepth < 0)
        {
            return [];
        }

        // Walk back up from the deepest process to the root, then flip. Depth() already
        // proved this path reaches the root within the cap.
        var chain = new List<string>(bestDepth + 1);
        var node = best;
        for (var steps = 0; steps <= bestDepth; steps++)
        {
            // The root itself may already be gone while its children linger; its name is just left out.
            if (!processes.TryGetValue(node, out var info))
            {
                break;
            }

            chain.Add(info.Name);
            node = info.Parent;
        }

        chain.Reverse();
        return chain;
    }
}
