using System.Runtime.InteropServices;
using System.Text;

namespace Codale.Smart;

public interface ISmartRuntime : IDisposable
{
    string Backend { get; }
    void Load(string modelPath, bool gpu, CancellationToken ct);
    int CountTokens(string prompt) => Encoding.UTF8.GetByteCount(prompt);
    string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null);
}

public sealed class NativeRuntime(string nativeDirectory) : ISmartRuntime
{
    private IntPtr _handle;
    private static bool _resolved;
    public string Backend { get; private set; } = "CPU";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CancelCallback(IntPtr state);
    [DllImport("codale-smart", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr smart_open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int gpu, CancelCallback cancel, IntPtr state, byte[] error, int errorSize);
    [DllImport("codale-smart", CallingConvention = CallingConvention.Cdecl)] private static extern int smart_generate(
        IntPtr runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt, int maxTokens,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? grammar, CancelCallback cancel, IntPtr state, byte[] output, int capacity, byte[] error, int errorSize);
    [DllImport("codale-smart", CallingConvention = CallingConvention.Cdecl)] private static extern void smart_close(IntPtr runtime);
    [DllImport("codale-smart", CallingConvention = CallingConvention.Cdecl)] private static extern int smart_token_count(
        IntPtr runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt);

    public int CountTokens(string prompt)
    {
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("Model is not loaded.");
        return smart_token_count(_handle, prompt);
    }

    public void Load(string modelPath, bool gpu, CancellationToken ct)
    {
        if (!_resolved)
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeRuntime).Assembly, (name, _, _) => name == "codale-smart"
                ? NativeLibrary.Load(Path.Combine(nativeDirectory, "codale-smart.dll")) : IntPtr.Zero);
            _resolved = true;
        }
        ct.ThrowIfCancellationRequested();
        var error = new byte[1024]; CancelCallback cancel = _ => ct.IsCancellationRequested ? 1 : 0;
        _handle = smart_open(nativeDirectory, modelPath, gpu ? 1 : 0, cancel, IntPtr.Zero, error, error.Length);
        if (ct.IsCancellationRequested) { Dispose(); ct.ThrowIfCancellationRequested(); }
        if (_handle == IntPtr.Zero) throw new InvalidOperationException(Text(error));
        Backend = gpu ? "GPU" : "CPU";
    }
    public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null)
    {
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("Model is not loaded.");
        var output = new byte[65536]; var error = new byte[1024];
        CancelCallback cancel = _ => ct.IsCancellationRequested ? 1 : 0;
        int length = smart_generate(_handle, prompt, maxTokens, grammar, cancel, IntPtr.Zero, output, output.Length, error, error.Length);
        ct.ThrowIfCancellationRequested();
        if (length < 0) throw new InvalidOperationException(Text(error));
        return Encoding.UTF8.GetString(output, 0, length);
    }
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    public void Dispose() { if (_handle != IntPtr.Zero) { smart_close(_handle); _handle = IntPtr.Zero; } }
}
