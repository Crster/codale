using System.Net;

using Codale.Core.Syntax;

namespace Codale.Core.Tests.Syntax;

public sealed class SyntaxCatalogClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codale-catalog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private const string Grammar = """{ "scopeName": "source.zig", "patterns": [ { "match": "\\bfn\\b", "name": "keyword.zig" } ] }""";

    [Fact]
    public void The_built_in_catalog_lists_grammars_with_https_urls()
    {
        Assert.NotEmpty(SyntaxCatalogClient.Entries);
        Assert.All(SyntaxCatalogClient.Entries, e =>
        {
            Assert.StartsWith("https://raw.githubusercontent.com/", e.Url);
            Assert.Contains("/shikijs/textmate-grammars-themes/", e.Url);
            Assert.NotEmpty(e.ScopeName);
            Assert.All(e.Dependencies, id => Assert.Contains(SyntaxCatalogClient.Entries, d => d.Id == id));
        });
    }

    [Fact]
    public async Task Installing_a_catalog_entry_downloads_validates_and_stores_it()
    {
        var store = new SyntaxStore(_root);
        var handler = new StubHandler(_ => Ok(Grammar));
        var client = new SyntaxCatalogClient(new HttpClient(handler));
        var entry = new CatalogEntry("zig", "Zig", ["zig"], "https://raw.githubusercontent.com/x/y/main/zig.json", "x/y");

        var installed = await client.InstallAsync(entry, store);

        Assert.Equal(GrammarSource.Downloaded, installed.Source);
        Assert.Equal("source.zig", installed.ScopeName);
        Assert.Equal(["zig"], store.Find("zig")!.Extensions);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task New_downloads_persist_catalog_filename_patterns_names_and_first_line_hints()
    {
        var store = new SyntaxStore(_root);
        var client = new SyntaxCatalogClient(new HttpClient(new StubHandler(_ => Ok(Grammar))));
        var entry = new CatalogEntry("zig", "Zig", ["zig"], "https://raw.githubusercontent.com/x/y/main/zig.json", "x/y")
        {
            Filenames = ["Zigfile"], FilenamePatterns = ["*.zig.*"], FirstLine = "^#!.*zig-script", Aliases = ["Zig Script"],
        };
        await client.InstallAsync(entry, store);
        var saved = new SyntaxStore(_root).Find("zig")!;
        Assert.Contains("Zigfile", saved.Filenames);
        Assert.Contains("*.zig.*", saved.FilenamePatterns);
        Assert.Equal("^#!.*zig-script", saved.FirstLine);
        Assert.Contains("Zig Script", saved.Aliases);
    }

    [Fact]
    public async Task A_download_that_is_not_a_grammar_is_refused_and_nothing_is_stored()
    {
        var store = new SyntaxStore(_root);
        var client = new SyntaxCatalogClient(new HttpClient(new StubHandler(_ => Ok("<html>not found</html>"))));
        var entry = new CatalogEntry("zig", "Zig", ["zig"], "https://raw.githubusercontent.com/x/y/main/zig.json", "x/y");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.InstallAsync(entry, store));

        Assert.Empty(store.Languages);
    }

    [Fact]
    public async Task A_failed_request_surfaces_as_a_readable_error()
    {
        var store = new SyntaxStore(_root);
        var client = new SyntaxCatalogClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        var entry = new CatalogEntry("zig", "Zig", ["zig"], "https://raw.githubusercontent.com/x/y/main/zig.json", "x/y");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.InstallAsync(entry, store));

        Assert.Contains("Zig", error.Message);
    }

    [Theory]
    [InlineData("http://raw.githubusercontent.com/x/y/zig.json")]
    [InlineData("https://evil.example.com/zig.json")]
    [InlineData("file:///C:/secrets.json")]
    public async Task Urls_off_the_allowed_host_are_never_requested(string url)
    {
        var store = new SyntaxStore(_root);
        var handler = new StubHandler(_ => Ok(Grammar));
        var client = new SyntaxCatalogClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.InstallAsync(new CatalogEntry("zig", "Zig", ["zig"], url, "x/y"), store));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_oversized_grammar_is_refused()
    {
        var store = new SyntaxStore(_root);
        var big = Grammar + new string(' ', SyntaxStore.MaxGrammarBytes + 10);
        var client = new SyntaxCatalogClient(new HttpClient(new StubHandler(_ => Ok(big))));
        var entry = new CatalogEntry("zig", "Zig", ["zig"], "https://raw.githubusercontent.com/x/y/main/zig.json", "x/y");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.InstallAsync(entry, store));
    }

    [Fact]
    public async Task Dependencies_are_installed_without_taking_over_file_associations()
    {
        var store = new SyntaxStore(_root);
        var handler = new StubHandler(request =>
        {
            var id = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            var scope = SyntaxCatalogClient.Entries.First(e => e.Id == id).ScopeName;
            return Ok($$"""{ "scopeName": "{{scope}}", "patterns": [] }""");
        });
        var client = new SyntaxCatalogClient(new HttpClient(handler));
        var entry = SyntaxCatalogClient.Entries.First(e => e.Id == "vue");
        var notifications = 0;
        store.Changed += () => notifications++;
        await client.InstallAsync(entry, store);
        Assert.Equal(1, notifications);
        Assert.False(store.Find("vue")!.IsDependency);
        Assert.True(store.Find("typescript")!.IsDependency);
        Assert.Equal(handler.Requests.Count, handler.Requests.Distinct().Count());
        var catalog = new LanguageCatalog(store, new TextMateSharp.Grammars.RegistryOptions(TextMateSharp.Grammars.ThemeName.DarkPlus));
        Assert.Equal("vue", catalog.ForFile("component.vue")!.Id);
        Assert.DoesNotContain(catalog.All, l => l.Id == "vue-directives");
    }

    [Fact]
    public async Task A_bad_dependency_leaves_the_existing_store_unchanged()
    {
        var store = new SyntaxStore(_root);
        var client = new SyntaxCatalogClient(new HttpClient(new StubHandler(_ => Ok("{}"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.InstallAsync(SyntaxCatalogClient.Entries.First(e => e.Id == "vue"), store));
        Assert.Empty(store.Languages);
    }

    [Fact]
    public void Begin_only_rules_and_misplaced_capture_metadata_can_be_loaded()
    {
        const string json = """
            { "scopeName": "source.capture-test", "patterns": [
              { "include": "#array-rule" },
              { "begin": "<!--", "captures": { "0": { "name": "comment" }, "end": "-->", "name": "comment.block" } },
              { "begin": "BEGIN", "name": "string" }
            ], "repository": { "array-rule": [ { "match": "test", "name": "keyword" } ] } }
            """;
        Assert.Null(GrammarValidator.Validate(json, out _, "<!-- comment -->\nBEGIN text"));
    }
}
