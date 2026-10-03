using System.Reflection;

namespace Codale.Core.Syntax;

/// <summary>
/// Grammars Codale ships itself for languages the TextMateSharp package lacks. They behave like
/// built-in languages: listed in the catalog, replaceable by an installed grammar, never removable.
/// </summary>
internal static class BundledGrammars
{
    internal static readonly LanguageInfo[] Languages =
    [
        new("toml", "TOML", "source.toml", ["toml"], ["Cargo.lock", "Pipfile"], [], null, GrammarSource.Builtin),
    ];

    /// <summary>The grammar JSON for a bundled scope name, or null if there is none.</summary>
    internal static string? Read(string scopeName)
    {
        var language = Languages.FirstOrDefault(l => l.ScopeName == scopeName);
        if (language is null)
        {
            return null;
        }

        var assembly = typeof(BundledGrammars).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".{language.Id}.tmLanguage.json", StringComparison.Ordinal));
        if (resource is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
