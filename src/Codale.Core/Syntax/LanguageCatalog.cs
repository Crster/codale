using System.Text.RegularExpressions;

using TextMateSharp.Grammars;

namespace Codale.Core.Syntax;

/// <summary>
/// Every language the editor knows: the ones that ship with the app plus whatever is installed
/// in the <see cref="SyntaxStore"/>. An installed language wins over a built-in one with the same
/// id or extension, which is how a downloaded or generated grammar replaces a shipped one.
/// </summary>
public sealed class LanguageCatalog(SyntaxStore store, RegistryOptions builtin)
{
    // Extensions the shipped grammars do not claim but that the editor has always coloured.
    private static readonly (string Extension, string LanguageId)[] ExtraExtensions =
    [
        ("xaml", "xml"), ("csproj", "xml"), ("props", "xml"), ("targets", "xml"), ("slnx", "xml"),
        ("resx", "xml"), ("config", "xml"), ("vcxproj", "xml"), ("axaml", "xml"),
        ("zsh", "shellscript"), ("cmd", "bat"), ("psm1", "powershell"), ("psd1", "powershell"),
        ("jsonc", "json"), ("mjs", "javascript"), ("cjs", "javascript"),
    ];

    private readonly SyntaxStore _store = store;
    private readonly RegistryOptions _builtin = builtin;
    private readonly Lock _gate = new();
    private List<LanguageInfo> _all = [];
    private int _version = -1;
    private readonly Dictionary<string, (DateTime Stamp, long Length, GrammarMeta? Metadata)> _metadataCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All languages, installed ones first so they win every lookup tier.</summary>
    public IReadOnlyList<LanguageInfo> All
    {
        get
        {
            lock (_gate)
            {
                if (_version != _store.Version)
                {
                    _all = Build();
                    _version = _store.Version;
                }

                return _all;
            }
        }
    }

    public LanguageInfo? ById(string? id) =>
        id is null ? null : All.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a language by id, alias, or extension - what a markdown fence names ("cs", "py", "typescript").</summary>
    public LanguageInfo? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var key = name.Trim();
        var all = All;
        return all.FirstOrDefault(l => string.Equals(l.Id, key, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(l => l.Aliases.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase)))
            ?? all.FirstOrDefault(l => l.Extensions.Any(e => string.Equals(e, key.TrimStart('.'), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Picks the language by exact filename, dotenv family, filename pattern, longest extension, then first line.</summary>
    public LanguageInfo? ForFile(string path, string? firstLine = null)
    {
        var name = Path.GetFileName(path);
        var all = All;

        var byName = all.FirstOrDefault(l => l.Filenames.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)));
        if (byName is not null)
        {
            return byName;
        }

        // Try the longest dotted suffix first so "foo.d.ts" or "x.tar.gz" can match a multi-part extension.
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".env", StringComparison.Ordinal) || lower.Contains(".env.", StringComparison.Ordinal))
        {
            // Environment suffixes (.env.local, app.env.production, .env.example) are not
            // language extensions. Resolve the dotenv family before trying their last suffix.
            // Older downloads may have no fileTypes in their manifest. Recognize their scope
            // as well as their id so they work without reinstalling or fetching anything.
            return all.FirstOrDefault(l => !l.IsBuiltin && (string.Equals(l.Id, "dotenv", StringComparison.OrdinalIgnoreCase) || l.ScopeName == "source.dotenv"))
                ?? all.FirstOrDefault(l => !l.IsBuiltin && l.Extensions.Contains("env", StringComparer.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(l => !l.IsBuiltin && l.ScopeName == "source.ini")
                ?? ById("dotenv") ?? ById("ini");
        }
        var byPattern = all.SelectMany(l => l.FilenamePatterns.Select(p => (Language: l, Pattern: p)))
            .OrderBy(p => p.Language.IsBuiltin)
            .ThenByDescending(p => p.Pattern.Count(c => c is not ('*' or '?' or '{' or '}')))
            .FirstOrDefault(p => FilenamePattern.Matches(p.Pattern, path)).Language;
        if (byPattern is not null) return byPattern;
        for (var dot = lower.IndexOf('.'); dot >= 0; dot = lower.IndexOf('.', dot + 1))
        {
            var suffix = lower[(dot + 1)..];
            if (suffix.Length == 0)
            {
                break;
            }

            var hit = all.FirstOrDefault(l => l.Extensions.Contains(suffix, StringComparer.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }

        if (!string.IsNullOrEmpty(firstLine))
        {
            foreach (var language in all.Where(l => l.FirstLine is not null))
            {
                try
                {
                    if (Regex.IsMatch(firstLine, language.FirstLine!, RegexOptions.None, TimeSpan.FromMilliseconds(20)))
                    {
                        return language;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                    // A bad firstLineMatch only costs that one hint.
                }
            }
        }

        return null;
    }

    private List<LanguageInfo> Build()
    {
        var shipped = new List<LanguageInfo>();
        foreach (var language in _builtin.GetAvailableLanguages())
        {
            if (string.IsNullOrEmpty(language.Id))
            {
                continue;
            }

            var scope = _builtin.GetScopeByLanguageId(language.Id);
            if (string.IsNullOrEmpty(scope))
            {
                continue;
            }

            var extensions = (language.Extensions ?? [])
                .Select(e => e.TrimStart('.').ToLowerInvariant())
                .Concat(ExtraExtensions.Where(x => x.LanguageId == language.Id).Select(x => x.Extension))
                .Where(e => e.Length > 0)
                .Distinct()
                .ToArray();
            var aliases = language.Aliases ?? [];
            var entry = new LanguageEntry { Id = language.Id, Name = aliases.FirstOrDefault() ?? language.Id,
                ScopeName = scope, Extensions = extensions, Aliases = [.. aliases], Source = GrammarSource.Builtin };
            shipped.Add(Info(LanguageAssociations.Enrich(entry, null, CatalogEntryFor(entry), null)));
        }
        shipped.AddRange(BundledGrammars.Languages.Where(b => shipped.All(l => l.Id != b.Id)));
        var list = new List<LanguageInfo>();
        foreach (var entry in _store.Languages.Where(e => !e.IsDependency))
        {
            var builtin = shipped.FirstOrDefault(l => l.Id == entry.Id && l.ScopeName == entry.ScopeName)
                ?? shipped.FirstOrDefault(l => l.ScopeName == entry.ScopeName);
            list.Add(Info(LanguageAssociations.Enrich(entry, ReadMetadata(entry), CatalogEntryFor(entry), builtin)));
        }
        var installedIds = list.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadowed = list.SelectMany(l => l.Extensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        list.AddRange(shipped.Where(l => !installedIds.Contains(l.Id))
            .Select(l => l with { Extensions = [.. l.Extensions.Where(e => !shadowed.Contains(e))] }));
        return list;
    }

    private static CatalogEntry? CatalogEntryFor(LanguageEntry entry) =>
        SyntaxCatalogClient.Entries.FirstOrDefault(e => !e.Hidden && string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase) && e.ScopeName == entry.ScopeName)
        ?? SyntaxCatalogClient.Entries.FirstOrDefault(e => !e.Hidden && e.ScopeName == entry.ScopeName);

    private GrammarMeta? ReadMetadata(LanguageEntry entry)
    {
        try
        {
            var path = _store.GrammarPath(entry);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > SyntaxStore.MaxGrammarBytes) return null;
            if (_metadataCache.TryGetValue(path, out var cached) && cached.Stamp == file.LastWriteTimeUtc && cached.Length == file.Length) return cached.Metadata;
            var json = _store.ReadGrammar(entry);
            var metadata = json is not null && GrammarValidator.ReadMetadata(json, out var parsed) is null && parsed.ScopeName == entry.ScopeName ? parsed : null;
            _metadataCache[path] = (file.LastWriteTimeUtc, file.Length, metadata);
            return metadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static LanguageInfo Info(LanguageEntry entry) => new(entry.Id, entry.Name, entry.ScopeName,
        entry.Extensions, entry.Filenames, entry.Aliases, entry.FirstLine, entry.Source) { FilenamePatterns = entry.FilenamePatterns };
}
