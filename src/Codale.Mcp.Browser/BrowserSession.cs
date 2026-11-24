using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Codale.Core.Projects;
using Codale.Settings;

namespace Codale.Mcp.Browser;

/// <summary>A device profile the browser can be switched to: size, pixel ratio, touch and user agent.</summary>
public sealed record ViewportPreset(string Name, int Width, int Height, double Scale, bool Mobile, string? UserAgent = null)
{
    private const string IPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";
    private const string IPad = "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";
    private const string Android = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36";

    public static readonly IReadOnlyList<ViewportPreset> All =
    [
        new("mobile", 390, 844, 3, true, IPhone),
        new("mobile-small", 360, 640, 2, true, Android),
        new("tablet", 820, 1180, 2, true, IPad),
        new("laptop", 1366, 768, 1, false),
        new("desktop", 1920, 1080, 1, false),
        new("4k", 3840, 2160, 1, false),
    ];

    public static ViewportPreset? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Names => string.Join(", ", All.Select(p => $"{p.Name} ({p.Width}x{p.Height})"));

    /// <summary>True when switching between the two needs no reload (only the size differs).</summary>
    public bool SameProfileAs(ViewportPreset other) =>
        Scale == other.Scale && Mobile == other.Mobile && UserAgent == other.UserAgent;

    public override string ToString() => $"{Name} {Width}x{Height}@{Scale}x{(Mobile ? " mobile+touch" : "")}";
}

/// <summary>
/// The agent's handle on Codale Browser, the app's WebView2 window. It asks the window for a tab of
/// this session's own (marked in the session's colour) and attaches to that tab's DevTools target,
/// starting the window first when it is not open, so the user always sees what the agent does and
/// parallel agents never share a page. Logins live in the window's own profile. Device and media emulation belong
/// to this connection: they are dropped when it closes and never outlive the agent's session.
/// Element handles are refs stamped onto the DOM by <see cref="SnapshotScript"/>.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private const int MaxLogEntries = 200;

    /// <summary>The DevTools port Codale Browser listens on; see <see cref="McpDefaults.BrowserAttachPort"/>.</summary>
    public const int AttachPort = McpDefaults.BrowserAttachPort;

    public int DebuggingPort { get; init; } = AttachPort;

    /// <summary>How long a freshly started window gets to bring its page up.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _console = [];
    private readonly List<string> _network = [];
    private readonly Dictionary<string, (string Method, string Url)> _requests = [];
    private readonly Dictionary<string, string> _downloads = [];
    private CdpConnection? _cdp;
    private CdpPage? _page;
    private ViewportPreset? _device;
    private (int Width, int Height)? _customSize;
    private string? _colorScheme;
    private string? _reducedMotion;
    private bool _offline;
    private bool _acceptDialogs = true;
    private string? _promptText;

    private static string DownloadDirectory => Path.Combine(CodalePaths.TempDirectory, "downloads");

    /// <summary>
    /// Codale Browser's executable: CODALE_BROWSER_EXE, else the copy the app ships beside this
    /// server (…\Codale.Browser\Codale.Browser.exe next to …\Codale.Mcp.Browser\).
    /// </summary>
    public static string? BrowserExecutable
    {
        get
        {
            if (Environment.GetEnvironmentVariable("CODALE_BROWSER_EXE") is { Length: > 0 } configured) return configured;
            var bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Codale.Browser", "Codale.Browser.exe"));
            return File.Exists(bundled) ? bundled : null;
        }
    }

    // ---- Attaching ---------------------------------------------------------------------

    private async Task<bool> PortOpenAsync()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(300);
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, DebuggingPort, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Which agent session this server works for. The app sets CODALE_BROWSER_SESSION per chat, so
    /// a chat keeps one tab even when its CLI restarts this server; without it the process is the session.
    /// </summary>
    public string SessionKey { get; init; } = Environment.GetEnvironmentVariable("CODALE_BROWSER_SESSION") is { Length: > 0 } key
        ? key : $"process-{Environment.ProcessId}";

    /// <summary>The name on the session's tab (CODALE_BROWSER_LABEL), e.g. the CLI and chat title.</summary>
    public string SessionLabel { get; init; } = Environment.GetEnvironmentVariable("CODALE_BROWSER_LABEL") is { Length: > 0 } label ? label : "Agent";

    /// <summary>Codale Browser's per-user request channel (Codale.Browser, ControlPipe): the same name is spelled out there.</summary>
    public static string ControlPipeName => "Codale.Browser.Control." + Environment.UserName;

    /// <summary>
    /// Sends one request to Codale Browser's control pipe and returns the answer, or null when no
    /// Codale Browser is answering there (another Chromium on the port, or one still starting up).
    /// </summary>
    private async Task<JsonElement?> ControlAsync(object request, TimeSpan timeout)
    {
        // The pipe belongs to the window on the standard port; a browser on any other port is not it.
        if (DebuggingPort != AttachPort) return null;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", ControlPipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cts.Token).ConfigureAwait(false);
            if (await reader.ReadLineAsync(cts.Token).ConfigureAwait(false) is not { } line) return null;
            using var answer = JsonDocument.Parse(line);
            return answer.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>This session's own tab in Codale Browser (created on first use), or null when no tab service answers.</summary>
    private async Task<string?> RequestTabAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            var answer = await ControlAsync(new { op = "open", session = SessionKey, label = SessionLabel }, remaining);
            if (answer is null) return null;
            if (answer.Value.TryGetProperty("targetId", out var id) && id.GetString() is { Length: > 0 } targetId) return targetId;
            if (answer.Value.TryGetProperty("error", out var error) && !error.GetString()!.Contains("starting", StringComparison.Ordinal))
            {
                throw new McpToolException($"Codale Browser could not open a tab: {error.GetString()}");
            }

            await Task.Delay(250);
        }
    }

    /// <summary>
    /// A page target's WebSocket address: the one with <paramref name="targetId"/>, or (when null)
    /// the first page - for a Chromium that is not Codale Browser. Null while it is not listed yet.
    /// </summary>
    private async Task<Uri?> FindPageAsync(string? targetId = null)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://127.0.0.1:{DebuggingPort}/json/list").ConfigureAwait(false);
            using var targets = JsonDocument.Parse(json);
            foreach (var target in targets.RootElement.EnumerateArray())
            {
                if (target.GetProperty("type").GetString() != "page") continue;
                if (targetId is not null && target.GetProperty("id").GetString() != targetId) continue;
                if (target.TryGetProperty("url", out var url) && url.GetString()?.StartsWith("devtools://", StringComparison.Ordinal) == true) continue;
                if (target.TryGetProperty("webSocketDebuggerUrl", out var ws) && ws.GetString() is { Length: > 0 } address) return new Uri(address);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Not up yet.
        }

        return null;
    }

    private void StartBrowser()
    {
        var exe = BrowserExecutable
            ?? throw new McpToolException("Codale Browser is not installed with this build. Open it from Codale (Open browser) and try again.");
        try
        {
            // Same launch as the app's Open browser: the data root goes over by file (the shell cannot pass env), and
            // Explorer starts the browser. A direct child of the packaged app inherits its package identity, which the
            // unpackaged browser cannot start under.
            var handoff = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codale", "browser-launch.json");
            Directory.CreateDirectory(Path.GetDirectoryName(handoff)!);
            File.WriteAllText(handoff, JsonSerializer.Serialize(new Dictionary<string, string> { ["dataRoot"] = CodalePaths.DataRoot }));

            using var process = Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{exe}\"", UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new McpToolException($"Could not start Codale Browser ({exe}): {ex.Message}");
        }
    }

    /// <summary>The page, attaching to Codale Browser (and starting it) when there is no live connection.</summary>
    private async Task<CdpPage> EnsurePageAsync()
    {
        if (_cdp is { IsOpen: true } && _page is not null) return _page;
        await DropConnectionAsync();

        var started = false;
        if (!await PortOpenAsync())
        {
            StartBrowser();
            started = true;
        }

        // Each session drives a tab of its own. Codale Browser hands it out over its control pipe;
        // a plain Chromium on the port (no pipe) is driven through its first page instead.
        var targetId = await RequestTabAsync(started ? StartupTimeout : TimeSpan.FromSeconds(3));
        var deadline = DateTime.UtcNow + (started ? StartupTimeout : TimeSpan.FromSeconds(5));
        Uri? address;
        while ((address = await FindPageAsync(targetId)) is null)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new McpToolException(started
                    ? "Codale Browser did not come up in time. Check that its window opened (WebView2 must be installed), then try again."
                    : $"Something is listening on port {DebuggingPort} but it has no page to drive. Close it, or open Codale Browser from Codale.");
            }

            await Task.Delay(250);
        }

        // Anything on the loopback port that serves a page is trusted as Codale Browser: a hostile
        // local process could squat on it, but the window is the only thing meant to listen there.
        var cdp = await CdpConnection.ConnectAsync(address);
        Wire(cdp);
        await cdp.SendAsync("Page.enable");
        await cdp.SendAsync("Runtime.enable");
        await cdp.SendAsync("Network.enable");
        try
        {
            Directory.CreateDirectory(DownloadDirectory);
            await cdp.SendAsync("Browser.setDownloadBehavior", new { behavior = "allowAndName", downloadPath = DownloadDirectory, eventsEnabled = true });
        }
        catch (Exception ex) when (ex is CdpException or IOException or UnauthorizedAccessException)
        {
            // The window's own download handling stays in charge.
        }

        _cdp = cdp;
        _page = new CdpPage(cdp);
        await ApplyEmulationAsync(_page, reload: false);
        return _page;
    }

    private void Wire(CdpConnection cdp)
    {
        cdp.On("Runtime.consoleAPICalled", e =>
        {
            var text = e.TryGetProperty("args", out var args)
                ? string.Join(" ", args.EnumerateArray().Select(a => a.TryGetProperty("value", out var v) ? v.ToString() : a.TryGetProperty("description", out var d) ? d.GetString() : ""))
                : "";
            Append(_console, $"[{e.GetProperty("type").GetString()}] {text}");
        });
        cdp.On("Runtime.exceptionThrown", e =>
        {
            var details = e.GetProperty("exceptionDetails");
            var text = details.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d) ? d.GetString() : details.GetProperty("text").GetString();
            Append(_console, $"[pageerror] {text}");
        });
        cdp.On("Network.requestWillBeSent", e =>
        {
            var request = e.GetProperty("request");
            lock (_requests)
            {
                _requests[e.GetProperty("requestId").GetString() ?? ""] = (request.GetProperty("method").GetString() ?? "", request.GetProperty("url").GetString() ?? "");
                if (_requests.Count > 2000) _requests.Clear();
            }
        });
        cdp.On("Network.responseReceived", e =>
        {
            var response = e.GetProperty("response");
            var method = Request(e).Method;
            Append(_network, $"{response.GetProperty("status").GetInt32()} {method} {response.GetProperty("url").GetString()}".Replace("  ", " "));
        });
        cdp.On("Network.loadingFailed", e =>
        {
            var (method, url) = Request(e);
            Append(_network, $"FAILED {method} {url} {e.GetProperty("errorText").GetString()}");
        });
        cdp.On("Page.javascriptDialogOpening", e =>
        {
            var accept = _acceptDialogs;
            Append(_console, $"[dialog:{e.GetProperty("type").GetString()}] {e.GetProperty("message").GetString()} -> {(accept ? "accepted" : "dismissed")}");

            // Handlers run on the receive loop, which must stay free to read the reply.
            _ = Task.Run(async () =>
            {
                try { await cdp.SendAsync("Page.handleJavaScriptDialog", new { accept, promptText = accept ? _promptText : null }); }
                catch (CdpException) { /* the page moved on */ }
            });
        });
        cdp.On("Browser.downloadWillBegin", e =>
        {
            lock (_downloads) _downloads[e.GetProperty("guid").GetString() ?? ""] = e.GetProperty("suggestedFilename").GetString() ?? "download";
        });
        cdp.On("Browser.downloadProgress", e =>
        {
            var state = e.GetProperty("state").GetString();
            if (state is not ("completed" or "canceled")) return;
            string? name;
            var guid = e.GetProperty("guid").GetString() ?? "";
            lock (_downloads) { _downloads.Remove(guid, out name); }
            name ??= "download";
            if (state == "canceled") { Append(_console, $"[download] {name} was cancelled"); return; }
            try
            {
                var path = UniqueDownloadPath(name);
                File.Move(Path.Combine(DownloadDirectory, guid), path);
                Append(_console, $"[download] {name} saved to {path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Append(_console, $"[download] {name} failed: {ex.Message}");
            }
        });
    }

    private (string Method, string Url) Request(JsonElement e)
    {
        lock (_requests) return _requests.TryGetValue(e.GetProperty("requestId").GetString() ?? "", out var r) ? r : ("", "");
    }

    /// <summary>Lets go of the window. Closing the connection drops every override it set.</summary>
    private async Task DropConnectionAsync()
    {
        if (_cdp is not null) await _cdp.DisposeAsync();
        _cdp = null;
        _page = null;
        lock (_requests) _requests.Clear();
    }

    /// <summary>
    /// A free path in the download folder for a server-suggested name. The name is page
    /// controlled, so only its last segment is used (no "..\" out of the folder) and an
    /// existing file is never overwritten.
    /// </summary>
    private static string UniqueDownloadPath(string suggested)
    {
        var name = Path.GetFileName(suggested);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            name = "download";
        }

        var path = Path.Combine(DownloadDirectory, name);
        for (var i = 1; File.Exists(path); i++)
        {
            path = Path.Combine(DownloadDirectory, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        }

        return path;
    }

    /// <summary>
    /// The address, only if it is a web page (http, https) or about:blank. file:, javascript:,
    /// data: and the like would let a prompt-injected page or model read local files or
    /// run script in the browser's privileged contexts.
    /// </summary>
    public static string RequireWebUrl(string url)
    {
        return !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "about")
            ? throw new McpToolException($"Only http, https and about: addresses can be opened, not '{url}'.")
            : url.Trim();
    }

    private static void Append(List<string> log, string entry)
    {
        lock (log)
        {
            log.Add(entry);
            if (log.Count > MaxLogEntries) log.RemoveAt(0);
        }
    }

    /// <summary>Runs a tool against the page. All tool work is serialized through the gate.</summary>
    public async Task<T> WithPageAsync<T>(Func<CdpPage, Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            return await action(await EnsurePageAsync());
        }
        catch (CdpException ex)
        {
            throw new McpToolException(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Device and media emulation ------------------------------------------------------

    /// <summary>
    /// Switches to a device preset, a custom size, or (both null) back to the window's own size.
    /// A change of touch, pixel ratio or user agent reloads the page so it sees the new device.
    /// </summary>
    public Task<string> SetViewportAsync(ViewportPreset? device, (int Width, int Height)? size) => WithPageAsync(async page =>
    {
        await ApplyDeviceAsync(page, device, size);
        return device is not null
            ? $"Viewport: {device}."
            : size is { } s ? $"Viewport: {s.Width}x{s.Height}." : "Viewport reset to the window's size.";
    });

    private async Task ApplyDeviceAsync(CdpPage page, ViewportPreset? device, (int Width, int Height)? size)
    {
        var before = _device;
        _device = device;
        _customSize = device is null ? size : null;
        var reload = (before, device) switch
        {
            (null, null) => false,
            ({ } a, { } b) => !a.SameProfileAs(b),
            ({ Mobile: false, UserAgent: null }, null) or (null, { Mobile: false, UserAgent: null }) => false,
            _ => true,
        };
        await ApplyEmulationAsync(page, reload);
    }

    /// <summary>Puts this session's device and media settings on the page (a new connection starts clean).</summary>
    private async Task ApplyEmulationAsync(CdpPage page, bool reload)
    {
        var cdp = page.Cdp;
        if (_device is { } d)
        {
            await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new { width = d.Width, height = d.Height, deviceScaleFactor = d.Scale, mobile = d.Mobile });
        }
        else if (_customSize is { } c)
        {
            await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new { width = c.Width, height = c.Height, deviceScaleFactor = 0, mobile = false });
        }
        else
        {
            await cdp.SendAsync("Emulation.clearDeviceMetricsOverride");
        }

        var mobile = _device?.Mobile == true;
        await cdp.SendAsync("Emulation.setTouchEmulationEnabled", new { enabled = mobile, maxTouchPoints = mobile ? 5 : 1 });
        await cdp.SendAsync("Emulation.setUserAgentOverride", new { userAgent = _device?.UserAgent ?? "" });
        await cdp.SendAsync("Emulation.setEmulatedMedia", new
        {
            features = new[]
            {
                new { name = "prefers-color-scheme", value = _colorScheme ?? "" },
                new { name = "prefers-reduced-motion", value = _reducedMotion ?? "" },
            },
        });
        if (_offline)
        {
            await cdp.SendAsync("Network.emulateNetworkConditions", new { offline = true, latency = 0, downloadThroughput = -1, uploadThroughput = -1 });
        }

        if (reload && !(await page.UrlAsync()).StartsWith("about:", StringComparison.Ordinal))
        {
            try { await page.ReloadAsync(); }
            catch (McpToolException) { /* keep the new profile even when the page will not come back */ }
        }
    }

    /// <summary>Releases the agent's viewport override (end of a turn) without starting a browser.</summary>
    public async Task RestoreViewportAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_device is null && _customSize is null) return;
            if (_page is null || _cdp is not { IsOpen: true })
            {
                _device = null;
                _customSize = null;
                return;
            }

            await ApplyDeviceAsync(_page, null, null);
        }
        catch (Exception ex) when (ex is CdpException or McpToolException)
        {
            // The window went away; nothing is left to restore.
        }
        finally { _gate.Release(); }
    }

    public Task<string> EmulateAsync(string? colorScheme, string? reducedMotion, bool? offline) => WithPageAsync(async page =>
    {
        var notes = new List<string>();

        if (colorScheme is { Length: > 0 })
        {
            _colorScheme = colorScheme.ToLowerInvariant() switch
            {
                "dark" => "dark",
                "light" => "light",
                "none" or "default" or "no-preference" => null,
                var other => throw new McpToolException($"Unknown colorScheme '{other}': use dark, light or default."),
            };
            notes.Add($"colorScheme={colorScheme}");
        }

        if (reducedMotion is { Length: > 0 })
        {
            _reducedMotion = reducedMotion.ToLowerInvariant() switch
            {
                "reduce" => "reduce",
                "none" or "default" or "no-preference" => null,
                var other => throw new McpToolException($"Unknown reducedMotion '{other}': use reduce or default."),
            };
            notes.Add($"reducedMotion={reducedMotion}");
        }

        if (offline is { } off)
        {
            _offline = off;
            await page.Cdp.SendAsync("Network.emulateNetworkConditions", new { offline = off, latency = 0, downloadThroughput = -1, uploadThroughput = -1 });
            notes.Add($"offline={off}");
        }

        await ApplyEmulationAsync(page, reload: false);
        return notes.Count == 0 ? "Nothing to change." : "Emulating " + string.Join(", ", notes) + ".";
    });

    /// <summary>A screenshot at each preset, then the previous viewport restored: the quick responsive check.</summary>
    public Task<IReadOnlyList<McpContent>> ResponsiveShotsAsync(IReadOnlyList<ViewportPreset> presets, bool fullPage) => WithPageAsync(async page =>
    {
        if ((await page.UrlAsync()).StartsWith("about:", StringComparison.Ordinal))
        {
            throw new McpToolException("Open a page first (browser_navigate).");
        }

        var previousDevice = _device;
        var previousSize = _customSize;
        var results = new List<McpContent>();
        try
        {
            foreach (var preset in presets)
            {
                await ApplyDeviceAsync(page, preset, null);
                await Task.Delay(400);
                var bytes = await page.ScreenshotAsync(fullPage, jpeg: true);
                results.Add(McpContent.FromText($"{preset} - {await page.UrlAsync()}"));
                results.Add(McpContent.FromImage(bytes, "image/jpeg"));
            }
        }
        finally
        {
            await ApplyDeviceAsync(page, previousDevice, previousSize);
        }

        return (IReadOnlyList<McpContent>)results;
    });

    // ---- Handing a step to the user -----------------------------------------------------

    /// <summary>
    /// Lets the user do something only they can - sign in, pass a captcha, approve an SSO
    /// prompt - in the Codale Browser window, then carries on once the page shows it is done.
    /// </summary>
    public Task<string> HandoffAsync(string? url, string? untilUrl, string? untilText, int timeoutSeconds, CancellationToken ct = default) => WithPageAsync(async page =>
    {
        if (url is { Length: > 0 })
        {
            await page.NavigateAsync(RequireWebUrl(url));
        }

        // Show the user this session's tab and raise the window; a plain Chromium only gets bringToFront.
        if (await ControlAsync(new { op = "focus", session = SessionKey }, TimeSpan.FromSeconds(3)) is null)
        {
            try { await page.Cdp.SendAsync("Page.bringToFront"); } catch (CdpException) { }
        }
        var startUrl = await page.UrlAsync();
        var sawPassword = false;
        var satisfied = 0;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 10, 900));
        var done = false;
        var where = startUrl;

        while (DateTime.UtcNow < deadline && !done)
        {
            await Task.Delay(700, ct);
            if (_cdp is not { IsOpen: true })
            {
                throw new McpToolException("The Codale Browser window was closed before the user finished.");
            }

            try
            {
                where = await page.UrlAsync();
                var hasPassword = await page.EvaluateAsync(PasswordVisibleScript) is { ValueKind: JsonValueKind.True };
                sawPassword |= hasPassword;

                var ok = untilUrl is { Length: > 0 }
                    ? where.Contains(untilUrl, StringComparison.OrdinalIgnoreCase)
                    : untilText is { Length: > 0 }
                        ? await page.EvaluateAsync($"document.body.innerText.includes({JsonSerializer.Serialize(untilText)})") is { ValueKind: JsonValueKind.True }
                        : sawPassword ? !hasPassword : where != startUrl;

                // Two quiet polls in a row: a redirect chain or a spinner is not "finished".
                satisfied = ok ? satisfied + 1 : 0;
                done = satisfied >= 2;
            }
            catch (McpToolException)
            {
                satisfied = 0; // mid-navigation; look again
            }
        }

        return done
            ? $"The user finished. Current page: {where}"
            : $"Timed out waiting for the user; Codale Browser is still open at {where}. Try browser_handoff again or continue without signing in.";
    });

    public Task<string> SessionAsync(string action) => WithPageAsync(async page =>
    {
        switch (action)
        {
            case "save":
                return "Codale Browser keeps logins in its own profile as they happen; there is nothing to save.";

            case "clear":
                await page.Cdp.SendAsync("Network.clearBrowserCookies");
                return "Cleared all cookies in Codale Browser. Local storage of open pages is untouched until they reload.";

            case "info":
                var cookies = (await page.Cdp.SendAsync("Network.getAllCookies")).GetProperty("cookies");
                var domains = cookies.EnumerateArray().Select(c => (c.GetProperty("domain").GetString() ?? "").TrimStart('.')).Distinct().Order().ToList();
                return $"{cookies.GetArrayLength()} cookies across {domains.Count} domains" +
                    (domains.Count > 0 ? ": " + string.Join(", ", domains.Take(30)) : "");

            default:
                throw new McpToolException("action must be save, clear or info.");
        }
    });

    public void SetDialogPolicy(bool accept, string? promptText)
    {
        _acceptDialogs = accept;
        _promptText = promptText;
    }

    private const string PasswordVisibleScript =
        "[...document.querySelectorAll('input[type=password]')].some(e => { const r = e.getBoundingClientRect(), s = getComputedStyle(e); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; })";

    /// <summary>The current page's login-wall tell: a visible password field, or a sign-in style address.</summary>
    public static async Task<bool> LooksLikeLoginAsync(CdpPage page)
    {
        try
        {
            if (await page.EvaluateAsync(PasswordVisibleScript) is { ValueKind: JsonValueKind.True }) return true;
            return System.Text.RegularExpressions.Regex.IsMatch(await page.UrlAsync(), @"/(log-?in|sign-?in|sso|oauth2?|auth(?:orize)?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch (McpToolException)
        {
            return false;
        }
    }

    public string ConsoleLog(bool clear) => Drain(_console, clear);

    public string NetworkLog(bool clear) => Drain(_network, clear);

    private static string Drain(List<string> log, bool clear)
    {
        lock (log)
        {
            var text = log.Count == 0 ? "(none)" : string.Join('\n', log);
            if (clear) log.Clear();
            return text;
        }
    }

    /// <summary>
    /// Stamps a data-codale-ref on every visible interactive element and returns an
    /// outline of the page: headings, text blocks and refs the model can act on.
    /// </summary>
    public const string SnapshotScript = """
        () => {
          document.querySelectorAll('[data-codale-ref]').forEach(e => e.removeAttribute('data-codale-ref'));
          const interactive = 'a[href],button,input,select,textarea,summary,[role=button],[role=link],[role=checkbox],[role=tab],[role=menuitem],[role=switch],[role=radio],[role=combobox],[contenteditable=""],[contenteditable=true],[onclick],[tabindex]:not([tabindex="-1"])';
          const visible = el => {
            const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
          };
          const secret = el => el.type === 'password';
          const label = el => (el.getAttribute('aria-label') || el.innerText || (secret(el) ? '' : el.value) || el.placeholder || el.title || el.alt || '').trim().replace(/\s+/g, ' ').slice(0, 80);
          let n = 0; const lines = [];
          const walk = (node, depth) => {
            for (const el of node.children) {
              if (['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE'].includes(el.tagName) || !visible(el)) continue;
              const tag = el.tagName.toLowerCase();
              const pad = '  '.repeat(Math.min(depth, 8));
              if (el.matches(interactive)) {
                const ref = 'e' + (++n); el.setAttribute('data-codale-ref', ref);
                const type = el.getAttribute('type'); const role = el.getAttribute('role');
                let extra = '';
                if (el.disabled) extra += ' disabled';
                if (el.checked) extra += ' checked';
                if (tag === 'a') extra += ' href=' + (el.getAttribute('href') || '').slice(0, 80);
                if ((tag === 'input' || tag === 'textarea') && !secret(el)) extra += ' value="' + (el.value || '').slice(0, 40) + '"';
                lines.push(pad + '[' + ref + '] ' + (role || tag) + (type ? '(' + type + ')' : '') + ' "' + label(el) + '"' + extra);
                if (!el.children.length || tag === 'a' || tag === 'button') continue;
              } else if (/^h[1-6]$/.test(tag)) {
                lines.push(pad + '# ' + label(el));
                continue;
              } else if (el.children.length === 0 && el.innerText && el.innerText.trim()) {
                lines.push(pad + el.innerText.trim().replace(/\s+/g, ' ').slice(0, 160));
                continue;
              }
              walk(el, depth + 1);
            }
          };
          walk(document.body, 0);
          return lines.join('\n');
        }
        """;

    /// <summary>
    /// For the browser_close tool: lets go of Codale Browser and forgets this session's emulation,
    /// waiting its turn so an in-flight tool is not torn down mid-call. The window stays open.
    /// </summary>
    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DropConnectionAsync();
            _device = null;
            _customSize = null;
            _colorScheme = null;
            _reducedMotion = null;
            _offline = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        CleanupDownloads();
    }

    private static void CleanupDownloads()
    {
        try
        {
            if (Directory.Exists(DownloadDirectory))
            {
                var cutoff = DateTime.UtcNow.AddHours(-24);
                foreach (var file in Directory.EnumerateFiles(DownloadDirectory))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
