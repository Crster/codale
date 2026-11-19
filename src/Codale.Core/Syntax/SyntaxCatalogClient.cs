using System.Text.Json;

namespace Codale.Core.Syntax;

/// <summary>A grammar the built-in catalog offers: where it comes from and what files it colours.</summary>
public sealed record CatalogEntry(string Id, string Name, string[] Extensions, string Url, string Source)
{
    public string ScopeName { get; init; } = "";
    public string[] Aliases { get; init; } = [];
    public string[] Filenames { get; init; } = [];
    public string[] FilenamePatterns { get; init; } = [];
    public string? FirstLine { get; init; }
    public string[] AssociationSources { get; init; } = [];
    public string[] Dependencies { get; init; } = [];
    public bool Hidden { get; init; }
    public string[] InjectionScopes { get; init; } = [];
    public string License { get; init; } = "";
    public string LicenseUrl { get; init; } = "";
}

/// <summary>
/// Downloads grammars from the app's fixed catalog. The list ships inside the app, so nothing is
/// contacted until the user picks a grammar; each URL must be https on the one host the catalog
/// uses, and what comes back is size-capped and validated before it is stored. A downloaded
/// grammar is data for the tokenizer, never code.
/// </summary>
public sealed class SyntaxCatalogClient(HttpClient? http = null)
{
    private const string AllowedHost = "raw.githubusercontent.com";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _http = http ?? SharedHttp;

    /// <summary>The catalog that ships with the app.</summary>
    public static IReadOnlyList<CatalogEntry> Entries { get; } = LoadEntries();

    /// <summary>Fetches, validates and installs <paramref name="entry"/>; throws <see cref="InvalidDataException"/> with a readable reason.</summary>
    public async Task<LanguageEntry> InstallAsync(CatalogEntry entry, SyntaxStore store, CancellationToken ct = default)
    {
        // Fetch and validate the entire dependency graph before changing the installed set.
        var pending = new List<(CatalogEntry Entry, string Json, GrammarMeta Meta)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task Fetch(CatalogEntry next)
        {
            if (!seen.Add(next.Id)) return;
            foreach (var id in next.Dependencies)
            {
                if (store.Find(id) is not null) continue;
                var dependency = Entries.FirstOrDefault(e => e.Id == id)
                    ?? throw new InvalidDataException($"Unknown grammar dependency: {id}.");
                await Fetch(dependency).ConfigureAwait(false);
            }
            var json = await DownloadAsync(next.Url, next.Name, ct).ConfigureAwait(false);
            var error = GrammarValidator.Validate(json, out var meta);
            if (error is not null) throw new InvalidDataException($"{next.Name}: {error}");
            if (next.ScopeName.Length > 0 && next.ScopeName != meta.ScopeName)
                throw new InvalidDataException($"{next.Name}: the downloaded scope does not match the catalog.");
            pending.Add((next, json, meta));
        }
        await Fetch(entry).ConfigureAwait(false);
        LanguageEntry? result = null;
        var associations = new LanguageCatalog(store, GrammarTokenizer.Shared.Builtin).All;
        // A graph can contain dozens of grammars; recolor open editors once for the batch.
        try
        {
            foreach (var (item, json, meta) in pending)
            {
                var builtin = associations.FirstOrDefault(l => l.ScopeName == meta.ScopeName || l.Id == item.Id);
                var language = LanguageAssociations.Enrich(new LanguageEntry
                {
                    Id = item.Id, Name = item.Name, ScopeName = meta.ScopeName,
                    FirstLine = meta.FirstLineMatch, Source = GrammarSource.Downloaded,
                    IsDependency = item.Id != entry.Id,
                    InjectionScopes = item.InjectionScopes,
                }, meta, item, builtin);
                var saved = store.Add(language, json, notify: false);
                if (item.Id == entry.Id) result = saved;
            }
        }
        finally { store.NotifyChanged(); }
        return result!;
    }

    internal async Task<string> DownloadAsync(string url, string name, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{name} points somewhere the catalog does not allow.");
        }

        string json;
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > SyntaxStore.MaxGrammarBytes)
            {
                throw new InvalidDataException($"{name} is larger than 1 MB.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            json = await ReadCappedAsync(stream, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidDataException($"Could not download {name}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidDataException($"Downloading {name} timed out.");
        }

        return json;
    }

    private static async Task<string> ReadCappedAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > SyntaxStore.MaxGrammarBytes)
            {
                throw new InvalidDataException("The grammar is larger than 1 MB.");
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IReadOnlyList<CatalogEntry> LoadEntries()
    {
        var assembly = typeof(SyntaxCatalogClient).Assembly;
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".catalog.json", StringComparison.Ordinal));
        if (resource is null)
        {
            return [];
        }

        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return JsonSerializer.Deserialize<List<CatalogEntry>>(reader.ReadToEnd(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }
}
