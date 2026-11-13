using Codale.App.Services;
using Codale.Terminal;

using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace Codale.App.Views;

/// <summary>
/// The notebook terminal's look: a black terminal, as native as the classic one, whose command lines
/// carry their details (folder, timing, result, shell) as quiet text in the app's own type and the
/// chat's status colours - no coloured blocks. The terminal is dark in every app theme, so these
/// colours are fixed rather than theme resources.
/// </summary>
internal static class TerminalLook
{
    public static readonly Color Background = Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C);
    public static readonly Color Command = Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8);
    public static readonly Color Muted = Color.FromArgb(0xFF, 0x80, 0x80, 0x88);
    public static readonly Color Faint = Color.FromArgb(0xFF, 0x50, 0x50, 0x58);
    public static readonly Color Accent = Color.FromArgb(0xFF, 0xA7, 0x8B, 0xFA);
    public static readonly Color Success = Color.FromArgb(0xFF, 0x34, 0xD3, 0x99);
    public static readonly Color Failure = Color.FromArgb(0xFF, 0xFB, 0x71, 0x85);
    public static readonly Color Hover = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
    public static readonly Color Selected = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);

    public static SolidColorBrush Brush(Color color) => new(color);

    /// <summary>The terminal font from Settings, for commands and the input.</summary>
    public static FontFamily MonoFont => new(InstalledFonts.Resolve(AppSettings.TerminalFontFamily, "Cascadia Mono"));

    /// <summary>The short name of the shell commands run under ("pwsh", "powershell", "cmd").</summary>
    public static string ShellLabel => Path.GetFileNameWithoutExtension(TerminalSession.ResolveShell(AppSettings.TerminalShell));

    public static double MonoSize => Math.Clamp(AppSettings.TerminalFontSize, AppSettings.MinFontSize, AppSettings.MaxFontSize);
}
