using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Codale.Core.Browser;
using Codale.Mcp.Browser;

using Microsoft.Playwright;

namespace Codale.Mcp.Tests;

public sealed class BrowserViewportTests
{
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("responsive")]
    [InlineData("turn-end")]
    [InlineData("dispose")]
    public async Task Attached_browser_restores_full_window_after_agent_resizing(string completion)
    {
        using var playwright = await Playwright.CreateAsync();
        var port = FreePort();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = "msedge", Headless = true, Args = [$"--remote-debugging-port={port}"],
        });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = ViewportSize.NoViewport });
        var page = await context.NewPageAsync();
        await page.RouteAsync("http://viewport.test/**", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "text/html", Body = "<h1>Viewport restoration</h1><button>Ready</button>",
        }));

        await using var session = new BrowserSession { DebuggingPort = port };
        var tools = BrowserTools.Create(session);
        async Task<IReadOnlyList<McpContent>> Call(string name, object? args = null) =>
            await tools.Single(t => t.Name == name).Handler(new McpArgs(JsonSerializer.SerializeToElement(args ?? new { })), CancellationToken.None);

        await Call("browser_navigate", new { url = "http://viewport.test/" });
        Assert.Contains("Viewport restoration", (await Call("browser_snapshot"))[0].Text);
        var original = await page.EvaluateAsync<int[]>("() => [innerWidth, innerHeight]");

        if (completion == "responsive")
        {
            await Call("browser_responsive_check", new { devices = "mobile" });
        }
        else
        {
            await Call("browser_set_viewport", new { device = "mobile" });
            Assert.Equal(390, await page.EvaluateAsync<int>("innerWidth"));
            if (completion == "reset")
            {
                await Call("browser_set_viewport", new { device = "reset" });
            }
            else if (completion == "turn-end")
            {
                var pipe = "codale-viewport-test-" + Guid.NewGuid().ToString("N");
                await using var control = new BrowserViewportControl(pipe, session.RestoreViewportAsync);
                Assert.True(await BrowserViewportControl.RestoreAsync(pipe));
            }
            else
            {
                await session.DisposeAsync();
            }
        }

        await page.WaitForFunctionAsync("original => innerWidth === original[0] && innerHeight === original[1]", original,
            new PageWaitForFunctionOptions { Timeout = 3000 });
        Assert.Equal(original, await page.EvaluateAsync<int[]>("() => [innerWidth, innerHeight]"));
        // Verify that restoration removed the override, rather than pinning the old dimensions.
        var cdp = await context.NewCDPSessionAsync(page);
        try
        {
            var window = await cdp.SendAsync("Browser.getWindowForTarget");
            var bounds = window!.Value.GetProperty("bounds");
            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = window.Value.GetProperty("windowId").GetInt32(),
                ["bounds"] = new { width = bounds.GetProperty("width").GetInt32() + 120, height = bounds.GetProperty("height").GetInt32() + 80 },
            });
            await page.WaitForFunctionAsync("original => innerWidth !== original", original[0]);
        }
        finally { await cdp.DetachAsync(); }

        if (completion != "dispose")
        {
            Assert.Contains("Viewport restoration", (await Call("browser_snapshot"))[0].Text);
            var console = await Call("browser_console_messages");
            Assert.DoesNotContain(console, item => item.Text?.Contains("[pageerror]", StringComparison.Ordinal) == true);
        }
    }
}
