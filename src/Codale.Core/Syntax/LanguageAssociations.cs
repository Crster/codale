namespace Codale.Core.Syntax;

/// <summary>Combines persisted, grammar-declared, catalog and shipped associations without fetching payloads.</summary>
internal static class LanguageAssociations
{
    internal static LanguageEntry Enrich(LanguageEntry entry, GrammarMeta? grammar, CatalogEntry? catalog, LanguageInfo? builtin)
    {
        var types = (grammar?.FileTypes ?? []).Concat(entry.Extensions).Concat(catalog?.Extensions ?? []).Concat(builtin?.Extensions ?? []).ToArray();
        var names = entry.Filenames.Concat(grammar?.Filenames ?? []).Concat(catalog?.Filenames ?? []).Concat(builtin?.Filenames ?? []).ToArray();
        return entry with
        {
            Extensions = Unique(types.Where(t => !IsPattern(t))
                .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())),
            // TextMate fileTypes accepts both extensions and literal names such as Makefile.
            Filenames = Unique(names.Concat(grammar?.FileTypes ?? []).Where(t => !IsPattern(t))),
            FilenamePatterns = Unique(entry.FilenamePatterns.Concat(grammar?.FilenamePatterns ?? []).Concat(types.Concat(names).Where(IsPattern))
                .Concat(catalog?.FilenamePatterns ?? []).Concat(builtin?.FilenamePatterns ?? [])),
            Aliases = Unique(entry.Aliases.Concat(catalog?.Aliases ?? []).Concat(builtin?.Aliases ?? [])),
            FirstLine = entry.FirstLine ?? grammar?.FirstLineMatch ?? catalog?.FirstLine ?? builtin?.FirstLine,
        };
    }

    private static bool IsPattern(string text) => text.IndexOfAny(['*', '?', '[', '{', '/', '\\']) >= 0;
    private static string[] Unique(IEnumerable<string> values) => [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase)];
}
