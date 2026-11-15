using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Codale.Terminal;

/// <summary>
/// A Windows pseudoconsole hosting one child process.
/// </summary>
/// <remarks>
/// Written by hand rather than generated: the surface is eight functions, but the
/// STARTUPINFOEX / proc-thread attribute-list dance is the part that actually goes wrong,
/// and doing it explicitly keeps the lifetime rules visible. Handle order matters - the
/// pseudoconsole must be closed before the pipes, or the reader blocks forever.
/// </remarks>
internal sealed partial class ConPty : IDisposable
{
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    private IntPtr _pseudoConsole;
    private IntPtr _attributeList;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private SafeFileHandle? _childInputRead;
    private SafeFileHandle? _childOutputWrite;
    private PROCESS_INFORMATION _process;
    private bool _disposed;

    public Stream Input { get; private set; } = Stream.Null;

    public Stream Output { get; private set; } = Stream.Null;

    public int ProcessId => _process.DwProcessId;

    public bool HasExited =>
        _process.HProcess == IntPtr.Zero ||
        (WaitForSingleObject(_process.HProcess, 0) == 0);

    /// <summary>
    /// True while a live process names the shell as its parent: the command a wrapped shell
    /// launched, or whatever is running at its prompt. The shell itself outlives a command it
    /// was started with, so being alive alone does not mean anything is still running in it.
    /// </summary>
    /// <remarks>
    /// The pseudoconsole host is a child of the calling app, not of the shell, so an idle shell
    /// has none. A parent id can be stale - an orphan whose dead parent's id was later reused by
    /// this shell - so a candidate started before the shell does not count.
    /// </remarks>
    public bool HasChildProcesses()
    {
        if (HasExited)
        {
            return false;
        }

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        try
        {
            var entry = new PROCESSENTRY32W { DwSize = Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                if (entry.Th32ParentProcessID == ProcessId && StartedAfterShell(entry.Th32ProcessID))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private bool StartedAfterShell(int childId)
    {
        try
        {
            using var shell = System.Diagnostics.Process.GetProcessById(ProcessId);
            using var child = System.Diagnostics.Process.GetProcessById(childId);
            return child.StartTime >= shell.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Gone already means it is not running; one we may not inspect still counts - asking
            // once too often beats silently killing a running command.
            return ex is Win32Exception;
        }
    }

    /// <summary>
    /// Starts <paramref name="command"/> in a new pseudoconsole. With <paramref name="environment"/>
    /// the child gets exactly those variables instead of inheriting this process's.
    /// </summary>
    public void Start(string command, string workingDirectory, short columns, short rows,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        // Two pipes: one each direction. The child gets the far ends.
        if (!CreatePipe(out _childInputRead, out _inputWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out _outputRead, out _childOutputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the pseudoconsole pipes.");
        }

        var size = new COORD { X = columns, Y = rows };
        var hr = CreatePseudoConsole(size, _childInputRead, _childOutputWrite, 0, out _pseudoConsole);
        if (hr != 0)
        {
            throw new Win32Exception(hr, "CreatePseudoConsole failed.");
        }

        var startup = new STARTUPINFOEX();
        startup.StartupInfo.Cb = Marshal.SizeOf<STARTUPINFOEX>();
        startup.StartupInfo.DwFlags = STARTF_USESTDHANDLES;

        // Size the attribute list, allocate, then fill it with the pseudoconsole handle.
        var attributeSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);

        _attributeList = Marshal.AllocHGlobal(attributeSize);
        startup.LpAttributeList = _attributeList;

        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref attributeSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
        }

        if (!UpdateProcThreadAttribute(
                _attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
        }

        var environmentBlock = environment is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(BuildEnvironmentBlock(environment));
        bool created;
        try
        {
            created = CreateProcessW(
                null,
                new string(command),
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                environmentBlock,
                workingDirectory,
                ref startup,
                out _process);
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }

        if (!created)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start '{command}'.");
        }

        // The child owns its ends now; holding them open would keep the read side alive
        // after the child exits and the output reader would never see EOF.
        _childInputRead.Dispose();
        _childOutputWrite.Dispose();
        _childInputRead = null;
        _childOutputWrite = null;

        // Unbuffered: a keystroke is a few bytes and must reach the shell immediately;
        // reads already use their own 8K buffer, so FileStream's would only add a copy.
        Input = new FileStream(_inputWrite!, FileAccess.Write, bufferSize: 0);
        Output = new FileStream(_outputRead!, FileAccess.Read, bufferSize: 0);
    }

    /// <summary>
    /// The Unicode environment block CreateProcess takes: "NAME=value\0" entries sorted
    /// case-insensitively (Windows requires the order), ended by one more NUL. The string marshaller
    /// adds the final terminator, so the text carries every entry's own NUL plus one.
    /// </summary>
    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new System.Text.StringBuilder();
        foreach (var (name, value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            // A name with '=' past its first character, or a NUL anywhere, would corrupt the block.
            if (name.Length == 0 || name.IndexOf('=', 1) >= 0 || name.Contains('\0') || value.Contains('\0'))
            {
                continue;
            }

            block.Append(name).Append('=').Append(value).Append('\0');
        }

        if (block.Length == 0)
        {
            block.Append('\0');
        }

        return block.ToString();
    }

    public void Resize(short columns, short rows)
    {
        if (_pseudoConsole != IntPtr.Zero)
        {
            ResizePseudoConsole(_pseudoConsole, new COORD { X = columns, Y = rows });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing the pseudoconsole first signals the child and releases the reader.
        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        if (_process.HProcess != IntPtr.Zero)
        {
            TerminateProcess(_process.HProcess, 0);
            CloseHandle(_process.HProcess);
            CloseHandle(_process.HThread);
            _process = default;
        }

        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }

        Input.Dispose();
        Output.Dispose();
        _childInputRead?.Dispose();
        _childOutputWrite?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int Cb;
        public IntPtr LpReserved;
        public IntPtr LpDesktop;
        public IntPtr LpTitle;
        public int DwX;
        public int DwY;
        public int DwXSize;
        public int DwYSize;
        public int DwXCountChars;
        public int DwYCountChars;
        public int DwFillAttribute;
        public int DwFlags;
        public short WShowWindow;
        public short CbReserved2;
        public IntPtr LpReserved2;
        public IntPtr HStdInput;
        public IntPtr HStdOutput;
        public IntPtr HStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr LpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr HProcess;
        public IntPtr HThread;
        public int DwProcessId;
        public int DwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CreatePseudoConsole(
        COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void ClosePseudoConsole(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int ResizePseudoConsole(IntPtr handle, COORD size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(
        out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value,
        IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void DeleteProcThreadAttributeList(IntPtr attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        int creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(IntPtr process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = -1;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PROCESSENTRY32W
    {
        public int DwSize;
        public int CntUsage;
        public int Th32ProcessID;
        public IntPtr Th32DefaultHeapID;
        public int Th32ModuleID;
        public int CntThreads;
        public int Th32ParentProcessID;
        public int PcPriClassBase;
        public int DwFlags;
        public fixed char SzExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateToolhelp32Snapshot(uint flags, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
}