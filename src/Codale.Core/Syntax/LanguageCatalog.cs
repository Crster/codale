using System.Text.RegularExpressions;
using TextMateSharp.Grammars;

namespace Codale.Core.Syntax;

/// <summary>
/// Every language the editor knows: the ones that ship with the app plus whatever is installed
/// in the <see cref="SyntaxStore"/>. An installed language wins over a built-in one with the same
/// id or extension, which is how a downloaded or generated grammar replaces a shipped one.
/// </summary>
public sealed class LanguageCatalog
{
    // Extensions the shipped grammars do not claim but that the editor has always coloured.
    private static readonly (string Extension, string LanguageId)[] ExtraExtensions =
    [
        ("xaml", "xml"), ("csproj", "xml"), ("props", "xml"), ("targets", "xml"), ("slnx", "xml"),
        ("resx", "xml"), ("config", "xml"), ("vcxproj", "xml"), ("axaml", "xml"),
        ("zsh", "shellscript"), ("cmd", "bat"), ("psm1", "powershell"), ("psd1", "powershell"),
        ("jsonc", "json"), ("mjs", "javascript"), ("cjs", "javascript"),
    ];

    private readonly SyntaxStore _store;
    private readonly RegistryOptions _builtin;
    private readonly object _gate = new();
    private List<LanguageInfo> _all = [];
    private int _version = -1;

    public LanguageCatalog(SyntaxStore store, RegistryOptions builtin)
    {
        _store = store;
        _builtin = builtin;
    }

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

    /// <summary>Picks the language for a file: exact file name, then extension, then a first-line match.</summary>
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
        for (var dot = lower.IndexOf('.'); dot >= 0; dot = lower.IndexOf('.', dot + 1))
        {
            var suffix = lower[(dot + 1)..];
            if (suffix.Length == 0)
            {
                break;
            }

            var hit = all.FirstOrDefault(l => l.Extensions.Contains(suffix));
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
        var list = new List<LanguageInfo>();
        foreach (var entry in _store.Languages)
        {
            list.Add(new LanguageInfo(
                entry.Id,
                entry.Name,
                entry.ScopeName,
                entry.Extensions,
                entry.Filenames,
                entry.Aliases,
                entry.FirstLine,
                entry.Source));
        }

        var installedIds = list.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadowed = list.SelectMany(l => l.Extensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var language in _builtin.GetAvailableLanguages())
        {
            if (string.IsNullOrEmpty(language.Id) || installedIds.Contains(language.Id))
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
                .Where(e => e.Length > 0 && !shadowed.Contains(e))
                .Distinct()
                .ToArray();
            var aliases = language.Aliases ?? [];
            list.Add(new LanguageInfo(
                language.Id,
                aliases.FirstOrDefault() ?? language.Id,
                scope,
                extensions,
                [],
                aliases,
                null,
                GrammarSource.Builtin));
        }

        foreach (var bundled in BundledGrammars.Languages.Where(b => !installedIds.Contains(b.Id) && list.All(l => l.Id != b.Id)))
        {
            list.Add(bundled with { Extensions = bundled.Extensions.Where(e => !shadowed.Contains(e)).ToArray() });
        }

        return list;
    }
}
