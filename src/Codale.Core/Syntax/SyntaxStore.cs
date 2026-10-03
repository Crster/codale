using System.Text.Json;

namespace Codale.Core.Syntax;

/// <summary>
/// The on-disk home of user-installed grammars: <c>languages.json</c> (the manifest) plus
/// <c>grammars\&lt;id&gt;.tmLanguage.json</c>. Built-in languages are not stored here; they
/// come from the grammars the app ships. Every write is temp-file-then-replace so a crash
/// never leaves a half-written manifest, and a manifest that cannot be read is set aside as
/// <c>.broken</c> rather than blocking start-up.
/// </summary>
public sealed class SyntaxStore
{
    public const int MaxGrammarBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private List<LanguageEntry> _languages = [];

    public SyntaxStore(string root)
    {
        Root = root;
        Load();
    }

    public string Root { get; }

    public string ManifestPath => Path.Combine(Root, "languages.json");

    private string GrammarsDir => Path.Combine(Root, "grammars");

    /// <summary>Raised after the installed set changes, on whichever thread made the change.</summary>
    public event Action? Changed;

    /// <summary>Bumped on every change; lets caches built from the store know they are stale.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<LanguageEntry> Languages
    {
        get { lock (_gate) { return _languages.ToArray(); } }
    }

    public string GrammarPath(LanguageEntry entry) => Path.Combine(GrammarsDir, entry.GrammarFile);

    public LanguageEntry? Find(string id)
    {
        lock (_gate)
        {
            return _languages.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public string? ReadGrammar(LanguageEntry entry)
    {
        try
        {
            var path = GrammarPath(entry);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            CrashLogHook.Warn("syntax", $"could not read grammar {entry.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Installs (or replaces) a language. The grammar must already have passed validation.</summary>
    public LanguageEntry Add(LanguageEntry entry, string grammarJson)
    {
        var id = Slug(entry.Id.Length > 0 ? entry.Id : entry.Name);
        if (id.Length == 0)
        {
            throw new ArgumentException("A language needs an id or a name.", nameof(entry));
        }

        var saved = entry with
        {
            Id = id,
            Name = entry.Name.Length > 0 ? entry.Name : id,
            Extensions = Normalize(entry.Extensions),
            GrammarFile = id + ".tmLanguage.json",
        };

        lock (_gate)
        {
            Directory.CreateDirectory(GrammarsDir);
            WriteAtomic(GrammarPath(saved), grammarJson);
            _languages.RemoveAll(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
            _languages.Add(saved);
            Persist();
        }

        RaiseChanged();
        return saved;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var gone = _languages.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
            if (gone is null)
            {
                return false;
            }

            _languages.Remove(gone);
            Persist();
            try { File.Delete(GrammarPath(gone)); } catch { /* an orphan file is harmless */ }
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Imports a grammar file from disk; the language is described by what the grammar declares.</summary>
    public LanguageEntry Import(string path, GrammarSource source = GrammarSource.Imported)
    {
        if (new FileInfo(path).Length > MaxGrammarBytes)
        {
            throw new InvalidDataException("That grammar file is larger than 1 MB.");
        }

        var json = File.ReadAllText(path);
        var error = GrammarValidator.Validate(json, out var meta);
        if (error is not null)
        {
            throw new InvalidDataException(error);
        }

        var stem = Path.GetFileName(path);
        var dot = stem.IndexOf('.');
        var name = meta.Name ?? (dot > 0 ? stem[..dot] : stem);
        return Add(new LanguageEntry
        {
            Id = name,
            Name = name,
            ScopeName = meta.ScopeName,
            Extensions = meta.FileTypes,
            FirstLine = meta.FirstLineMatch,
            Source = source,
        }, json);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return;
            }

            var list = JsonSerializer.Deserialize<List<LanguageEntry>>(File.ReadAllText(ManifestPath), Json) ?? [];
            _languages = list
                .Where(l => l.Id.Length > 0 && l.GrammarFile.Length > 0 && File.Exists(GrammarPath(l)))
                .ToList();
        }
        catch (Exception ex)
        {
            CrashLogHook.Warn("syntax", $"languages.json unreadable ({ex.Message}); starting with built-ins only");
            try { File.Copy(ManifestPath, ManifestPath + ".broken", overwrite: true); } catch { /* best effort */ }
            _languages = [];
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Root);
        WriteAtomic(ManifestPath, JsonSerializer.Serialize(_languages, Json));
    }

    /// <summary>Tells listeners to re-detect languages without the installed set having changed (for example after overrides are cleared).</summary>
    public void NotifyChanged() => RaiseChanged();

    private void RaiseChanged()
    {
        Version++;
        Changed?.Invoke();
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path))
        {
            File.Replace(tmp, path, null);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static string[] Normalize(IEnumerable<string> extensions) =>
        extensions
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToArray();

    internal static string Slug(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }
}

/// <summary>Lets Core log without referencing the app's CrashLog; the app wires it up at start-up.</summary>
public static class CrashLogHook
{
    public static Action<string, string>? Sink { get; set; }

    internal static void Warn(string area, string message) => Sink?.Invoke(area, message);
}
