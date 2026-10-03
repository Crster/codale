using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace Codale.Mcp.Computer;

/// <summary>
/// Screen capture and synthetic input over the whole virtual desktop. Coordinates the
/// model sees are those of the last screenshot; <see cref="_scale"/> maps them back to
/// real pixels when the capture was downscaled.
/// </summary>
internal sealed class Desktop
{
    private const int MaxImageWidth = 1600;

    /// <summary>Pause between drag phases so the target app sees press, move and release as separate events.</summary>
    private const int DragStepDelayMs = 80;
    private const ushort VkReturn = 0x0D;

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["escape"] = 0x1B, ["esc"] = 0x1B,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["space"] = 0x20,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["insert"] = 0x2D,
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["meta"] = 0x5B,
    };

    private static readonly HashSet<ushort> ExtendedKeys = [0x26, 0x28, 0x25, 0x27, 0x24, 0x23, 0x21, 0x22, 0x2D, 0x2E, 0x5B];

    private double _scale = 1;
    private int _originX, _originY;

    public (int X, int Y, int W, int H) VirtualScreen() => (
        Native.GetSystemMetrics(Native.SmXVirtualScreen), Native.GetSystemMetrics(Native.SmYVirtualScreen),
        Native.GetSystemMetrics(Native.SmCxVirtualScreen), Native.GetSystemMetrics(Native.SmCyVirtualScreen));

    /// <summary>Captures the whole desktop, or one window matched by title substring.</summary>
    public (byte[] Png, string Description) Screenshot(string? windowTitle)
    {
        int x, y, w, h;
        string what;
        if (windowTitle is { Length: > 0 })
        {
            var hwnd = FindWindow(windowTitle) ?? throw new McpToolException($"No visible window titled like '{windowTitle}'.");
            Native.GetWindowRect(hwnd, out var r);
            (x, y, w, h) = (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            what = $"window \"{Native.WindowTitle(hwnd)}\"";
        }
        else
        {
            (x, y, w, h) = VirtualScreen();
            what = "the desktop";
        }

        if (w <= 0 || h <= 0) throw new McpToolException("Nothing to capture (window minimized?).");

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }

        _scale = w > MaxImageWidth ? (double)w / MaxImageWidth : 1;
        _originX = x;
        _originY = y;
        var outW = (int)Math.Round(w / _scale);
        var outH = (int)Math.Round(h / _scale);

        using var final = _scale == 1 ? null : new Bitmap(bmp, outW, outH);
        using var ms = new MemoryStream();
        (final ?? bmp).Save(ms, ImageFormat.Png);
        return (ms.GetBuffer()[..(int)ms.Length], $"Screenshot of {what}: {outW}x{outH}px. Coordinates for mouse tools are pixels in this image.");
    }

    private (int X, int Y) ToScreen(int x, int y) =>
        ((int)Math.Round(x * _scale) + _originX, (int)Math.Round(y * _scale) + _originY);

    /// <summary>
    /// Synthetic input lands in whatever is focused. Codale's own window must never be that:
    /// the model could click its own approval prompts or type into its own chat.
    /// </summary>
    private static void RefuseWhenCodaleIsFocused()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == 0)
        {
            return;
        }

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (ProcessNameOf(pid) is { } name &&
            (name.Equals("Codale.App", StringComparison.OrdinalIgnoreCase) || name.Equals("Codale", StringComparison.OrdinalIgnoreCase)))
        {
            throw new McpToolException("Input refused: Codale itself is the foreground window. Use computer_focus_window to bring the target app forward first.");
        }
    }

    /// <summary>The process name for an id, or null when the process is gone or unreadable.</summary>
    private static string? ProcessNameOf(uint pid)
    {
        try
        {
            // GetProcessById holds an OS handle; without using, one leaks per call.
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void Send(params Native.Input[] inputs)
    {
        RefuseWhenCodaleIsFocused();
        if (Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>()) != inputs.Length)
        {
            throw new McpToolException($"Input was blocked (error {Marshal.GetLastWin32Error()}). A higher-privilege window may be focused.");
        }
    }

    private static Native.Input Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) => new()
    {
        Type = Native.InputMouse,
        U = new Native.InputUnion { Mouse = new Native.MouseInput { Dx = dx, Dy = dy, Flags = flags, MouseData = data } },
    };

    private static Native.Input Key(ushort vk, ushort scan, uint flags) => new()
    {
        Type = Native.InputKeyboard,
        U = new Native.InputUnion { Key = new Native.KeyInput { Vk = vk, Scan = scan, Flags = flags } },
    };

    public void MoveTo(int x, int y)
    {
        var (sx, sy) = ToScreen(x, y);
        var (vx, vy, vw, vh) = VirtualScreen();
        if (sx < vx || sy < vy || sx >= vx + vw || sy >= vy + vh)
        {
            throw new McpToolException($"({x},{y}) is outside the screen. Take a screenshot first and use its coordinates.");
        }

        // Absolute mouse coordinates are normalized to 0..65535 across the virtual desktop.
        var nx = (int)Math.Round((sx - vx) * 65535.0 / Math.Max(1, vw - 1));
        var ny = (int)Math.Round((sy - vy) * 65535.0 / Math.Max(1, vh - 1));
        Send(Mouse(Native.MouseMove | Native.MouseAbsolute | Native.MouseVirtualDesk, nx, ny));
    }

    public void Click(int x, int y, string button, int count)
    {
        var (down, up) = button switch
        {
            "left" => (Native.LeftDown, Native.LeftUp),
            "right" => (Native.RightDown, Native.RightUp),
            "middle" => (Native.MiddleDown, Native.MiddleUp),
            _ => throw new McpToolException("button must be left, right or middle."),
        };

        MoveTo(x, y);
        for (var i = 0; i < count; i++)
        {
            Send(Mouse(down), Mouse(up));
        }
    }

    public void Drag(int x1, int y1, int x2, int y2)
    {
        MoveTo(x1, y1);
        Send(Mouse(Native.LeftDown));
        Thread.Sleep(DragStepDelayMs);
        MoveTo((x1 + x2) / 2, (y1 + y2) / 2);
        Thread.Sleep(DragStepDelayMs);
        MoveTo(x2, y2);
        Thread.Sleep(DragStepDelayMs);
        Send(Mouse(Native.LeftUp));
    }

    public void Scroll(int x, int y, int clicksDown, int clicksRight)
    {
        MoveTo(x, y);
        if (clicksDown != 0) Send(Mouse(Native.MouseWheel, data: unchecked((uint)(-clicksDown * 120))));
        if (clicksRight != 0) Send(Mouse(Native.MouseHWheel, data: unchecked((uint)(clicksRight * 120))));
    }

    public void TypeText(string text)
    {
        // One SendInput call for the whole text: a syscall per character was slow, and other
        // input could slip in between the characters.
        var inputs = new List<Native.Input>(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue; // the '\n' that follows presses Enter once
            }

            if (ch is '\n' or '\r')
            {
                inputs.Add(Key(VkReturn, 0, 0));
                inputs.Add(Key(VkReturn, 0, Native.KeyUp));
                continue;
            }

            inputs.Add(Key(0, ch, Native.KeyUnicode));
            inputs.Add(Key(0, ch, Native.KeyUnicode | Native.KeyUp));
        }

        if (inputs.Count > 0)
        {
            Send([.. inputs]);
        }
    }

    /// <summary>Presses a chord such as "ctrl+shift+s", "alt+f4" or "enter".</summary>
    public void PressChord(string chord)
    {
        var codes = new List<ushort>();
        foreach (var part in chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (NamedKeys.TryGetValue(part, out var vk)) codes.Add(vk);
            else if (part.Length >= 2 && part[0] is 'f' or 'F' && int.TryParse(part[1..], out var n) && n is >= 1 and <= 24) codes.Add((ushort)(0x6F + n));
            else if (part.Length == 1 && Native.VkKeyScan(part[0]) is var scan && scan != -1) codes.Add((ushort)(scan & 0xFF));
            else throw new McpToolException($"Unknown key '{part}'.");
        }

        if (codes.Count == 0) throw new McpToolException("No key given.");

        static uint Flags(ushort vk, bool up) => (ExtendedKeys.Contains(vk) ? Native.KeyExtended : 0) | (up ? Native.KeyUp : 0);
        var seq = codes.Select(c => Key(c, 0, Flags(c, false)))
            .Concat(Enumerable.Reverse(codes).Select(c => Key(c, 0, Flags(c, true))))
            .ToArray();
        Send(seq);
    }

    public IReadOnlyList<(nint Hwnd, string Title, string Process)> Windows()
    {
        var found = new List<(nint, string, string)>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.IsWindowVisible(hwnd) && Native.WindowTitle(hwnd) is { Length: > 0 } title)
            {
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                found.Add((hwnd, title, ProcessNameOf(pid) ?? "?"));
            }

            return true;
        }, 0);
        return found;
    }

    public nint? FindWindow(string titlePart) =>
        Windows().FirstOrDefault(w => w.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase)) is { Hwnd: not 0 } w
            ? w.Hwnd
            : null;

    public string Focus(string titlePart)
    {
        var hwnd = FindWindow(titlePart) ?? throw new McpToolException($"No visible window titled like '{titlePart}'.");
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9); // SW_RESTORE
        Native.SetForegroundWindow(hwnd);
        return $"Focused \"{Native.WindowTitle(hwnd)}\".";
    }
}
