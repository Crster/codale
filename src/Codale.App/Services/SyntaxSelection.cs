using Codale.Core.Syntax;

namespace Codale.App.Services;

/// <summary>
/// Decides which language colours a file: the user's per-file override first, then whatever the
/// catalog detects from the name or first line. Also owns the app-side start-up of the syntax
/// store, so the grammars live next to settings.json.
/// </summary>
public static class SyntaxSelection
{
    /// <summary>The override value that switches colouring off for one file.</summary>
    public const string PlainText = "plaintext";

    private static bool _started;

    /// <summary>Points the syntax services at the app data folder and routes their warnings to the crash log. Safe to repeat.</summary>
    public static void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        CrashLogHook.Sink = CrashLog.Warn;
        SyntaxService.Initialize(Path.Combine(
            Path.GetDirectoryName(Codale.Storage.CodaleStore.DefaultDatabasePath)!,
            "syntax"));
    }

    /// <summary>The language id for a file, or null for plain text.</summary>
    public static string? IdFor(string path, string? firstLine = null)
    {
        Start();
        if (AppSettings.SyntaxOverrides.TryGetValue(Key(path), out var chosen))
        {
            if (chosen == PlainText)
            {
                return null;
            }

            if (SyntaxService.Catalog.ById(chosen) is { } overridden)
            {
                return overridden.Id;
            }

            // The overriding language was removed: fall through to detection.
        }

        return SyntaxService.Catalog.ForFile(path, firstLine)?.Id;
    }

    /// <summary>True when the user has picked a language for this file by hand.</summary>
    public static bool HasOverride(string path) => AppSettings.SyntaxOverrides.ContainsKey(Key(path));

    /// <summary>Sets (or, with null, clears) the language chosen for a file.</summary>
    public static void SetOverride(string path, string? languageId)
    {
        var all = AppSettings.SyntaxOverrides;
        if (languageId is null)
        {
            all.Remove(Key(path));
        }
        else
        {
            all[Key(path)] = languageId;
        }

        AppSettings.SyntaxOverrides = all;
    }

    private static string Key(string path) => Path.GetFullPath(path);
}
