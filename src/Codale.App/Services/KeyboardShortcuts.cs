using Microsoft.UI.Input;

using Windows.System;
using Windows.UI.Core;

namespace Codale.App.Services;

/// <summary>A key with its modifiers, e.g. <c>Ctrl+Shift+I</c>. The text form is what settings.json stores.</summary>
public readonly record struct KeyChord(VirtualKey Key, bool Ctrl, bool Shift, bool Alt)
{
    // The WinRT VirtualKey enum has no names for punctuation; these are the Win32 VK_OEM codes.
    private static readonly (string Name, VirtualKey Key)[] Punctuation =
    [
        ("`", (VirtualKey)192), (".", (VirtualKey)190), (",", (VirtualKey)188), ("-", (VirtualKey)189),
        ("=", (VirtualKey)187), (";", (VirtualKey)186), ("/", (VirtualKey)191), ("[", (VirtualKey)219),
        ("\\", (VirtualKey)220), ("]", (VirtualKey)221), ("'", (VirtualKey)222),
    ];

    /// <summary>
    /// The chord for a key press, reading the modifiers from the keyboard; null for a modifier on its own or a key we cannot name.
    /// With <paramref name="usableOnly"/> a press that could never be bound (no Ctrl or Alt, not a function key) also yields null,
    /// before any modifier is read - the window asks this on every key press.
    /// </summary>
    public static KeyChord? FromKeyPress(VirtualKey key, bool usableOnly = false)
    {
        return !CanName(key)
            ? null
            : usableOnly && key is not (>= VirtualKey.F1 and <= VirtualKey.F12) && !IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu)
            ? null
            : new KeyChord(key, IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift), IsDown(VirtualKey.Menu));
    }

    public static bool AnyModifierDown() =>
        IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift) || IsDown(VirtualKey.Menu);

    private static bool IsDown(VirtualKey modifier) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(modifier).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// A chord worth binding: Ctrl or Alt held (bare letters would fire while typing), or
    /// a function key on its own.
    /// </summary>
    public bool IsUsable => Ctrl || Alt || Key is >= VirtualKey.F1 and <= VirtualKey.F12;

    public static KeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        bool ctrl = false, shift = false, alt = false;
        VirtualKey? key = null;

        // Tokens split on '+', so the plus key is written "=" (same physical key).
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    ctrl = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                default:
                    if (key is not null || KeyFromName(raw) is not { } parsed)
                    {
                        return null;
                    }

                    key = parsed;
                    break;
            }
        }

        return key is { } k ? new KeyChord(k, ctrl, shift, alt) : null;
    }

    public override string ToString()
    {
        var name = NameOf(Key) ?? Key.ToString();
        return (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + name;
    }

    private static bool CanName(VirtualKey key) =>
        key is >= VirtualKey.A and <= VirtualKey.Z
            or >= VirtualKey.Number0 and <= VirtualKey.Number9
            or >= VirtualKey.F1 and <= VirtualKey.F12
            or VirtualKey.Space
        || Array.Exists(Punctuation, p => p.Key == key);

    private static string? NameOf(VirtualKey key)
    {
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            return ((char)key).ToString();
        }

        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            return ((char)key).ToString();
        }

        if (key is >= VirtualKey.F1 and <= VirtualKey.F12)
        {
            return "F" + (key - VirtualKey.F1 + 1);
        }

        if (key == VirtualKey.Space)
        {
            return "Space";
        }

        foreach (var (name, punctuation) in Punctuation)
        {
            if (punctuation == key)
            {
                return name;
            }
        }

        return null;
    }

    private static VirtualKey? KeyFromName(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetter(name[0]))
        {
            return (VirtualKey)char.ToUpperInvariant(name[0]);
        }

        if (name.Length == 1 && char.IsAsciiDigit(name[0]))
        {
            return (VirtualKey)name[0];
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 12)
        {
            return VirtualKey.F1 + (n - 1);
        }

        if (name.Equals("Space", StringComparison.OrdinalIgnoreCase))
        {
            return VirtualKey.Space;
        }

        foreach (var (punctuationName, key) in Punctuation)
        {
            if (punctuationName == name)
            {
                return key;
            }
        }

        return null;
    }
}

/// <summary>
/// The window-wide keyboard shortcuts: the actions, their default chords and the
/// user's overrides from settings.json (<c>shortcut.*</c>). A chord stored as "none"
/// disables the action; an unreadable one falls back to the default.
/// </summary>
public sealed class KeyboardShortcuts(AppSettingsStore settings) : IDisposable
{
    public const string OpenTerminal = "openTerminal";
    public const string FocusShellCommand = "focusShellCommand";
    public const string ToggleLeftPanel = "toggleLeftPanel";
    public const string NewFile = "newFile";
    public const string OpenChat = "openChat";
    public const string ToggleCommands = "toggleCommands";
    public const string Search = "search";
    public const string OpenBrowser = "openBrowser";
    public const string CycleMode = "cycleMode";
    public const string ShowGit = "showGit";
    public const string StopChat = "stopChat";

    /// <summary>The one chord text that is not a <see cref="KeyChord"/>: Esc pressed twice in a chat.</summary>
    public const string DoubleEscape = "Esc Esc";

    public static IReadOnlyList<ShortcutAction> Actions { get; } =
    [
        new(OpenTerminal, "Open terminal", "A new terminal tab.", "Ctrl+T"),
        new(FocusShellCommand, "Open terminal (alternate)", "A second chord for a new terminal tab.", "Ctrl+`"),
        new(ToggleLeftPanel, "Hide / show left panel", "The files and git panel.", "Ctrl+B"),
        new(NewFile, "New file", "An untitled editor tab.", "Ctrl+N"),
        new(OpenChat, "Open chat", "Brings up the chat and focuses its prompt.", "Ctrl+I"),
        new(ToggleCommands, "Toggle command menu", "The project commands menu.", "Ctrl+P"),
        new(Search, "Search", "Opens the quick search over files, history and commands; scoped to the file while an editor is in front.", "Ctrl+F"),
        new(OpenBrowser, "Open browser", "The browser the agent's browser tools attach to.", "Ctrl+Shift+B"),
        new(CycleMode, "Cycle chat mode", "Auto, Ask, Planning, Manual, Accept edits, Full.", "Ctrl+."),
        new(ShowGit, "Show git panel", "Opens the left panel on its Git tab.", "Ctrl+G"),
        new(StopChat, "Stop chat", "Cancels the running turn. Press Esc twice in the chat, or record another chord.", DoubleEscape),
    ];

    /// <summary>Chords that already mean something in every text field or in the editor.</summary>
    private static readonly HashSet<string> Reserved = ["Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Ctrl+S"];

    /// <summary>While a Settings recorder waits for a chord, the window must not act on the keys it is about to capture.</summary>
    public bool IsRecording { get; set; }

    private readonly ShortcutBindings _bindings = new(settings, Actions,
        text => text.Equals(DoubleEscape, StringComparison.OrdinalIgnoreCase) ? DoubleEscape : KeyChord.Parse(text)?.ToString());

    public void Dispose()
    {
        _bindings.Dispose();
        IsRecording = false;
    }

    public static ShortcutAction ActionFor(string id) => Actions.First(a => a.Id == id);

    /// <summary>The chord text for an action, or "" when it is disabled.</summary>
    public string Get(string id) => _bindings.Get(id);

    /// <summary>True while the user has Esc Esc bound to stopping the chat.</summary>
    public bool StopOnDoubleEscape => Get(StopChat) == DoubleEscape;

    /// <summary>Stores a chord (or "" to disable the action); the default is stored as the absence of a setting.</summary>
    public void Set(string id, string chord) => _bindings.Set(id, chord);

    public static bool IsReserved(KeyChord chord) => Reserved.Contains(chord.ToString());

    /// <summary>The action currently bound to a chord, or null.</summary>
    public string? Find(KeyChord chord) => _bindings.Find(chord.ToString());

    /// <summary>The action (other than <paramref name="except"/>) already using the chord, for the conflict message.</summary>
    public ShortcutAction? Owner(KeyChord chord, string except) =>
        Find(chord) is { } id && id != except ? ActionFor(id) : null;

}