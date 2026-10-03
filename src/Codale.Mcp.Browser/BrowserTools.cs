using Microsoft.Playwright;

namespace Codale.Mcp.Browser;

internal static class BrowserTools
{
    private static readonly (string, string, string) Target =
        ("target", "string", "Element ref from browser_snapshot (e.g. e12), or a CSS selector. Also accepted as 'ref' or 'selector'.");

    private static readonly (string, string, string) Element =
        ("element", "string", "Optional human description of the element, for the log only.");

    private static string Tgt(McpArgs a) =>
        a.String("target") ?? a.String("ref") ?? a.String("selector")
        ?? throw new McpToolException("Missing 'target': pass an element ref from browser_snapshot (e.g. e12) or a CSS selector.");

    private static async Task<ILocator> Locate(IPage p, McpArgs a)
    {
        var target = Tgt(a);
        await BrowserSession.EnsureRefExistsAsync(p, target);
        return BrowserSession.Resolve(p, target);
    }

    /// <summary>
    /// An upload path, resolved against and confined to the project directory the app passes in
    /// CODALE_PROJECT_DIR. Without it (or for anything that normalises to outside it, e.g. via
    /// "..") the upload is refused, so a page or prompt cannot make the model attach arbitrary
    /// local files (keys, browser profiles) to a form.
    /// </summary>
    private static string ConfineToProject(string path)
    {
        var projectDir = Environment.GetEnvironmentVariable("CODALE_PROJECT_DIR");
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            throw new McpToolException("browser_upload is unavailable: the project directory (CODALE_PROJECT_DIR) is not set.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDir));
        var full = Path.GetFullPath(path, root);
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new McpToolException($"Only files inside the project directory can be uploaded: {path}");
        }

        return full;
    }

    private static string Script(McpArgs a) =>
        a.String("script") ?? a.String("function") ?? a.String("expression")
        ?? throw new McpToolException("Missing 'script': pass a JavaScript expression or arrow function, e.g. () => document.title.");

    public static IReadOnlyList<McpTool> Create(BrowserSession b)
    {
        static McpTool Tool(string name, string description, System.Text.Json.Nodes.JsonObject schema,
            Func<McpArgs, CancellationToken, Task<IReadOnlyList<McpContent>>> handler) =>
            new(name, description, schema, handler);

        return
        [
            Tool("browser_navigate", "Open a URL in the current tab and wait for it to load. Use this to view a local dev server or a deployed page.",
                Schema([("url", "string", "Absolute URL, e.g. http://localhost:3000")], "url"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var response = await p.GotoAsync(BrowserSession.RequireWebUrl(a.RequiredString("url")));
                    var note = await BrowserSession.LooksLikeLoginAsync(p) ? LoginHint : "";
                    return (IReadOnlyList<McpContent>)[McpContent.FromText($"{response?.Status} {p.Url}\nTitle: {await p.TitleAsync()}{note}")];
                })),

            Tool("browser_snapshot", "Outline the page as text with a ref for every interactive element. Prefer this over screenshots for deciding what to click; refs are only valid until the next snapshot.",
                Schema(),
                (_, _) => b.WithPageAsync(async p =>
                {
                    var outline = await p.EvaluateAsync<string>(BrowserSession.SnapshotScript);
                    var loginNote = await BrowserSession.LooksLikeLoginAsync(p) ? LoginHint : "";
                    return (IReadOnlyList<McpContent>)[McpContent.FromText($"Page: {await p.TitleAsync()} ({p.Url}){loginNote}\n{outline}")];
                })),

            Tool("browser_screenshot", "Capture a PNG of the viewport (or the whole page, or one element) to check how the UI actually looks.",
                Schema([("fullPage", "boolean", "Capture the full scrollable page."), Target]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    byte[] png = a.String("target") is { Length: > 0 } t
                        ? await BrowserSession.Resolve(p, t).ScreenshotAsync()
                        : await p.ScreenshotAsync(new PageScreenshotOptions { FullPage = a.Bool("fullPage") ?? false });
                    return (IReadOnlyList<McpContent>)[McpContent.FromImage(png)];
                })),

            Tool("browser_click", "Click an element.",
                Schema([Target, Element, ("doubleClick", "boolean", "Double-click instead."), ("button", "string", "left (default), right or middle")]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var loc = await Locate(p, a);
                    var button = (a.String("button") ?? "left").ToLowerInvariant() switch { "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => MouseButton.Left };
                    if (a.Bool("doubleClick") == true) await loc.DblClickAsync(new LocatorDblClickOptions { Button = button }); else await loc.ClickAsync(new LocatorClickOptions { Button = button });
                    return (IReadOnlyList<McpContent>)[McpContent.FromText($"Clicked. Now at {p.Url}")];
                })),

            Tool("browser_type", "Type text into an input, textarea or editable element, replacing its content.",
                Schema([Target, Element, ("text", "string", "Text to enter."), ("submit", "boolean", "Press Enter afterwards.")], "text"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var loc = await Locate(p, a);
                    await loc.FillAsync(a.String("text") ?? "");
                    if (a.Bool("submit") == true) await loc.PressAsync("Enter");
                    return (IReadOnlyList<McpContent>)[McpContent.FromText("Typed.")];
                })),

            Tool("browser_press_key", "Press a key or chord on the page, e.g. Enter, Escape, Control+A.",
                Schema([("key", "string", "Key name as in Playwright.")], "key"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    await p.Keyboard.PressAsync(a.RequiredString("key"));
                    return (IReadOnlyList<McpContent>)[McpContent.FromText("Pressed.")];
                })),

            Tool("browser_select_option", "Choose an option in a <select> by value or label.",
                Schema([Target, Element, ("value", "string", "Option value or visible label.")], "value"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var v = a.RequiredString("value");
                    await (await Locate(p, a))
                        .SelectOptionAsync(new[] { new SelectOptionValue { Value = v }, new SelectOptionValue { Label = v } });
                    return (IReadOnlyList<McpContent>)[McpContent.FromText("Selected.")];
                })),

            Tool("browser_hover", "Hover the pointer over an element.",
                Schema([Target, Element]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    await (await Locate(p, a)).HoverAsync();
                    return (IReadOnlyList<McpContent>)[McpContent.FromText("Hovered.")];
                })),

            // Deliberately unrestricted: running script in the page is this tool's purpose. It acts with the
            // page's own privileges and any logins held by the browser, never with the host's.
            Tool("browser_evaluate", "Run a JavaScript expression or arrow function in the page and return its JSON result. Use it to assert on state, e.g. () => document.title.",
                Schema([("script", "string", "Expression or function, e.g. () => document.querySelectorAll('li').length. Also accepted as 'function' or 'expression'.")]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var result = await p.EvaluateAsync<System.Text.Json.JsonElement?>(Script(a));
                    return (IReadOnlyList<McpContent>)[McpContent.FromText(result?.ToString() ?? "undefined")];
                })),

            Tool("browser_wait_for", "Wait until text appears or disappears, an element appears, the address contains something, or for a number of seconds.",
                Schema([("text", "string", "Text that must become visible."), ("textGone", "string", "Text that must disappear."), ("selector", "string", "CSS selector that must become visible."), ("urlContains", "string", "Address fragment to wait for."), ("seconds", "number", "Fixed delay instead (max 30).")]),
                (a, ct) => b.WithPageAsync(async p =>
                {
                    if (a.String("textGone") is { Length: > 0 } gone)
                    {
                        await p.GetByText(gone).First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
                        return (IReadOnlyList<McpContent>)[McpContent.FromText($"\"{gone}\" is gone.")];
                    }

                    if (a.String("selector") is { Length: > 0 } css)
                    {
                        await p.Locator(css).First.WaitForAsync();
                        return (IReadOnlyList<McpContent>)[McpContent.FromText($"Found {css}.")];
                    }

                    if (a.String("urlContains") is { Length: > 0 } frag)
                    {
                        await p.WaitForURLAsync(u => u.Contains(frag, StringComparison.OrdinalIgnoreCase));
                        return (IReadOnlyList<McpContent>)[McpContent.FromText($"At {p.Url}")];
                    }

                    if (a.String("text") is { Length: > 0 } text)
                    {
                        await p.GetByText(text).First.WaitForAsync();
                        return (IReadOnlyList<McpContent>)[McpContent.FromText($"Found \"{text}\".")];
                    }

                    var seconds = Math.Clamp(a.Int("seconds") ?? 1, 0, 30);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                    return (IReadOnlyList<McpContent>)[McpContent.FromText($"Waited {seconds}s.")];
                })),

            Tool("browser_console_messages", "Console output and uncaught page errors since the last clear (last 200).",
                Schema([("clear", "boolean", "Clear the log after reading.")]),
                (a, _) => b.WithPageAsync(p => BrowserTools.TextResult(b.ConsoleLog(p, a.Bool("clear") ?? false)))),

            Tool("browser_network_requests", "Responses and failed requests since the last clear (last 200).",
                Schema([("clear", "boolean", "Clear the log after reading.")]),
                (a, _) => b.WithPageAsync(p => BrowserTools.TextResult(b.NetworkLog(p, a.Bool("clear") ?? false)))),

            Tool("browser_resize", "Resize the viewport to a custom size. For device profiles (mobile, tablet, laptop, desktop, 4k) use browser_set_viewport.",
                Schema([("width", "number", "Pixels."), ("height", "number", "Pixels.")], "width", "height"),
                async (a, _) => await Text(b.SetViewportAsync(null, (a.RequiredInt("width"), a.RequiredInt("height"))))),

            Tool("browser_navigate_back", "Go back in the current tab's history.",
                Schema(),
                (_, _) => b.WithPageAsync(async p =>
                {
                    await p.GoBackAsync();
                    return (IReadOnlyList<McpContent>)[McpContent.FromText($"At {p.Url}")];
                })),

            Tool("browser_tabs", "List, open, select or close tabs.",
                Schema([("action", "string", "list | new | select | close"), ("index", "number", "Tab index for select/close."), ("url", "string", "URL for new.")], "action"),
                async (a, _) => (IReadOnlyList<McpContent>)[McpContent.FromText(a.RequiredString("action") switch
                {
                    "list" => await b.ListTabsAsync(),
                    "new" => await b.NewTabAsync(a.String("url")),
                    "select" => await b.SelectTabAsync(a.RequiredInt("index")),
                    "close" => await b.CloseTabAsync(a.Int("index")),
                    var other => throw new McpToolException($"Unknown action '{other}'."),
                })]),

            Tool("browser_set_viewport",
                "Switch the browser to a device profile to test responsive layouts: " + ViewportPreset.Names + ". Mobile and tablet presets also emulate touch, pixel ratio and the device user agent, and reload open tabs. " +
                "Or give a custom width and height. device=reset returns to the default.",
                Schema([("device", "string", "Preset name (mobile, mobile-small, tablet, laptop, desktop, 4k) or reset."), ("width", "number", "Custom width in pixels."), ("height", "number", "Custom height in pixels.")]),
                async (a, _) =>
                {
                    if (a.String("device") is { Length: > 0 } name)
                    {
                        if (name.Trim().ToLowerInvariant() is "reset" or "default")
                        {
                            return await Text(b.SetViewportAsync(null, null));
                        }

                        var preset = ViewportPreset.Find(name)
                            ?? throw new McpToolException($"Unknown device '{name}'. Choose one of: {ViewportPreset.Names}.");
                        return await Text(b.SetViewportAsync(preset, null));
                    }

                    return await Text(b.SetViewportAsync(null, (a.RequiredInt("width"), a.RequiredInt("height"))));
                }),

            Tool("browser_responsive_check",
                "Take a screenshot of the current page at several device sizes in one call (default mobile, tablet, laptop, desktop), then restore the previous size. Use it to verify a layout across breakpoints.",
                Schema([("devices", "string", "Comma-separated presets: " + ViewportPreset.Names + ". Default: mobile,tablet,laptop,desktop."), ("fullPage", "boolean", "Capture each full scrollable page.")]),
                (a, _) =>
                {
                    var names = (a.String("devices") ?? "mobile,tablet,laptop,desktop")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var presets = names.Select(n => ViewportPreset.Find(n)
                        ?? throw new McpToolException($"Unknown device '{n}'. Choose one of: {ViewportPreset.Names}.")).ToList();
                    return b.ResponsiveShotsAsync(presets, a.Bool("fullPage") ?? false);
                }),

            Tool("browser_emulate", "Emulate page conditions: dark or light colour scheme, reduced motion, or offline mode.",
                Schema([("colorScheme", "string", "dark | light | default"), ("reducedMotion", "string", "reduce | default"), ("offline", "boolean", "Cut the network.")]),
                (a, _) => Text(b.EmulateAsync(a.String("colorScheme"), a.String("reducedMotion"), a.Bool("offline")))),

            Tool("browser_handoff",
                "Use when a page needs a sign-in, captcha, 2FA or any step only the user can do. Do NOT give up or guess credentials: this opens a visible browser window for the user, waits until they finish, saves the login and returns to the hidden browser signed in. " +
                "It waits until the password field is gone or the address changes, or until untilUrl / untilText is met.",
                Schema([("url", "string", "Page to open for the user; defaults to the current tab."), ("untilUrl", "string", "Finished when the address contains this."), ("untilText", "string", "Finished when this text is visible."), ("timeoutSeconds", "number", "How long to wait (default 300, max 900)."), ("returnToHidden", "boolean", "Hide the window again afterwards (default true).")]),
                (a, ct) => Text(b.HandoffAsync(a.String("url"), a.String("untilUrl"), a.String("untilText"), a.Int("timeoutSeconds") ?? 300, a.Bool("returnToHidden") ?? true, ct))),

            Tool("browser_mode", "Show or hide the automation browser. It is hidden (headless) by default; logins and tabs are kept when switching.",
                Schema([("mode", "string", "visible | hidden")], "mode"),
                (a, _) => Text(b.SetModeAsync(a.RequiredKeyword("mode") switch
                {
                    "hidden" or "headless" => true,
                    "visible" or "shown" or "headed" => false,
                    var other => throw new McpToolException($"Unknown mode '{other}': use visible or hidden."),
                }))),

            Tool("browser_session", "Manage saved logins: info (which domains have cookies), save, or clear.",
                Schema([("action", "string", "info | save | clear")], "action"),
                (a, _) => Text(b.SessionAsync(a.RequiredKeyword("action")))),

            Tool("browser_history", "Go back, forward or reload the current tab.",
                Schema([("action", "string", "back | forward | reload")], "action"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    switch (a.RequiredKeyword("action"))
                    {
                        case "back": await p.GoBackAsync(); break;
                        case "forward": await p.GoForwardAsync(); break;
                        case "reload": await p.ReloadAsync(); break;
                        default: throw new McpToolException("action must be back, forward or reload.");
                    }

                    return (IReadOnlyList<McpContent>)[McpContent.FromText($"At {p.Url}")];
                })),

            Tool("browser_scroll", "Scroll an element into view, or scroll the page to the top or bottom or by some pixels.",
                Schema([Target, Element, ("to", "string", "top | bottom | up | down"), ("pixels", "number", "Amount for up/down (default one screen).")]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    if (HasTarget(a))
                    {
                        await (await Locate(p, a)).ScrollIntoViewIfNeededAsync();
                        return (IReadOnlyList<McpContent>)[McpContent.FromText("Scrolled into view.")];
                    }

                    var to = (a.String("to") ?? "down").Trim().ToLowerInvariant();
                    var pixels = a.Int("pixels") ?? 0;
                    var script = to switch
                    {
                        "top" => "() => { window.scrollTo(0, 0); return window.scrollY; }",
                        "bottom" => "() => { window.scrollTo(0, document.documentElement.scrollHeight); return window.scrollY; }",
                        "up" => $"() => {{ window.scrollBy(0, -({pixels} || window.innerHeight * 0.9)); return window.scrollY; }}",
                        "down" => $"() => {{ window.scrollBy(0, {pixels} || window.innerHeight * 0.9); return window.scrollY; }}",
                        _ => throw new McpToolException("to must be top, bottom, up or down."),
                    };
                    var y = await p.EvaluateAsync<double>(script);
                    return (IReadOnlyList<McpContent>)[McpContent.FromText($"Scrolled; page offset is now {y:0}px.")];
                })),

            Tool("browser_drag", "Drag one element onto another.",
                Schema([("from", "string", "Ref or selector of the element to drag."), ("to", "string", "Ref or selector of the drop target.")], "from", "to"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    await BrowserSession.EnsureRefExistsAsync(p, a.RequiredString("from"));
                    await BrowserSession.EnsureRefExistsAsync(p, a.RequiredString("to"));
                    await BrowserSession.Resolve(p, a.RequiredString("from")).DragToAsync(BrowserSession.Resolve(p, a.RequiredString("to")));
                    return (IReadOnlyList<McpContent>)[McpContent.FromText("Dragged.")];
                })),

            Tool("browser_upload", "Attach one or more local files to a file input. Only files inside the project folder can be attached.",
                Schema([Target, Element, ("paths", "string", "File path, or several separated by | .")], "paths"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var files = (a.Array("paths")?.Select(e => e.GetString() ?? "").ToArray()
                            ?? a.RequiredString("paths").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        .Select(ConfineToProject).ToArray();
                    foreach (var f in files.Where(f => !File.Exists(f)))
                    {
                        throw new McpToolException($"No such file: {f}");
                    }

                    await (await Locate(p, a)).SetInputFilesAsync(files);
                    return (IReadOnlyList<McpContent>)[McpContent.FromText($"Attached {files.Length} file(s).")];
                })),

            Tool("browser_dialog", "Set how alert/confirm/prompt dialogs are answered from now on (default: accept). Dialogs that appear are noted in browser_console_messages.",
                Schema([("action", "string", "accept | dismiss"), ("promptText", "string", "Text to enter for prompt dialogs.")], "action"),
                (a, _) =>
                {
                    var accept = a.RequiredKeyword("action") switch
                    {
                        "accept" => true,
                        "dismiss" => false,
                        var other => throw new McpToolException($"Unknown action '{other}': use accept or dismiss."),
                    };
                    b.SetDialogPolicy(accept, a.String("promptText"));
                    return TextResult($"Dialogs will be {(accept ? "accepted" : "dismissed")}.");
                }),

            Tool("browser_get_text", "Read the visible text of an element, or of the whole page.",
                Schema([Target, Element]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var text = HasTarget(a) ? await (await Locate(p, a)).InnerTextAsync() : await p.InnerTextAsync("body");
                    return (IReadOnlyList<McpContent>)[McpContent.FromText(text.Length > 20000 ? text[..20000] + "\n...(truncated)" : text)];
                })),

            Tool("browser_fill_form", "Fill several fields at once. Each field is a target and a value; use it for login and sign-up forms.",
                new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["fields"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Fields to fill, in order.",
                            ["items"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new System.Text.Json.Nodes.JsonObject
                                {
                                    ["target"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string", ["description"] = "Ref or selector." },
                                    ["value"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string", ["description"] = "Text to enter, or true/false for a checkbox." },
                                },
                                ["required"] = new System.Text.Json.Nodes.JsonArray("target", "value"),
                            },
                        },
                        ["submit"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean", ["description"] = "Press Enter in the last field." },
                    },
                    ["required"] = new System.Text.Json.Nodes.JsonArray("fields"),
                },
                (a, _) => b.WithPageAsync(async p =>
                {
                    var fields = a.RequiredArray("fields");
                    ILocator? last = null;
                    foreach (var f in fields)
                    {
                        var target = (f.TryGetProperty("target", out var t) ? t.GetString() : null)
                            ?? (f.TryGetProperty("ref", out var r) ? r.GetString() : null)
                            ?? throw new McpToolException("Each field needs a target.");
                        var value = f.TryGetProperty("value", out var v) ? (v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
                        await BrowserSession.EnsureRefExistsAsync(p, target);
                        last = BrowserSession.Resolve(p, target);
                        if (value is "true" or "false" && await last.EvaluateAsync<bool>("e => e.type === 'checkbox' || e.type === 'radio'"))
                        {
                            await last.SetCheckedAsync(value == "true");
                        }
                        else
                        {
                            await last.FillAsync(value);
                        }
                    }

                    if (a.Bool("submit") == true && last is not null) await last.PressAsync("Enter");
                    return (IReadOnlyList<McpContent>)[McpContent.FromText($"Filled {fields.Count} field(s).")];
                })),

            Tool("browser_close", "Close the browser. The next browser tool call starts a fresh one.",
                Schema(),
                async (_, _) =>
                {
                    await b.CloseAsync();
                    return (IReadOnlyList<McpContent>)[McpContent.FromText("Closed.")];
                }),
        ];
    }

    private static async Task<IReadOnlyList<McpContent>> Text(Task<string> text) =>
        [McpContent.FromText(await text)];

    private const string LoginHint =
        "\nNOTE: this looks like a sign-in page. Do not stop or guess credentials: call browser_handoff so the user can sign in, then carry on.";

    private static bool HasTarget(McpArgs a) =>
        (a.String("target") ?? a.String("ref") ?? a.String("selector")) is { Length: > 0 };

    private static Task<IReadOnlyList<McpContent>> TextResult(string text) =>
        Task.FromResult<IReadOnlyList<McpContent>>([McpContent.FromText(text)]);

    private static System.Text.Json.Nodes.JsonObject Schema(
        IEnumerable<(string, string, string)>? props = null, params string[] required) =>
        McpTool.Schema(props, required);
}
