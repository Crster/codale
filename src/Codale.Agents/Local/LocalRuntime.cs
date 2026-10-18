using LLama.Native;

namespace Codale.Agents.Local;

/// <summary>
/// Points LLamaSharp at the CUDA 13 llama.cpp build when this machine can run it, else leaves it on the CPU.
/// LLamaSharp's own CUDA detection needs the CUDA toolkit installed, which end users do not have, so the
/// library is chosen here instead.
/// </summary>
internal static class LocalRuntime
{
    /// <summary>The CUDA 13 runtime DLLs the backend package does not ship: set <c>CODALE_CUDA_DIR</c> to a folder holding them.</summary>
    public const string CudaDirVariable = "CODALE_CUDA_DIR";

    private static readonly string[] RuntimeDlls = ["cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll"];

    private static bool _configured;

    public static void Configure()
    {
        if (_configured)
        {
            return;
        }

        _configured = true;
        var backend = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "cuda13");
        var llama = Path.Combine(backend, "llama.dll");
        if (!File.Exists(llama) || !File.Exists(Path.Combine(backend, "ggml-cpu.dll")))
        {
            return;
        }

        var runtimeDir = new[] { backend, Environment.GetEnvironmentVariable(CudaDirVariable) }
            .FirstOrDefault(dir => !string.IsNullOrWhiteSpace(dir) && RuntimeDlls.All(dll => File.Exists(Path.Combine(dir, dll))));
        if (runtimeDir is null)
        {
            return;
        }

        // The backend's DLLs find the CUDA runtime through the process search path.
        Environment.SetEnvironmentVariable("PATH", runtimeDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        NativeLibraryConfig.All.WithLibrary(llama, null);
    }
}
