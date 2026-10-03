using System.Text;

using Microsoft.Playwright;

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

    /// <summary>True when switching between the two needs no new context (only the size differs).</summary>
    public bool SameProfileAs(ViewportPreset other) =>
        Scale == other.Scale && Mobile == other.Mobile && UserAgent == other.UserAgent;

    public override string ToString() => $"{Name} {Width}x{Height}@{Scale}x{(Mobile ? " mobile+touch" : "")}";
}

/// <summary>
/// One lazily-launched browser (the installed Edge, then Chrome, then bundled Chromium)
/// with a set of tabs, or - when the user opened one with the app's "Open browser" button -
/// that browser, attached over its debugging port. Headless by default. Logins survive
/// restarts and headless/visible switches through a saved storage-state file. Element
/// handles are stable refs stamped onto the DOM by <see cref="SnapshotScript"/>.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private const int MaxLogEntries = 200;

    /// <summary>
    /// The port a browser the user opened by hand listens on for automation. The app's
    /// "Open browser" button launches Edge with it; see <see cref="McpDefaults.BrowserAttachPort"/>.
    /// </summary>
    public const int AttachPort = McpDefaults.BrowserAttachPort;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IPage> _pages = [];
    private readonly Dictionary<IPage, List<string>> _console = [];
    private readonly Dictionary<IPage, List<string>> _network = [];
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private int _current;
    private bool _attached;
    private bool? _headlessOverride;
    private ViewportPreset? _device;
    private (int Width, int Height)? _customSize;
    private ColorScheme? _colorScheme;
    private ReducedMotion? _reducedMotion;
    private bool _offline;
    private bool _acceptDialogs = true;
    private string? _promptText;

    /// <summary>Headless unless CODALE_BROWSER_HEADLESS=0, so agent work never pops a window up on its own.</summary>
    public bool Headless { get; init; } = Environment.GetEnvironmentVariable("CODALE_BROWSER_HEADLESS") != "0";

    private bool CurrentHeadless => _headlessOverride ?? Headless;

    /// <summary>
    /// Cookies and local storage saved between runs so a login only has to happen once. The file
    /// is plaintext session data, protected only by the per-user ACL of the local app-data folder.
    /// </summary>
    public static string StatePath { get; } = Environment.GetEnvironmentVariable("CODALE_BROWSER_STATE") is { Length: > 0 } p
        ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale", "browser-state.json");

    private static string DownloadDirectory =>
        Path.Combine(Path.GetTempPath(), "codale-downloads");

    private static async Task<bool> AttachAvailableAsync()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(300);
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, AttachPort, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    private bool IsAlive => _context is not null && _browser is { IsConnected: true };

    private async Task<IBrowserContext> EnsureContextAsync()
    {
        var attachable = await AttachAvailableAsync();

        // A browser the user opened after we went headless takes over; the headless one is dropped.
        if (IsAlive && (_attached || !attachable))
        {
            return _context!;
        }

        // A crashed browser gets relaunched, but its old process, context and tracked
        // tabs must not be abandoned - each crash/close cycle would leak all three.
        await CloseBrowserAsync(saveState: false);

        _playwright ??= await Playwright.CreateAsync();

        // Anything listening on the attach port is trusted as the user's own browser: the
        // port is loopback-only, but a hostile local process could squat on it. Kept as is
        // on purpose - the "Open browser" button depends on it.
        if (attachable && await TryAttachAsync())
        {
            return _context!;
        }

        await LaunchAsync();
        _context = await CreateContextAsync();
        ResetTabs();
        return _context;
    }

    private async Task<bool> TryAttachAsync()
    {
        try
        {
            _browser = await _playwright!.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{AttachPort}");
            _attached = true;
            _context = _browser.Contexts.Count > 0
                ? _browser.Contexts[0]
                : await _browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = ViewportSize.NoViewport });
            ResetTabs();
            foreach (var existing in _context.Pages)
            {
                Adopt(existing);
            }

            _current = Math.Max(0, _pages.Count - 1);
            return true;
        }
        catch (PlaywrightException)
        {
            _browser = null;
            _context = null;
            _attached = false;
            return false;
        }
    }

    private async Task LaunchAsync()
    {
        var errors = new StringBuilder();
        foreach (var channel in new string?[] { "msedge", "chrome", null })
        {
            try
            {
                _browser = await _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Channel = channel,
                    Headless = CurrentHeadless,
                });
                _attached = false;
                return;
            }
            catch (PlaywrightException ex)
            {
                errors.AppendLine($"{channel ?? "chromium"}: {ex.Message.Split('\n')[0]}");
            }
        }

        throw new McpToolException("Could not launch a browser. Install Microsoft Edge or Chrome.\n" + errors);
    }

    /// <summary>A context carrying the saved logins and the current device emulation.</summary>
    private async Task<IBrowserContext> CreateContextAsync()
    {
        var options = new BrowserNewContextOptions { AcceptDownloads = true };

        if (_device is { } d)
        {
            options.ViewportSize = new ViewportSize { Width = d.Width, Height = d.Height };
            options.DeviceScaleFactor = (float)d.Scale;
            options.IsMobile = d.Mobile;
            options.HasTouch = d.Mobile;
            options.UserAgent = d.UserAgent;
        }
        else if (_customSize is { } c)
        {
            options.ViewportSize = new ViewportSize { Width = c.Width, Height = c.Height };
        }
        else
        {
            // Headless has no window to follow, so it gets a fixed size; a visible one follows its window.
            options.ViewportSize = CurrentHeadless ? new ViewportSize { Width = 1280, Height = 800 } : ViewportSize.NoViewport;
        }

        if (_colorScheme is { } scheme) options.ColorScheme = scheme;
        if (_reducedMotion is { } motion) options.ReducedMotion = motion;

        if (File.Exists(StatePath))
        {
            options.StorageStatePath = StatePath;
        }

        IBrowserContext context;
        try
        {
            context = await _browser!.NewContextAsync(options);
        }
        catch (PlaywrightException) when (options.StorageStatePath is not null)
        {
            // A damaged state file must not lock the browser out.
            options.StorageStatePath = null;
            context = await _browser!.NewContextAsync(options);
        }

        if (_offline) await context.SetOfflineAsync(true);
        return context;
    }

    private void ResetTabs()
    {
        _pages.Clear();
        _console.Clear();
        _network.Clear();
        _current = 0;
    }

    /// <summary>Saves the login state, then closes the browser (or lets go of an attached one).</summary>
    private async Task CloseBrowserAsync(bool saveState)
    {
        if (saveState)
        {
            await SaveStateAsync();
        }

        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); }
            catch (Exception ex) when (ex is PlaywrightException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Already gone; that is why we are here.
            }
        }

        _browser = null;
        _context = null;
        _attached = false;
        ResetTabs();
    }

    private async Task SaveStateAsync()
    {
        if (_context is null || _attached || _browser is not { IsConnected: true })
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            await _context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = StatePath });
        }
        catch (Exception ex) when (ex is PlaywrightException or IOException or UnauthorizedAccessException)
        {
            // Losing the saved login is an inconvenience, never a failure of the tool call.
        }
    }

    private void Adopt(IPage page)
    {
        page.SetDefaultTimeout(15_000);
        Track(page);
    }

    private void Track(IPage page)
    {
        _pages.Add(page);
        var console = _console[page] = [];
        var network = _network[page] = [];
        page.Console += (_, m) => Append(console, $"[{m.Type}] {m.Text}");
        page.PageError += (_, e) => Append(console, $"[pageerror] {e}");
        page.Response += (_, r) => Append(network, $"{r.Status} {r.Request.Method} {r.Url}");
        page.RequestFailed += (_, r) => Append(network, $"FAILED {r.Method} {r.Url} {r.Failure}");
        page.Dialog += async (_, d) =>
        {
            Append(console, $"[dialog:{d.Type}] {d.Message} -> {(_acceptDialogs ? "accepted" : "dismissed")}");
            try
            {
                if (_acceptDialogs) await d.AcceptAsync(_promptText); else await d.DismissAsync();
            }
            catch (PlaywrightException) { /* the page moved on */ }
        };
        page.Download += async (_, d) =>
        {
            try
            {
                Directory.CreateDirectory(DownloadDirectory);
                var path = UniqueDownloadPath(d.SuggestedFilename);
                await d.SaveAsAsync(path);
                Append(console, $"[download] {d.SuggestedFilename} saved to {path}");
            }
            catch (Exception ex) when (ex is PlaywrightException or IOException)
            {
                Append(console, $"[download] {d.SuggestedFilename} failed: {ex.Message}");
            }
        };
        page.Close += (_, _) =>
        {
            var idx = _pages.IndexOf(page);
            _pages.Remove(page);
            _console.Remove(page);
            _network.Remove(page);
            if (idx >= 0 && _current >= idx && _current > 0) _current--;
        };
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
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "about"))
        {
            throw new McpToolException($"Only http, https and about: addresses can be opened, not '{url}'.");
        }

        return url.Trim();
    }

    private static void Append(List<string> log, string entry)
    {
        lock (log)
        {
            log.Add(entry);
            if (log.Count > MaxLogEntries) log.RemoveAt(0);
        }
    }

    private async Task<IPage> NewPageAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        Adopt(page);
        await ApplyMediaAsync(page);
        return page;
    }

    private async Task ApplyMediaAsync(IPage page)
    {
        if (_colorScheme is null && _reducedMotion is null)
        {
            return;
        }

        await page.EmulateMediaAsync(new PageEmulateMediaOptions
        {
            ColorScheme = _colorScheme,
            ReducedMotion = _reducedMotion,
        });
    }

    /// <summary>The active page, opening one if none exists. All tool work is serialized through the gate.</summary>
    private async Task<IPage> CurrentPageAsync()
    {
        var context = await EnsureContextAsync();
        if (_pages.Count == 0)
        {
            await NewPageAsync(context);
            _current = 0;
        }

        return _pages[Math.Clamp(_current, 0, _pages.Count - 1)];
    }

    public async Task<T> WithPageAsync<T>(Func<IPage, Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            return await action(await CurrentPageAsync());
        }
        catch (PlaywrightException ex)
        {
            throw new McpToolException(ex.Message.Split('\n')[0]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> WithSessionAsync<T>(Func<IBrowserContext, Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            return await action(await EnsureContextAsync());
        }
        catch (PlaywrightException ex)
        {
            throw new McpToolException(ex.Message.Split('\n')[0]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<string> ListTabsAsync() => WithSessionAsync(async _ =>
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _pages.Count; i++)
        {
            sb.AppendLine($"{(i == _current ? "*" : " ")} [{i}] {await _pages[i].TitleAsync()} - {_pages[i].Url}");
        }

        return sb.Length == 0 ? "(no tabs)" : sb.ToString().TrimEnd();
    });

    public Task<string> NewTabAsync(string? url) => WithSessionAsync(async ctx =>
    {
        var page = await NewPageAsync(ctx);
        _current = _pages.Count - 1;
        if (url is { Length: > 0 })
        {
            await page.GotoAsync(RequireWebUrl(url));
        }

        return $"Opened tab [{_current}].";
    });

    public Task<string> SelectTabAsync(int index) => WithSessionAsync(async _ =>
    {
        if (index < 0 || index >= _pages.Count) throw new McpToolException($"No tab [{index}].");
        _current = index;
        await _pages[index].BringToFrontAsync();
        return $"Selected tab [{index}].";
    });

    public Task<string> CloseTabAsync(int? index) => WithSessionAsync(async _ =>
    {
        var i = index ?? _current;
        if (i < 0 || i >= _pages.Count) throw new McpToolException($"No tab [{i}].");
        await _pages[i].CloseAsync();
        return $"Closed tab [{i}].";
    });

    // ---- Device emulation -------------------------------------------------------------

    /// <summary>
    /// Switches to a device preset, a custom size, or (both null) back to the default. A
    /// change of touch/pixel-ratio/user agent needs a fresh context, so the tabs reopen at
    /// their URLs with the saved logins; a plain size change resizes in place.
    /// </summary>
    public Task<string> SetViewportAsync(ViewportPreset? device, (int Width, int Height)? size) => WithSessionAsync(async _ =>
    {
        await ApplyDeviceAsync(device, size);
        return device is not null
            ? $"Viewport: {device}."
            : size is { } s ? $"Viewport: {s.Width}x{s.Height}." : "Viewport reset to the default.";
    });

    private async Task ApplyDeviceAsync(ViewportPreset? device, (int Width, int Height)? size)
    {
        var previous = _device;
        _device = device;
        _customSize = device is null ? size : null;

        var target = device is not null ? (device.Width, device.Height) : size;

        if (_attached)
        {
            // The user's own browser: only the size can change, without touching their profile.
            if (target is { } t)
            {
                foreach (var page in _pages)
                {
                    await page.SetViewportSizeAsync(t.Item1, t.Item2);
                }
            }

            return;
        }

        // Only the size differs (or a plain size is asked for while no device is active): resize in place.
        var resizeInPlace = target is not null &&
            ((previous is not null && device is not null && previous.SameProfileAs(device)) ||
             (previous is null && device is null));
        if (resizeInPlace)
        {
            foreach (var page in _pages)
            {
                await page.SetViewportSizeAsync(target!.Value.Item1, target.Value.Item2);
            }

            return;
        }

        await RecreateContextAsync();
    }

    /// <summary>A fresh context (same browser, same logins) with its tabs reopened where they were.</summary>
    private async Task RecreateContextAsync()
    {
        var urls = _pages.Select(p => p.Url).ToList();
        var current = _current;
        await SaveStateAsync();

        try { await _context!.CloseAsync(); }
        catch (PlaywrightException) { /* already closed */ }

        _context = await CreateContextAsync();
        ResetTabs();
        await ReopenAsync(urls, current);
    }

    private async Task ReopenAsync(List<string> urls, int current)
    {
        foreach (var url in urls)
        {
            var page = await NewPageAsync(_context!);
            if (url is { Length: > 0 } && !url.StartsWith("about:", StringComparison.Ordinal))
            {
                try { await page.GotoAsync(url); }
                catch (PlaywrightException) { /* keep the tab even when the page is unreachable */ }
            }
        }

        _current = Math.Clamp(current, 0, Math.Max(0, _pages.Count - 1));
    }

    public Task<string> EmulateAsync(string? colorScheme, string? reducedMotion, bool? offline) => WithSessionAsync(async ctx =>
    {
        var notes = new List<string>();

        if (colorScheme is { Length: > 0 })
        {
            _colorScheme = colorScheme.ToLowerInvariant() switch
            {
                "dark" => ColorScheme.Dark,
                "light" => ColorScheme.Light,
                "none" or "default" or "no-preference" => null,
                var other => throw new McpToolException($"Unknown colorScheme '{other}': use dark, light or default."),
            };
            notes.Add($"colorScheme={colorScheme}");
        }

        if (reducedMotion is { Length: > 0 })
        {
            _reducedMotion = reducedMotion.ToLowerInvariant() switch
            {
                "reduce" => ReducedMotion.Reduce,
                "none" or "default" or "no-preference" => null,
                var other => throw new McpToolException($"Unknown reducedMotion '{other}': use reduce or default."),
            };
            notes.Add($"reducedMotion={reducedMotion}");
        }

        foreach (var page in _pages)
        {
            await page.EmulateMediaAsync(new PageEmulateMediaOptions
            {
                ColorScheme = _colorScheme ?? ColorScheme.NoPreference,
                ReducedMotion = _reducedMotion ?? ReducedMotion.NoPreference,
            });
        }

        if (offline is { } off)
        {
            _offline = off;
            await ctx.SetOfflineAsync(off);
            notes.Add($"offline={off}");
        }

        return notes.Count == 0 ? "Nothing to change." : "Emulating " + string.Join(", ", notes) + ".";
    });

    /// <summary>A screenshot at each preset, then the previous viewport restored: the quick responsive check.</summary>
    public Task<IReadOnlyList<McpContent>> ResponsiveShotsAsync(IReadOnlyList<ViewportPreset> presets, bool fullPage) => WithSessionAsync(async _ =>
    {
        if (_pages.Count == 0)
        {
            throw new McpToolException("Open a page first (browser_navigate).");
        }

        var previousDevice = _device;
        var previousSize = _customSize;
        var results = new List<McpContent>();

        // The user's own browser cannot be reset by "no device", so put each tab's size back by hand.
        var originalSizes = _attached ? _pages.Select(p => p.ViewportSize).ToList() : [];
        try
        {
            foreach (var preset in presets)
            {
                await ApplyDeviceAsync(preset, null);
                var page = _pages[Math.Clamp(_current, 0, _pages.Count - 1)];
                await Task.Delay(400);
                var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    FullPage = fullPage,
                    Type = ScreenshotType.Jpeg,
                    Quality = 75,
                });
                results.Add(McpContent.FromText($"{preset} - {page.Url}"));
                results.Add(McpContent.FromImage(bytes, "image/jpeg"));
            }
        }
        finally
        {
            await ApplyDeviceAsync(previousDevice, previousSize);

            for (var i = 0; i < originalSizes.Count && i < _pages.Count; i++)
            {
                if (originalSizes[i] is { } size && previousDevice is null && previousSize is null)
                {
                    await _pages[i].SetViewportSizeAsync(size.Width, size.Height);
                }
            }
        }

        return (IReadOnlyList<McpContent>)results;
    });

    // ---- Visible / hidden, and handing a login to the user ------------------------------

    /// <summary>Restarts the browser visible or hidden, keeping the tabs' URLs and the logins.</summary>
    public Task<string> SetModeAsync(bool headless) => WithSessionAsync(async _ =>
    {
        if (_attached)
        {
            throw new McpToolException("This is a browser you opened yourself, so it is already visible and stays as you left it.");
        }

        if (CurrentHeadless == headless)
        {
            return headless ? "Already hidden (headless)." : "Already visible.";
        }

        await SwitchModeAsync(headless);
        return headless ? "Now hidden (headless)." : "Now visible.";
    });

    private async Task SwitchModeAsync(bool headless)
    {
        var urls = _pages.Select(p => p.Url).ToList();
        var current = _current;
        await CloseBrowserAsync(saveState: true);

        _headlessOverride = headless;
        await LaunchAsync();
        _context = await CreateContextAsync();
        await ReopenAsync(urls, current);
    }

    /// <summary>
    /// Lets the user do something only they can - sign in, pass a captcha, approve an SSO
    /// prompt - in a visible window, then carries on. The logins are saved, so the agent's
    /// hidden browser is signed in afterwards.
    /// </summary>
    public Task<string> HandoffAsync(string? url, string? untilUrl, string? untilText, int timeoutSeconds, bool returnToHidden, CancellationToken ct = default) => WithSessionAsync(async _ =>
    {
        var wasHidden = !_attached && CurrentHeadless;
        if (wasHidden)
        {
            await SwitchModeAsync(headless: false);
        }

        IPage Active() => _pages[Math.Clamp(_current, 0, _pages.Count - 1)];

        if (_pages.Count == 0)
        {
            await NewPageAsync(_context!);
            _current = 0;
        }

        if (url is { Length: > 0 })
        {
            await Active().GotoAsync(RequireWebUrl(url));
        }

        await Active().BringToFrontAsync();
        var startUrl = Active().Url;
        var sawPassword = false;
        var satisfied = 0;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 10, 900));
        var done = false;

        while (DateTime.UtcNow < deadline && !done)
        {
            await Task.Delay(700, ct);
            if (!IsAlive || _pages.Count == 0)
            {
                throw new McpToolException("The browser window was closed before the sign-in finished.");
            }

            try
            {
                var page = Active();
                var hasPassword = await page.Locator("input[type=password]:visible").CountAsync() > 0;
                sawPassword |= hasPassword;

                var ok = untilUrl is { Length: > 0 }
                    ? page.Url.Contains(untilUrl, StringComparison.OrdinalIgnoreCase)
                    : untilText is { Length: > 0 }
                        ? await page.GetByText(untilText).First.IsVisibleAsync()
                        : sawPassword ? !hasPassword : page.Url != startUrl;

                // Two quiet polls in a row: a redirect chain or a spinner is not "finished".
                satisfied = ok ? satisfied + 1 : 0;
                done = satisfied >= 2;
            }
            catch (PlaywrightException)
            {
                satisfied = 0; // mid-navigation or a closed popup; look again
            }
        }

        var where = _pages.Count > 0 ? Active().Url : "(no page)";
        if (!done)
        {
            return $"Timed out waiting for the user; the browser window is still open at {where}. Try browser_handoff again or continue without signing in.";
        }

        await SaveStateAsync();
        if (returnToHidden && wasHidden)
        {
            await SwitchModeAsync(headless: true);
            return $"The user finished; login saved and the browser is hidden again. Current page: {(_pages.Count > 0 ? Active().Url : where)}";
        }

        return $"The user finished. Current page: {where}";
    });

    public Task<string> SessionAsync(string action) => WithSessionAsync(async ctx =>
    {
        switch (action)
        {
            case "save":
                await SaveStateAsync();
                return _attached ? "Attached to your own browser; its sessions are already yours." : $"Saved logins to {StatePath}.";

            case "clear":
                await ctx.ClearCookiesAsync();
                try
                {
                    File.Delete(StatePath);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return $"Cleared cookies, but could not delete the saved logins at {StatePath}: {e.Message}";
                }

                return "Cleared cookies and the saved logins. Local storage of open pages is untouched until they reload.";

            case "info":
                var cookies = await ctx.CookiesAsync();
                var domains = cookies.Select(c => c.Domain.TrimStart('.')).Distinct().OrderBy(d => d).ToList();
                return $"{cookies.Count} cookies across {domains.Count} domains" +
                    (domains.Count > 0 ? ": " + string.Join(", ", domains.Take(30)) : "") +
                    $"\nSaved state: {(File.Exists(StatePath) ? StatePath : "none")}";

            default:
                throw new McpToolException("action must be save, clear or info.");
        }
    });

    public void SetDialogPolicy(bool accept, string? promptText)
    {
        _acceptDialogs = accept;
        _promptText = promptText;
    }

    /// <summary>The current page's login-wall tell: a visible password field, or a sign-in style address.</summary>
    public static async Task<bool> LooksLikeLoginAsync(IPage page)
    {
        try
        {
            if (await page.Locator("input[type=password]:visible").CountAsync() > 0)
            {
                return true;
            }
        }
        catch (PlaywrightException)
        {
            return false;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(page.Url, @"/(log-?in|sign-?in|sso|oauth2?|auth(?:orize)?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public string ConsoleLog(IPage page, bool clear) => Drain(_console, page, clear);

    public string NetworkLog(IPage page, bool clear) => Drain(_network, page, clear);

    private static string Drain(Dictionary<IPage, List<string>> logs, IPage page, bool clear)
    {
        if (!logs.TryGetValue(page, out var log)) return "(none)";
        lock (log)
        {
            var text = log.Count == 0 ? "(none)" : string.Join('\n', log);
            if (clear) log.Clear();
            return text;
        }
    }

    public static ILocator Resolve(IPage page, string target)
    {
        target = target.Trim();
        if (target.Length > 1 && target[0] == 'e' && target[1..].All(char.IsAsciiDigit))
        {
            return page.Locator($"[data-codale-ref=\"{target}\"]");
        }

        return page.Locator(target);
    }

    /// <summary>True when target names a snapshot ref that no element carries any more (a stale or unknown ref).</summary>
    public static async Task EnsureRefExistsAsync(IPage page, string target)
    {
        target = target.Trim();
        if (target.Length > 1 && target[0] == 'e' && target[1..].All(char.IsAsciiDigit) &&
            await page.Locator($"[data-codale-ref=\"{target}\"]").CountAsync() == 0)
        {
            throw new McpToolException($"No element {target} on this page. Refs only last until the next browser_snapshot (or a navigation): take a fresh browser_snapshot and use its refs.");
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

    /// <summary>Closes the browser for the browser_close tool, waiting its turn so an in-flight tool is not torn down mid-call.</summary>
    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseBrowserAsync(saveState: true);
        _playwright?.Dispose();
        _playwright = null;
    }
}
