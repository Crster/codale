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
            Assert.NotEmpty(e.Extensions);
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
}
