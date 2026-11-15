using Codale.Core.Syntax;

namespace Codale.Settings;

/// <summary>
/// Decides which language colours a file: the user's per-file override first, then whatever the
/// catalog detects from the name or first line. Also owns the app-side start-up of the syntax
/// store, so the grammars live next to settings.json.
/// </summary>
public sealed class SyntaxSelection(AppSettingsStore settings, string dataFolder, SyntaxStore? store = null, LanguageCatalog? catalog = null)
{
    /// <summary>The override value that switches colouring off for one file.</summary>
    public const string PlainText = "plaintext";

    public bool HighlightingEnabled => settings.EditorHighlighting;
    public SyntaxStore Store { get; } = store ?? new SyntaxStore(Path.Combine(dataFolder, "syntax"));
    public LanguageCatalog Catalog { get { Start(); return _catalog!; } }
    private LanguageCatalog? _catalog = catalog;

    /// <summary>Loads a standalone catalog unless the host supplied its shared catalog.</summary>
    public void Start() => _catalog ??= new LanguageCatalog(
        Store, new TextMateSharp.Grammars.RegistryOptions(TextMateSharp.Grammars.ThemeName.DarkPlus));

    /// <summary>The language id for a file, or null for plain text.</summary>
    public string? IdFor(string path, string? firstLine = null)
    {
        Start();
        if (settings.SyntaxOverrides.TryGetValue(Key(path), out var chosen))
        {
            if (chosen == PlainText)
            {
                return null;
            }

            if (_catalog!.ById(chosen) is { } overridden)
            {
                return overridden.Id;
            }

            // The overriding language was removed: fall through to detection.
        }

        return _catalog!.ForFile(path, firstLine)?.Id;
    }

    /// <summary>True when the user has picked a language for this file by hand.</summary>
    public bool HasOverride(string path) => settings.SyntaxOverrides.ContainsKey(Key(path));

    /// <summary>Sets (or, with null, clears) the language chosen for a file.</summary>
    public void SetOverride(string path, string? languageId)
    {
        var all = settings.SyntaxOverrides;
        if (languageId is null)
        {
            all.Remove(Key(path));
        }
        else
        {
            all[Key(path)] = languageId;
        }

        settings.SyntaxOverrides = all;
    }

    private static string Key(string path) => Path.GetFullPath(path);
}