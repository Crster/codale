using System.Text;
using System.Text.RegularExpressions;

namespace Codale.Mcp.Phone;

internal static partial class PhoneTools
{
    private const int MaxLogChars = 40_000;

    private static readonly (string, string, string) Serial =
        ("serial", "string", "Optional device serial from phone_devices. Omit when exactly one device is connected.");

    private static readonly (string, string, string) X = ("x", "number", "Horizontal position in device pixels, as measured on the latest phone_screenshot.");
    private static readonly (string, string, string) Y = ("y", "number", "Vertical position in device pixels, as measured on the latest phone_screenshot.");
    private static readonly (string, string, string) Package = ("package", "string", "Android application id, e.g. com.example.app.");

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackagePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex WordPattern();

    private static string RequirePackage(McpArgs a) =>
        a.RequiredString("package") is var p && PackagePattern().IsMatch(p)
            ? p
            : throw new McpToolException($"'{a.String("package")}' is not an application id (like com.example.app).");

    private static string RequireWord(string value, string what) =>
        WordPattern().IsMatch(value) ? value : throw new McpToolException($"'{value}' is not a valid {what}.");

    private static Task<IReadOnlyList<McpContent>> Done(string text) => McpTool.Text(text);

    private static IEnumerable<(string, string, string)> With(params (string, string, string)[] props) => [Serial, .. props];

    public static IReadOnlyList<McpTool> Create(Adb adb)
    {
        // Resolves the target device, then runs the handler against it.
        static Func<McpArgs, CancellationToken, Task<IReadOnlyList<McpContent>>> OnDevice(
            Adb adb, Func<string, McpArgs, CancellationToken, Task<IReadOnlyList<McpContent>>> run) =>
            async (a, ct) => await run(await adb.ResolveAsync(a.String("serial"), ct), a, ct);

        static async Task<IReadOnlyList<McpContent>> Text(Task<string> output) => [McpContent.FromText(await output)];

        return
        [
            new("phone_devices",
                "List connected Android devices and emulators (serial, state, model) and the emulator AVDs that can be started. Start here; a device must show state 'device' to be usable ('unauthorized' means accept the USB debugging prompt on the phone).",
                McpTool.Schema(),
                async (_, ct) =>
                {
                    var sb = new StringBuilder();
                    var devices = await adb.DevicesAsync(ct);
                    sb.AppendLine("Devices:");
                    foreach (var (serial, state, detail) in devices)
                    {
                        sb.AppendLine($"  {serial}  {state}  {detail}".TrimEnd());
                    }

                    if (devices.Count == 0)
                    {
                        sb.AppendLine("  (none)");
                    }

                    try
                    {
                        var avds = (await adb.RunEmulatorAsync(["-list-avds"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        sb.AppendLine("Emulator AVDs: " + (avds.Length == 0 ? "(none)" : string.Join(", ", avds)));
                    }
                    catch (McpToolException)
                    {
                        sb.AppendLine("Emulator AVDs: (emulator tool not found)");
                    }

                    return [McpContent.FromText(sb.ToString().TrimEnd())];
                }),

            new("phone_connect",
                "Connect a real phone over Wi-Fi (Wireless debugging). First time: action 'pair' with the pairing address and 6-digit code shown under Developer options > Wireless debugging > Pair device with pairing code. Then action 'connect' with the IP:port shown on the Wireless debugging screen (a different port than pairing). 'disconnect' drops it. After connecting it appears in phone_devices.",
                McpTool.Schema(
                    [
                        ("action", "string", "pair, connect or disconnect."),
                        ("address", "string", "IP:port, e.g. 192.168.1.20:37215. For pair, the pairing address; for connect, the connection address."),
                        ("code", "string", "The 6-digit pairing code (pair only)."),
                    ],
                    "action", "address"),
                async (a, ct) =>
                {
                    var action = a.RequiredKeyword("action");
                    var address = a.RequiredString("address").Trim();
                    if (!Regex.IsMatch(address, @"^[A-Za-z0-9.\-]+:\d{1,5}$"))
                    {
                        throw new McpToolException($"'{address}' is not an address like 192.168.1.20:37215.");
                    }

                    string output;
                    switch (action)
                    {
                        case "pair":
                            var code = a.RequiredString("code").Trim();
                            if (!Regex.IsMatch(code, @"^\d{6}$"))
                            {
                                throw new McpToolException("The pairing code is the 6 digits shown on the phone.");
                            }

                            output = await adb.RunAsync(["pair", address, code], ct);
                            break;
                        case "connect":
                            output = await adb.RunAsync(["connect", address], ct);
                            break;
                        case "disconnect":
                            output = await adb.RunAsync(["disconnect", address], ct);
                            break;
                        default:
                            throw new McpToolException("action must be pair, connect or disconnect.");
                    }

                    // adb exits 0 even when the phone refuses, so the outcome is in the text.
                    var text = output.Trim();
                    if (action != "disconnect" && !text.Contains("Successfully", StringComparison.OrdinalIgnoreCase) && !text.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) || text.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new McpToolException(text.Length > 0 ? text : "adb gave no answer; check the address and that Wireless debugging is on.");
                    }

                    return [McpContent.FromText(text.Length > 0 ? text : "Done.")];
                }),

            new("phone_screenshot",
                "Capture the Android screen as a PNG. This is how you see the app. ALWAYS take one (or phone_ui_tree) before phone_tap / phone_swipe: coordinates are device pixels on the latest screenshot.",
                McpTool.Schema(With()),
                OnDevice(adb, async (s, _, ct) =>
                {
                    await adb.EnsureAwakeAsync(s, ct);
                    var png = Adb.TrimToPng(await adb.RunBinaryAsync(Adb.ForDevice(s, "exec-out", "screencap", "-p"), ct));
                    if (png.Length < 8 || png[1] != 0x50)
                    {
                        throw new McpToolException("The device returned no screenshot (the screen may be locked or showing a secure window).");
                    }

                    return [McpContent.FromImage(png)];
                })),

            new("phone_ui_tree",
                "Read the current screen as text: one line per element with its text, content description, resource id and tap center (x,y). Cheaper and more precise than a screenshot for finding what to tap; pair with phone_tap using the center.",
                McpTool.Schema(With()),
                OnDevice(adb, async (s, _, ct) =>
                {
                    // /dev/tty is unreliable on recent Android; dump to a file and read it back, retrying while the UI settles.
                    await adb.EnsureAwakeAsync(s, ct);
                    const string dump ="uiautomator dump /sdcard/codale_window.xml >/dev/null 2>&1; cat /sdcard/codale_window.xml";
                    var xml = "";
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        xml = (await adb.TryShellAsync(s, dump, ct)).Output;
                        if (xml.Contains("<hierarchy", StringComparison.Ordinal))
                        {
                            break;
                        }

                        await Task.Delay(700, ct);
                    }

                    if (!xml.Contains("<hierarchy", StringComparison.Ordinal))
                    {
                        throw new McpToolException("The device returned no UI hierarchy (the screen may be off, locked or animating). Try phone_screenshot instead.");
                    }

                    return [McpContent.FromText(UiTree.Parse(xml))];
                })),

            new("phone_tap", "Tap the screen at a position in device pixels.",
                McpTool.Schema(With(X, Y), "x", "y"),
                OnDevice(adb, async (s, a, ct) =>
                {
                    await adb.ShellAsync(s, $"input tap {a.RequiredInt("x")} {a.RequiredInt("y")}", ct);
                    return await Done("Tapped.");
                })),

            new("phone_long_press", "Press and hold at a position in device pixels (context menus, drag handles).",
                McpTool.Schema(With(X, Y, ("duration_ms", "number", "Hold time in milliseconds (default 800).")), "x", "y"),
                OnDevice(adb, async (s, a, ct) =>
                {
                    int x = a.RequiredInt("x"), y = a.RequiredInt("y");
                    await adb.ShellAsync(s, $"input swipe {x} {y} {x} {y} {Math.Clamp(a.Int("duration_ms") ?? 800, 100, 10_000)}", ct);
                    return await Done("Long-pressed.");
                })),

            new("phone_swipe", "Swipe from one position to another in device pixels: scroll lists, pull to refresh, page between screens. To scroll down, swipe upward.",
                McpTool.Schema(With(("x1", "number", "Start X."), ("y1", "number", "Start Y."), ("x2", "number", "End X."), ("y2", "number", "End Y."),
                    ("duration_ms", "number", "Swipe time in milliseconds (default 300).")), "x1", "y1", "x2", "y2"),
                OnDevice(adb, async (s, a, ct) =>
                {
                    await adb.ShellAsync(s, $"input swipe {a.RequiredInt("x1")} {a.RequiredInt("y1")} {a.RequiredInt("x2")} {a.RequiredInt("y2")} {Math.Clamp(a.Int("duration_ms") ?? 300, 50, 10_000)}", ct);
                    return await Done("Swiped.");
                })),

            new("phone_type", "Type text into the focused field (tap the field first). Single line and ASCII only; use phone_key ENTER to submit.",
                McpTool.Schema(With(("text", "string", "The text to type.")), "text"),
                OnDevice(adb, async (s, a, ct) =>
                {
                    await adb.ShellAsync(s, "input text " + InputText.Escape(a.RequiredString("text")), ct);
                    return await Done("Typed.");
                })),

            new("phone_key", "Press a hardware or navigation key: HOME, BACK, ENTER, APP_SWITCH, TAB, DEL, VOLUME_UP, POWER, DPAD_DOWN, or a numeric keycode. Name without the KEYCODE_ prefix.",
                McpTool.Schema(With(("key", "string", "Key name, e.g. BACK, or a numeric keycode.")), "key"),
                OnDevice(adb, async (s, a, ct) =>
                {
                    var key = RequireWord(a.RequiredString("key").Trim().ToUpperInvariant(), "key name");
                    await adb.ShellAsync(s, "input keyevent " + (int.TryParse(key, out _) || key.StartsWith("KEYCODE_") ? key : "KEYCODE_" + key), ct);
                    return await Done("Pressed.");
                })),

            new("phone_launch_app", "Launch an installed app by application id (its launcher activity, or a specific activity).",
                McpTool.Schema(With(Package, ("activity", "string", "Optional activity, e.g. .MainActivity, to start instead of the launcher activity.")), "package"),
                OnDevice(adb, async (s, a, ct) =>
                {
                    var pkg = RequirePackage(a);
                    await adb.EnsureAwakeAsync(s, ct);
                    var activity = a.String("activity");
                    if (activity is { Length: > 0 })
                    {
                        if (!Regex.IsMatch(activity, @"^[A-Za-z0-9_.$]+$"))
                        {
                            throw new McpToolException($"'{activity}' is not an activity name.");
                        }

                        return await Text(adb.ShellAsync(s, $"am start -n {pkg}/{activity}", ct));
                    }

                    return await Text(adb.ShellAsync(s, $"monkey -p {pkg} -c android.intent.category.LAUNCHER 1", ct));
                })),

            new("phone_stop_app", "Force-stop an app (cold start on the next launch).",
                McpTool.Schema(With(Package), "package"),
                OnDevice(adb, async (s, a, ct) =>
                {
                    await adb.ShellAsync(s, "am force-stop " + RequirePackage(a), ct);
                    return await Done("Stopped.");
                })),

            new("phone_current_app", "Report which app and activity is in the foreground.",
                McpTool.Schema(With()),
                OnDevice(adb, async (s, _, ct) =>
                {
                    foreach (var command in new[]
                    {
                        "dumpsys window | grep -E 'mCurrentFocus|mFocusedApp'",
                        "dumpsys activity activities | grep -E 'topResumedActivity|ResumedActivity'",
                    })
                    {
                        var (_, output) = await adb.TryShellAsync(s, command, ct);
                        if (output.Contains("null", StringComparison.Ordinal) && !output.Contains('/'))
                        {
                            continue;
                        }

                        if (output.Trim().Length > 0)
                        {
                            return [McpContent.FromText(output.TrimEnd())];
                        }
                    }

                    return [McpContent.FromText("No foreground app found (the screen may be off or locked).")];
                })),

            new("phone_logcat",
                "Read recent device logs (logcat), newest last. Filter by app (its process), tag and minimum priority to find a crash or your own log lines. Check it after launching a freshly installed build.",
                McpTool.Schema(With(
                    ("package", "string", "Only lines from this app's running process."),
                    ("tag", "string", "Only this log tag."),
                    ("priority", "string", "Minimum priority: V, D, I, W (default), E or F."),
                    ("lines", "number", "How many recent lines to read (default 200)."),
                    ("clear", "boolean", "Clear the log buffer after reading."))),
                OnDevice(adb, async (s, a, ct) =>
                {
                    var priority = (a.String("priority") ?? "W").Trim().ToUpperInvariant();
                    if (priority.Length != 1 || "VDIWEF".IndexOf(priority[0]) < 0)
                    {
                        throw new McpToolException("priority must be one of V, D, I, W, E, F.");
                    }

                    var command = new StringBuilder($"logcat -d -t {Math.Clamp(a.Int("lines") ?? 200, 1, 5000)}");
                    if (a.String("package") is { Length: > 0 })
                    {
                        _ = RequirePackage(a);
                        var pid = (await adb.TryShellAsync(s, $"pidof {a.String("package")}", ct)).Output.Trim().Split(' ')[0];
                        if (pid.Length == 0)
                        {
                            return await Done($"{a.String("package")} is not running, so it has no log lines. Launch it first (phone_launch_app).");
                        }

                        command.Append(" --pid=").Append(RequireWord(pid, "process id"));
                    }

                    command.Append(a.String("tag") is { Length: > 0 } tag ? $" {RequireWord(tag, "tag")}:{priority} *:S" : $" *:{priority}");
                    var log = await adb.ShellAsync(s, command.ToString(), ct);
                    if (a.Bool("clear") == true)
                    {
                        await adb.ShellAsync(s, "logcat -c", ct);
                    }

                    if (log.Length > MaxLogChars)
                    {
                        log = "...(older lines cut)\n" + log[^MaxLogChars..];
                    }

                    return await Done(log.Length == 0 ? "(no matching log lines)" : log.TrimEnd());
                })),

            new("phone_port_forward",
                "Connect ports between the phone and this computer. direction 'reverse' lets the phone reach a dev server on this machine at localhost:PORT (Metro 8081, Flutter, Vite); 'forward' exposes a phone port here. Also 'list' and 'remove'.",
                McpTool.Schema(With(
                    ("direction", "string", "reverse (phone -> computer), forward (computer -> phone), or list."),
                    ("local_port", "number", "Port on the receiving side of the connection."),
                    ("remote_port", "number", "Port on the other side (default: same as local_port)."),
                    ("remove", "boolean", "Remove this mapping instead of adding it.")), "direction"),
                OnDevice(adb, async (s, a, ct) =>
                {
                    var direction = a.RequiredKeyword("direction");
                    if (direction is not ("reverse" or "forward" or "list"))
                    {
                        throw new McpToolException("direction must be reverse, forward or list.");
                    }

                    if (direction == "list")
                    {
                        var reverse = await adb.RunAsync(Adb.ForDevice(s, "reverse", "--list"), ct);
                        var forward = await adb.RunAsync(Adb.ForDevice(s, "forward", "--list"), ct);
                        return [McpContent.FromText($"reverse:\n{reverse.Trim()}\nforward:\n{forward.Trim()}")];
                    }

                    var local = a.RequiredInt("local_port");
                    var remote = a.Int("remote_port") ?? local;
                    if (a.Bool("remove") == true)
                    {
                        await adb.RunAsync(Adb.ForDevice(s, direction, "--remove", $"tcp:{local}"), ct);
                        return await Done("Removed.");
                    }

                    // adb reverse DEVICE_PORT HOST_PORT; adb forward HOST_PORT DEVICE_PORT.
                    await adb.RunAsync(Adb.ForDevice(s, direction, $"tcp:{local}", $"tcp:{remote}"), ct);
                    return await Done(direction == "reverse"
                        ? $"The phone's localhost:{local} now reaches this computer's port {remote}."
                        : $"This computer's localhost:{local} now reaches the phone's port {remote}.");
                })),

            new("phone_install", "Install (or reinstall, keeping data) an .apk from the project folder on the device. Build it first, e.g. with gradlew assembleDebug; for a gradle project 'gradlew installDebug' builds and installs in one step through the shell.",
                McpTool.Schema(With(("apk", "string", "Path to the .apk inside the project directory.")), "apk"),
                OnDevice(adb, async (s, a, ct) =>
                {
                    var apk = Adb.ConfineToProject(a.RequiredString("apk"));
                    if (!File.Exists(apk))
                    {
                        throw new McpToolException($"No such file: {apk}");
                    }

                    return await Text(adb.RunAsync(Adb.ForDevice(s, "install", "-r", "-t", apk), ct, TimeSpan.FromMinutes(5)));
                })),

            new("phone_uninstall", "Uninstall an app (and its data) from the device.",
                McpTool.Schema(With(Package), "package"),
                OnDevice(adb, (s, a, ct) => Text(adb.RunAsync(Adb.ForDevice(s, "uninstall", RequirePackage(a)), ct)))),

            new("phone_clear_data", "Clear an app's stored data and cache (like a fresh install without reinstalling).",
                McpTool.Schema(With(Package), "package"),
                OnDevice(adb, (s, a, ct) => Text(adb.ShellAsync(s, "pm clear " + RequirePackage(a), ct)))),

            new("phone_push", "Copy a file from the project folder to the device.",
                McpTool.Schema(With(("local", "string", "Source path inside the project directory."), ("remote", "string", "Destination path on the device, e.g. /sdcard/Download/")), "local", "remote"),
                OnDevice(adb, (s, a, ct) =>
                    Text(adb.RunAsync(Adb.ForDevice(s, "push", Adb.ConfineToProject(a.RequiredString("local")), a.RequiredString("remote")), ct, TimeSpan.FromMinutes(5))))),

            new("phone_pull", "Copy a file from the device into the project folder (e.g. a screenshot or database to inspect).",
                McpTool.Schema(With(("remote", "string", "Source path on the device."), ("local", "string", "Destination path inside the project directory.")), "remote", "local"),
                OnDevice(adb, (s, a, ct) =>
                    Text(adb.RunAsync(Adb.ForDevice(s, "pull", a.RequiredString("remote"), Adb.ConfineToProject(a.RequiredString("local"))), ct, TimeSpan.FromMinutes(5))))),

            new("phone_shell", "Run any command in the device shell (adb shell). For things the other tools do not cover: settings, pm, am, dumpsys, getprop.",
                McpTool.Schema(With(("command", "string", "The shell command to run on the device.")), "command"),
                OnDevice(adb, (s, a, ct) => Text(adb.ShellAsync(s, a.RequiredString("command"), ct)))),

            new("phone_emulator_start", "Start an Android emulator AVD (names from phone_devices) and wait until it has booted. Takes up to two minutes.",
                McpTool.Schema([("avd", "string", "AVD name.")], "avd"),
                async (a, ct) =>
                {
                    // AVD names may contain dots and dashes (Medium_Phone_API_37.0); they are passed as one argument, never through a shell.
                    var avd = a.RequiredString("avd").Trim();
                    if (!Regex.IsMatch(avd, @"^[A-Za-z0-9_.\-]+$"))
                    {
                        throw new McpToolException($"'{avd}' is not a valid AVD name.");
                    }
                    static IEnumerable<string> EmulatorSerials(IEnumerable<(string Serial, string State, string Detail)> devices) =>
                        devices.Where(d => d.Serial.StartsWith("emulator-", StringComparison.Ordinal)).Select(d => d.Serial);

                    var before = EmulatorSerials(await adb.DevicesAsync(ct)).ToHashSet();

                    // Already running this AVD: just wait for it.
                    string? serial = null;
                    foreach (var running in before)
                    {
                        if (await adb.AvdNameAsync(running, ct) == avd)
                        {
                            serial = running;
                            break;
                        }
                    }

                    var launch = serial is null ? Adb.StartEmulator(avd) : null;

                    var deadline = DateTime.UtcNow.AddMinutes(3);
                    while (DateTime.UtcNow < deadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (launch?.FailureMessage() is { } failure)
                        {
                            throw new McpToolException(failure);
                        }

                        try
                        {
                            var devices = await adb.DevicesAsync(ct);
                            serial ??= EmulatorSerials(devices).FirstOrDefault(x => !before.Contains(x));
                            if (serial is not null
                                && devices.Any(d => d.Serial == serial && d.State == "device")
                                && (await adb.TryShellAsync(serial, "getprop sys.boot_completed", ct, TimeSpan.FromSeconds(10))).Output.Trim() == "1")
                            {
                                return [McpContent.FromText($"Emulator booted: {serial}")];
                            }
                        }
                        catch (McpToolException)
                        {
                            // adb or the device is not ready yet; keep polling.
                        }

                        await Task.Delay(2000, ct);
                    }

                    throw new McpToolException("The emulator did not finish booting in three minutes; check phone_devices later." +
                        (launch?.Output is { Length: > 0 } tail ? "\nEmulator output:\n" + tail : ""));
                }),
        ];
    }
}
