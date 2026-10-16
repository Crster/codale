using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

using Codale.Core.Serving;
using Codale.Core.Tasks;

namespace Codale.Core.Tests;

public sealed class StaticFileServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codale-serve-" + Guid.NewGuid().ToString("N"));
    private readonly string _site;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });

    public StaticFileServerTests()
    {
        _site = Path.Combine(_dir, "site");
        Directory.CreateDirectory(Path.Combine(_site, "docs", "q&a"));
        File.WriteAllText(Path.Combine(_site, "index.html"), "<h1>home</h1>");
        File.WriteAllText(Path.Combine(_site, "app.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(_site, "docs", "a&b.txt"), "0123456789");
        File.WriteAllText(Path.Combine(_dir, "secret.txt"), "top-secret");
    }

    public void Dispose()
    {
        _http.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Serves_index_and_files_with_their_content_type()
    {
        using var server = new StaticFileServer(_site);

        var index = await _http.GetAsync(server.Url);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("text/html", index.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<h1>home</h1>", await index.Content.ReadAsStringAsync());
        Assert.Contains("no-store", index.Headers.CacheControl?.ToString());

        var js = await _http.GetAsync(server.Url + "app.js");
        Assert.Equal("text/javascript", js.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Lists_a_folder_without_index_with_escaped_names()
    {
        using var server = new StaticFileServer(_site);

        var redirect = await _http.GetAsync(server.Url + "docs");
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);

        var listing = await (await _http.GetAsync(server.Url + "docs/")).Content.ReadAsStringAsync();
        Assert.Contains("a&amp;b.txt", listing);
        Assert.Contains("q&amp;a/", listing);
        Assert.DoesNotContain(">q&a/<", listing);
        Assert.Contains("href=\"a%26b.txt\"", listing);
    }

    [Fact]
    public async Task Missing_files_are_404_and_spa_falls_back_to_index()
    {
        using (var plain = new StaticFileServer(_site))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(plain.Url + "nope.css")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(plain.Url + "route/inside")).StatusCode);
        }

        using var spa = new StaticFileServer(_site, spa: true);
        Assert.Equal("<h1>home</h1>", await _http.GetStringAsync(spa.Url + "route/inside"));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(spa.Url + "nope.css")).StatusCode);
    }

    [Fact]
    public async Task Answers_ranges_head_and_refuses_other_methods()
    {
        using var server = new StaticFileServer(_site);
        var url = server.Url + "docs/a&b.txt";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(2, 5);
        var partial = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("2345", await partial.Content.ReadAsStringAsync());

        var head = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(10, head.Content.Headers.ContentLength);

        var post = await _http.PostAsync(url, new StringContent("x"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/%2e%2e%5csecret.txt")]
    [InlineData("/docs/..%5c..%5csecret.txt")]
    public async Task Never_serves_outside_the_folder(string path)
    {
        using var server = new StaticFileServer(_site);

        // Raw bytes: HttpClient would normalize the dots away before sending.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost:{server.Port}\r\nConnection: close\r\n\r\n"));
        var reply = await new StreamReader(stream, Encoding.ASCII).ReadToEndAsync();

        Assert.DoesNotContain("top-secret", reply);
    }

    [Fact]
    public void Host_reuses_a_folder_and_stop_frees_the_port()
    {
        using var host = new FolderServerHost();
        var changes = 0;
        host.Changed += (_, _) => changes++;

        var first = host.Serve(_site);
        var again = host.Serve(_site + Path.DirectorySeparatorChar);
        Assert.Equal(first.Id, again.Id);
        Assert.Single(host.List());

        host.Stop(first.Id);
        Assert.Empty(host.List());
        Assert.Equal(2, changes);

        // The port is free again: a fixed bind to it succeeds.
        using var rebound = new StaticFileServer(_site, first.Port);
        Assert.Equal(first.Port, rebound.Port);

        Assert.Throws<TaskServiceException>(() => host.Stop("s99"));
        Assert.Throws<TaskServiceException>(() => host.Serve(Path.Combine(_dir, "missing")));
    }
}
