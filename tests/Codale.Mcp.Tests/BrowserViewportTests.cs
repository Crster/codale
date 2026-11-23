using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Codale.Core.Browser;
using Codale.Mcp.Browser;

namespace Codale.Mcp.Tests;

/// <summary>
/// Drives the browser tools against a headless Edge listening on a DevTools port, the same way
/// they drive Codale Browser. Skipped (passes) on machines without Edge.
/// </summary>
public sealed class BrowserViewportTests
{
    private const string Page = """
        <!doctype html><meta name="viewport" content="width=device-width"><title>Codale test</title>
        <h1>Codale test</h1>
        <input id="name" placeholder="Name">
        <button id="go" onclick="document.getElementById('out').textContent = 'Hello ' + document.getElementById('name').value; console.log('clicked')">Go</button>
        <div id="out"></div>
        <select id="pick"><option value="a">Alpha</option><option value="b">Beta</option></select>
        <input id="agree" type="checkbox">
        """;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? EdgePath() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        }
        .Select(root => Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"))
        .FirstOrDefault(File.Exists);

    /// <summary>A headless Edge with a DevTools port, and a tiny local site to point it at.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Process _edge;
        private readonly HttpListener _site = new();
        private readonly string _profile = Path.Combine(Path.GetTempPath(), "codale-cdp-test-" + Guid.NewGuid().ToString("N"));

        public int Port { get; } = FreePort();
        public string Url { get; }

        public Fixture(string edge)
        {
            var sitePort = FreePort();
            Url = $"http://localhost:{sitePort}/";
            _site.Prefixes.Add(Url);
            _site.Start();
            _ = Task.Run(async () =>
            {
                while (_site.IsListening)
                {
                    try
                    {
                        var context = await _site.GetContextAsync();
                        var body = Encoding.UTF8.GetBytes(Page);
                        context.Response.ContentType = "text/html; charset=utf-8";
                        await context.Response.OutputStream.WriteAsync(body);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            });

            _edge = Process.Start(new ProcessStartInfo(edge,
                $"--headless=new --remote-debugging-port={Port} --user-data-dir=\"{_profile}\" --no-first-run --no-default-browser-check --window-size=1200,800 about:blank"))!;
        }

        public BrowserSession Session() => new() { DebuggingPort = Port, StartupTimeout = TimeSpan.FromSeconds(20) };

        public async ValueTask DisposeAsync()
        {
            try { _edge.Kill(entireProcessTree: true); await _edge.WaitForExitAsync(); } catch (InvalidOperationException) { }
            _edge.Dispose();
            _site.Close();
            try { Directory.Delete(_profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static Func<string, object?, Task<IReadOnlyList<McpContent>>> Tools(BrowserSession session)
    {
        var tools = BrowserTools.Create(session);
        return async (name, args) =>
            await tools.Single(t => t.Name == name).Handler(new McpArgs(JsonSerializer.SerializeToElement(args ?? new { })), CancellationToken.None);
    }

    /// <summary>The first call waits for the debugging port, which a just-started Edge opens a moment later.</summary>
    private static async Task<IReadOnlyList<McpContent>> FirstCallAsync(Func<string, object?, Task<IReadOnlyList<McpContent>>> call, string name, object? args)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await call(name, args); }
            catch (McpToolException) when (attempt < 40) { await Task.Delay(250); }
        }
    }

    [Fact]
    public async Task Tools_drive_a_page_over_the_devtools_protocol()
    {
        if (EdgePath() is not { } edge) return;
        await using var fixture = new Fixture(edge);
        await using var session = fixture.Session();
        var call = Tools(session);

        var opened = (await FirstCallAsync(call, "browser_navigate", new { url = fixture.Url }))[0].Text!;
        Assert.StartsWith("200 ", opened, StringComparison.Ordinal);
        Assert.Contains("Title: Codale test", opened, StringComparison.Ordinal);

        var snapshot = (await call("browser_snapshot", null))[0].Text!;
        Assert.Contains("# Codale test", snapshot, StringComparison.Ordinal);
        Assert.Contains("button \"Go\"", snapshot, StringComparison.Ordinal);

        await call("browser_type", new { target = "#name", text = "Ada" });
        await call("browser_click", new { target = "#go" });
        Assert.Equal("Hello Ada", (await call("browser_get_text", new { target = "#out" }))[0].Text);
        Assert.Contains("[log] clicked", (await call("browser_console_messages", null))[0].Text, StringComparison.Ordinal);
        Assert.Contains($"200 GET {fixture.Url}", (await call("browser_network_requests", null))[0].Text, StringComparison.Ordinal);

        await call("browser_type", new { target = "#name", text = "" });
        await call("browser_click", new { target = "#name" });
        await call("browser_press_key", new { key = "Shift+B" });
        await call("browser_press_key", new { key = "o" });
        Assert.Equal("Bo", (await call("browser_evaluate", new { script = "() => document.getElementById('name').value" }))[0].Text);

        await call("browser_select_option", new { target = "#pick", value = "Beta" });
        await call("browser_fill_form", new { fields = new[] { new { target = "#agree", value = "true" } } });
        Assert.Equal("[\"b\",true]", (await call("browser_evaluate", new { script = "[pick.value, agree.checked]" }))[0].Text);

        await call("browser_emulate", new { colorScheme = "dark" });
        Assert.Equal("true", (await call("browser_evaluate", new { script = "matchMedia('(prefers-color-scheme: dark)').matches" }))[0].Text);

        var shot = (await call("browser_screenshot", null))[0];
        Assert.Equal("image", shot.Type);

        var stale = await Assert.ThrowsAsync<McpToolException>(() => call("browser_click", new { target = "e999" }));
        Assert.Contains("browser_snapshot", stale.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("responsive")]
    [InlineData("turn-end")]
    [InlineData("dispose")]
    public async Task Viewport_overrides_are_released_after_agent_resizing(string completion)
    {
        if (EdgePath() is not { } edge) return;
        await using var fixture = new Fixture(edge);
        var session = fixture.Session();
        var call = Tools(session);
        static async Task<int[]> SizeAsync(Func<string, object?, Task<IReadOnlyList<McpContent>>> using_) =>
            JsonSerializer.Deserialize<int[]>((await using_("browser_evaluate", new { script = "[innerWidth, innerHeight]" }))[0].Text!)!;

        await FirstCallAsync(call, "browser_navigate", new { url = fixture.Url });
        var original = await SizeAsync(call);

        if (completion == "responsive")
        {
            await call("browser_responsive_check", new { devices = "mobile" });
        }
        else
        {
            await call("browser_set_viewport", new { device = "mobile" });
            Assert.Equal(390, (await SizeAsync(call))[0]);
            if (completion == "reset")
            {
                await call("browser_set_viewport", new { device = "reset" });
            }
            else if (completion == "turn-end")
            {
                var pipe = "codale-viewport-test-" + Guid.NewGuid().ToString("N");
                await using var control = new BrowserViewportControl(pipe, session.RestoreViewportAsync);
                Assert.True(await BrowserViewportControl.RestoreAsync(pipe));
            }
        }

        if (completion == "dispose")
        {
            // Letting go of the window must not leave the device size behind on it.
            await session.DisposeAsync();
            await using var fresh = fixture.Session();
            Assert.Equal(original, await SizeAsync(Tools(fresh)));
        }
        else
        {
            Assert.Equal(original, await SizeAsync(call));
            Assert.Contains("Codale test", (await call("browser_snapshot", null))[0].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("[pageerror]", (await call("browser_console_messages", null))[0].Text, StringComparison.Ordinal);
            await session.DisposeAsync();
        }
    }
}
