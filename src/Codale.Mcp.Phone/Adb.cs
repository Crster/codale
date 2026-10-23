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

    /// <summary>Like <see cref="ShellAsync"/> but a non-zero exit is returned, not thrown (pidof, grep and friends use it for "no match").</summary>
    public async Task<(int Code, string Output)> TryShellAsync(string serial, string command, CancellationToken ct, TimeSpan? timeout = null)
    {
        var result = await ExecAsync(AdbPath, ForDevice(serial, "shell", command), ct, timeout);
        return (result.Code, result.Output);
    }

    /// <summary>
    /// Starts the emulator for an AVD and returns without waiting for it to exit. Its output must stay
    /// redirected (it would otherwise share this server's stdout, the protocol channel) and be drained,
    /// or a full pipe stalls the emulator.
    /// </summary>
    public static EmulatorLaunch StartEmulator(string avd)
    {
        var exe = Find("emulator", "emulator") ?? throw new McpToolException("The Android emulator was not found. Install it with the SDK Manager or set ANDROID_HOME.");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-avd");
        info.ArgumentList.Add(avd);
        var process = Process.Start(info) ?? throw new McpToolException($"Could not start {exe}.");
        var launch = new EmulatorLaunch(process);
        process.OutputDataReceived += (_, e) => launch.Add(e.Data);
        process.ErrorDataReceived += (_, e) => launch.Add(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return launch;
    }

    /// <summary>A started emulator process and the tail of what it printed, so a failed start can report the emulator's own message.</summary>
    public sealed class EmulatorLaunch(Process process)
    {
        private readonly Queue<string> _lines = new();

        public Process Process { get; } = process;

        public void Add(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (_lines)
            {
                _lines.Enqueue(line.Trim());
                while (_lines.Count > 15)
                {
                    _lines.Dequeue();
                }
            }
        }

        public string Output
        {
            get
            {
                lock (_lines)
                {
                    return string.Join("\n", _lines);
                }
            }
        }

        /// <summary>The emulator's message when it died with an error; null while it runs or after a clean exit.</summary>
        public string? FailureMessage()
        {
            if (!Process.HasExited || Process.ExitCode == 0)
            {
                return null;
            }

            Process.WaitForExit(500);
            var output = Output;
            return $"The emulator exited with code {Process.ExitCode}" + (output.Length > 0 ? ":\n" + output : ".");
        }
    }

    /// <summary>Wakes the screen (and dismisses a swipe-only lock screen) when the device is asleep; a sleeping screen gives black screenshots and no UI tree.</summary>
    public async Task EnsureAwakeAsync(string serial, CancellationToken ct)
    {
        var (_, power) = await TryShellAsync(serial, "dumpsys power | grep -m1 mWakefulness=", ct, TimeSpan.FromSeconds(15));
        if (power.Contains("Awake", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await TryShellAsync(serial, "input keyevent KEYCODE_WAKEUP; wm dismiss-keyguard", ct, TimeSpan.FromSeconds(15));
        await Task.Delay(800, ct);
    }

    /// <summary>The AVD name a running emulator serves, or null when it cannot be read.</summary>
    public async Task<string?> AvdNameAsync(string serial, CancellationToken ct)
    {
        try
        {
            var result = await ExecAsync(AdbPath, ForDevice(serial, "emu", "avd", "name"), ct, TimeSpan.FromSeconds(10));
            return result.Code == 0 ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() : null;
        }
        catch (McpToolException)
        {
            return null;
        }
    }

    /// <summary>The PNG inside screencap output, without any leading adb/shell noise; the input when no signature is found.</summary>
    public static byte[] TrimToPng(byte[] data)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var at = data.AsSpan().IndexOf(signature);
        return at <= 0 ? data : data[at..];
    }

    /// <summary>Online devices from <c>adb devices -l</c>: serial and the descriptive tail.</summary>
    public async Task<IReadOnlyList<(string Serial, string State, string Detail)>> DevicesAsync(CancellationToken ct) =>
        DropDuplicateWireless(ParseDevices(await RunAsync(["devices", "-l"], ct)));

    /// <summary>
    /// A wirelessly paired phone is listed twice: by IP:port and by its mDNS name (<c>adb-ID._adb-tls-connect._tcp</c>).
    /// Keeps the IP:port entry and drops the mDNS twin (same product/model/device) so the phone counts once.
    /// </summary>
    public static IReadOnlyList<(string Serial, string State, string Detail)> DropDuplicateWireless(IReadOnlyList<(string Serial, string State, string Detail)> devices)
    {
        static string Identity(string detail) =>
            string.Join(' ', detail.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("transport_id:", StringComparison.Ordinal)));

        var direct = devices
            .Where(d => !d.Serial.Contains("._adb-tls-", StringComparison.Ordinal) && d.Serial.Contains(':'))
            .Select(d => Identity(d.Detail))
            .ToHashSet();

        return devices.Where(d => !d.Serial.Contains("._adb-tls-", StringComparison.Ordinal) || !direct.Contains(Identity(d.Detail))).ToList();
    }

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
            0 => throw new McpToolException("No Android device is connected. Plug one in with USB debugging on, pair it over Wi-Fi (phone_connect), or start an emulator (phone_emulator_start), then check phone_devices."),
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
