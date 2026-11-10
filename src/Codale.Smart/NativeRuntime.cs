using System.Runtime.InteropServices;
using System.Text;

namespace Codale.Smart;

/// <summary>
/// Decoding settings. The defaults are IBM's recommendation for Granite 4.2 on every task (also in the GGUF:
/// <c>general.sampling.temp=1.0</c>, <c>general.sampling.top_p=0.95</c>). Temperature 0 decodes greedily.
/// </summary>
/// <param name="Seed">Null draws a random seed; evaluations pin one for reproducible runs.</param>
/// <param name="Stop">Generation ends before the first of these, once visible (non-whitespace) text has been written.</param>
public sealed record SmartGenerateOptions(float Temperature = 1.0f, float TopP = 0.95f, uint? Seed = null, IReadOnlyList<string>? Stop = null)
{
    public static SmartGenerateOptions Default { get; } = new();

    /// <summary>Cuts <paramref name="text"/> at the first stop string after its visible text starts.</summary>
    public string ApplyStops(string text)
    {
        if (Stop is not { Count: > 0 }) return text;
        int visible = 0;
        while (visible < text.Length && char.IsWhiteSpace(text[visible])) visible++;
        int cut = -1;
        foreach (var stop in Stop)
        {
            if (stop.Length == 0) continue;
            int at = text.IndexOf(stop, visible, StringComparison.Ordinal);
            if (at >= 0 && (cut < 0 || at < cut)) cut = at;
        }
        return cut < 0 ? text : text[..cut];
    }
}

public interface ISmartRuntime : IDisposable
{
    string Backend { get; }
    void Configure(SmartModel model) { }
    void Load(string modelPath, bool gpu, CancellationToken ct);
    int CountTokens(string prompt) => Encoding.UTF8.GetByteCount(prompt);
    string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null);
    /// <summary>Generation with sampling and stop strings; runtimes without native support apply the stops afterwards.</summary>
    string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar, SmartGenerateOptions options) =>
        options.ApplyStops(Generate(prompt, maxTokens, ct, grammar));
}

public sealed class NativeRuntime(string nativeDirectory) : ISmartRuntime
{
    private IntPtr _handle;
    public void Configure(SmartModel model) { }
    private static bool _resolved;
    public string Backend { get; private set; } = "CPU";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CancelCallback(IntPtr state);
    [DllImport("codale-smart", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr smart_open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int gpu, CancelCallback cancel, IntPtr state, byte[] error, int errorSize);
    [DllImport("codale-smart", EntryPoint = "smart_generate_v3", CallingConvention = CallingConvention.Cdecl)] private static extern int smart_generate_v3(
        IntPtr runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt, int maxTokens, float temperature, float topP, uint seed,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? grammar, byte[]? stops, CancelCallback cancel, IntPtr state, byte[] output, int capacity, byte[] error, int errorSize);
    [DllImport("codale-smart", CallingConvention = CallingConvention.Cdecl)] private static extern int smart_devices(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string folder, byte[] output, int capacity, byte[] error, int errorSize);
    [DllImport("codale-smart", CallingConvention = CallingConvention.Cdecl)] private static extern void smart_close(IntPtr runtime);

    /// <summary>The backend devices, one per line (type, total MiB, free MiB, description); the GPU the model uses is marked *.</summary>
    public string Devices()
    {
        Resolve();
        var output = new byte[8192]; var error = new byte[1024];
        int length = smart_devices(nativeDirectory, output, output.Length, error, error.Length);
        if (length < 0) throw new InvalidOperationException(Text(error));
        return Encoding.UTF8.GetString(output, 0, length);
    }
    private void Resolve()
    {
        if (_resolved) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeRuntime).Assembly, (name, _, _) => name == "codale-smart"
            ? NativeLibrary.Load(Path.Combine(nativeDirectory, "codale-smart.dll")) : IntPtr.Zero);
        _resolved = true;
    }
    [DllImport("codale-smart", CallingConvention = CallingConvention.Cdecl)] private static extern int smart_token_count(
        IntPtr runtime, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt);

    public int CountTokens(string prompt)
    {
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("Model is not loaded.");
        return smart_token_count(_handle, prompt);
    }

    public void Load(string modelPath, bool gpu, CancellationToken ct)
    {
        Resolve();
        ct.ThrowIfCancellationRequested();
        var error = new byte[1024]; CancelCallback cancel = _ => ct.IsCancellationRequested ? 1 : 0;
        _handle = smart_open(nativeDirectory, modelPath, gpu ? 1 : 0, cancel, IntPtr.Zero, error, error.Length);
        if (ct.IsCancellationRequested) { Dispose(); ct.ThrowIfCancellationRequested(); }
        if (_handle == IntPtr.Zero) throw new InvalidOperationException(Text(error));
        Backend = gpu ? "GPU" : "CPU";
    }
    public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar = null) =>
        Generate(prompt, maxTokens, ct, grammar, SmartGenerateOptions.Default);
    public string Generate(string prompt, int maxTokens, CancellationToken ct, string? grammar, SmartGenerateOptions options)
    {
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("Model is not loaded.");
        var output = new byte[65536]; var error = new byte[1024];
        CancelCallback cancel = _ => ct.IsCancellationRequested ? 1 : 0;
        // Stops cross as a double-NUL-terminated UTF-8 list.
        byte[]? stops = options.Stop is { Count: > 0 } list
            ? Encoding.UTF8.GetBytes(string.Concat(list.Where(s => s.Length > 0).Select(s => s + "\0")) + "\0") : null;
        int length = smart_generate_v3(_handle, prompt, maxTokens, options.Temperature, options.TopP, options.Seed ?? uint.MaxValue,
            grammar, stops, cancel, IntPtr.Zero, output, output.Length, error, error.Length);
        GC.KeepAlive(cancel);
        ct.ThrowIfCancellationRequested();
        if (length < 0) throw new InvalidOperationException(Text(error));
        return Encoding.UTF8.GetString(output, 0, length);
    }
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    public void Dispose() { if (_handle != IntPtr.Zero) { smart_close(_handle); _handle = IntPtr.Zero; } }
}
