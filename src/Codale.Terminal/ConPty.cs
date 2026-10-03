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

    public int ProcessId => _process.dwProcessId;

    public bool HasExited =>
        _process.hProcess == IntPtr.Zero ||
        (WaitForSingleObject(_process.hProcess, 0) == 0);

    public void Start(string command, string workingDirectory, short columns, short rows)
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
        startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;

        // Size the attribute list, allocate, then fill it with the pseudoconsole handle.
        var attributeSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);

        _attributeList = Marshal.AllocHGlobal(attributeSize);
        startup.lpAttributeList = _attributeList;

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

        var created = CreateProcessW(
            null,
            new string(command),
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
            IntPtr.Zero,
            workingDirectory,
            ref startup,
            out _process);

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

        if (_process.hProcess != IntPtr.Zero)
        {
            TerminateProcess(_process.hProcess, 0);
            CloseHandle(_process.hProcess);
            CloseHandle(_process.hThread);
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
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
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
}
