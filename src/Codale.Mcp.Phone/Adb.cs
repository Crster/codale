using System.Diagnostics;
using System.Text;

namespace Codale.Mcp.Phone;

/// <summary>Runs Android SDK command-line tools (adb, emulator) and resolves which device a call targets.</summary>
internal sealed class Adb
{
    private const string InstallHint = "Android platform-tools were not found. Install them (Android Studio, or the standalone platform-tools) or set ANDROID_HOME.";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The SDK roots to look in, in order: the environment, then Android Studio's default.</summary>
    private static IEnumerable<string> SdkRoots()
    {
        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root)
            {
                yield return root;
            }
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
    }

    /// <summary>The tool's exe: under an SDK root's <paramref name="subfolder"/>, else on PATH; null when absent.</summary>
    public static string? Find(string tool, string subfolder)
    {
        foreach (var root in SdkRoots())
        {
            var candidate = Path.Combine(root, subfolder, tool + ".exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), tool + ".exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return null;
    }

    private static string AdbPath => Find("adb", "platform-tools") ?? throw new McpToolException(InstallHint);

    /// <summary>Arguments targeting one device: <c>-s serial</c> then the command.</summary>
    public static string[] ForDevice(string serial, params string[] args) => ["-s", serial, .. args];

    /// <summary>Runs adb with the arguments and returns stdout; a non-zero exit becomes an error carrying adb's message.</summary>
    public async Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct, TimeSpan? timeout = null) =>
        Check(await ExecAsync(AdbPath, args, ct, timeout));

    /// <summary>Runs adb and returns raw stdout bytes (screencap).</summary>
    public async Task<byte[]> RunBinaryAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var result = await ExecAsync(AdbPath, args, ct, null, binary: true);
        if (result.Code != 0)
        {
            throw new McpToolException($"adb failed ({result.Code}): {result.Error.Trim()}");
        }

        return result.Bytes;
    }

    /// <summary>Runs a command in the device shell. The command travels as one argument so the device shell parses it.</summary>
    public Task<string> ShellAsync(string serial, string command, CancellationToken ct, TimeSpan? timeout = null) =>
        RunAsync(ForDevice(serial, "shell", command), ct, timeout);

    /// <summary>Runs the SDK emulator tool.</summary>
    public async Task<string> RunEmulatorAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var exe = Find("emulator", "emulator") ?? throw new McpToolException("The Android emulator was not found. Install it with the SDK Manager or set ANDROID_HOME.");
        return Check(await ExecAsync(exe, args, ct, null));
    }

    /// <summary>Starts the emulator for an AVD and returns without waiting for it to exit.</summary>
    public static void StartEmulator(string avd)
    {
        var exe = Find("emulator", "emulator") ?? throw new McpToolException("The Android emulator was not found. Install it with the SDK Manager or set ANDROID_HOME.");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-avd");
        info.ArgumentList.Add(avd);
        Process.Start(info)?.Dispose();
    }

    /// <summary>Online devices from <c>adb devices -l</c>: serial and the descriptive tail.</summary>
    public async Task<IReadOnlyList<(string Serial, string State, string Detail)>> DevicesAsync(CancellationToken ct) =>
        ParseDevices(await RunAsync(["devices", "-l"], ct));

    public static IReadOnlyList<(string Serial, string State, string Detail)> ParseDevices(string output)
    {
        var devices = new List<(string, string, string)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = line.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && !line.StartsWith('*'))
            {
                devices.Add((parts[0], parts[1], parts.Length > 2 ? parts[2] : ""));
            }
        }

        return devices;
    }

    /// <summary>
    /// The serial a call acts on: the one given, or the only ready device. With none or several,
    /// the error says what to do rather than guessing.
    /// </summary>
    public async Task<string> ResolveAsync(string? serial, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(serial))
        {
            return serial.Trim();
        }

        var ready = (await DevicesAsync(ct)).Where(d => d.State == "device").ToList();
        return ready.Count switch
        {
            1 => ready[0].Serial,
            0 => throw new McpToolException("No Android device is connected. Plug one in with USB debugging on, or start an emulator (phone_emulator_start), then check phone_devices."),
            _ => throw new McpToolException("Several devices are connected; pass 'serial' (see phone_devices): " + string.Join(", ", ready.Select(d => d.Serial))),
        };
    }

    /// <summary>
    /// A local path resolved against and confined to the project directory the app passes in
    /// CODALE_PROJECT_DIR, so a prompt cannot make the model push or pull arbitrary local files.
    /// </summary>
    public static string ConfineToProject(string path, string? projectDir = null)
    {
        projectDir ??= Environment.GetEnvironmentVariable("CODALE_PROJECT_DIR");
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            throw new McpToolException("File transfer is unavailable: the project directory (CODALE_PROJECT_DIR) is not set.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDir));
        var full = Path.GetFullPath(path, root);
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new McpToolException($"Only files inside the project directory can be used: {path}");
        }

        return full;
    }

    private static string Check((int Code, string Output, string Error, byte[] Bytes) result)
    {
        if (result.Code != 0)
        {
            var message = (result.Error.Length > 0 ? result.Error : result.Output).Trim();
            throw new McpToolException($"Command failed ({result.Code}): {message}");
        }

        return result.Output;
    }

    private static async Task<(int Code, string Output, string Error, byte[] Bytes)> ExecAsync(
        string exe, IEnumerable<string> args, CancellationToken ct, TimeSpan? timeout, bool binary = false)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = binary ? null : Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new McpToolException($"Could not start {exe}.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);

        try
        {
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            if (binary)
            {
                using var buffer = new MemoryStream();
                await process.StandardOutput.BaseStream.CopyToAsync(buffer, limit.Token);
                await process.WaitForExitAsync(limit.Token);
                return (process.ExitCode, "", await error, buffer.ToArray());
            }

            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            return (process.ExitCode, await output, await error, []);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new McpToolException("The command timed out. Is the device responsive (phone_devices)?");
        }
    }
}
