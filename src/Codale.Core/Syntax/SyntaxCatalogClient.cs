using System.Text.Json;

namespace Codale.Core.Syntax;

/// <summary>A grammar the built-in catalog offers: where it comes from and what files it colours.</summary>
public sealed record CatalogEntry(string Id, string Name, string[] Extensions, string Url, string Source);

/// <summary>
/// Downloads grammars from the app's fixed catalog. The list ships inside the app, so nothing is
/// contacted until the user picks a grammar; each URL must be https on the one host the catalog
/// uses, and what comes back is size-capped and validated before it is stored. A downloaded
/// grammar is data for the tokenizer, never code.
/// </summary>
public sealed class SyntaxCatalogClient
{
    private const string AllowedHost = "raw.githubusercontent.com";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _http;

    public SyntaxCatalogClient(HttpClient? http = null) => _http = http ?? SharedHttp;

    /// <summary>The catalog that ships with the app.</summary>
    public static IReadOnlyList<CatalogEntry> Entries { get; } = LoadEntries();

    /// <summary>Fetches, validates and installs <paramref name="entry"/>; throws <see cref="InvalidDataException"/> with a readable reason.</summary>
    public async Task<LanguageEntry> InstallAsync(CatalogEntry entry, SyntaxStore store, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{entry.Name} points somewhere the catalog does not allow.");
        }

        string json;
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > SyntaxStore.MaxGrammarBytes)
            {
                throw new InvalidDataException($"{entry.Name} is larger than 1 MB.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            json = await ReadCappedAsync(stream, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidDataException($"Could not download {entry.Name}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidDataException($"Downloading {entry.Name} timed out.");
        }

        var error = GrammarValidator.Validate(json, out var meta);
        if (error is not null)
        {
            throw new InvalidDataException($"{entry.Name}: {error}");
        }

        return store.Add(new LanguageEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            ScopeName = meta.ScopeName,
            Extensions = entry.Extensions,
            FirstLine = meta.FirstLineMatch,
            Source = GrammarSource.Downloaded,
        }, json);
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
