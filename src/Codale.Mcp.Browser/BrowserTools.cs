using System.Text.Json;

namespace Codale.Mcp.Browser;

internal static class BrowserTools
{
    private static readonly (string, string, string) Target =
        ("target", "string", "The element: a ref from the latest browser_snapshot (e.g. e12) or a CSS selector. Also accepted as 'ref' or 'selector'.");

    private static readonly (string, string, string) Element =
        ("element", "string", "Optional plain-language description of the element, e.g. \"Save button\"; shown to the user, not used to find it.");

    private static string Tgt(McpArgs a) =>
        a.String("target") ?? a.String("ref") ?? a.String("selector")
        ?? throw new McpToolException("Missing 'target': pass an element ref from browser_snapshot (e.g. e12) or a CSS selector.");

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
        return Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? throw new McpToolException($"Only files inside the project directory can be uploaded: {path}")
            : full;
    }

    private static string Script(McpArgs a) =>
        a.String("script") ?? a.String("function") ?? a.String("expression")
        ?? throw new McpToolException("Missing 'script': pass a JavaScript expression or arrow function, e.g. () => document.title.");

    private static string Js(string value) => JsonSerializer.Serialize(value);

    private const string VisibleJs = "(e => { const r = e.getBoundingClientRect(), s = getComputedStyle(e); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; })";

    public static IReadOnlyList<McpTool> Create(BrowserSession b)
    {
        static McpTool Tool(string name, string description, System.Text.Json.Nodes.JsonObject schema,
            Func<McpArgs, CancellationToken, Task<IReadOnlyList<McpContent>>> handler) =>
            new(name, description, schema, handler);

        return
        [
            Tool("browser_navigate", "Open a URL in your own tab of Codale Browser (a window the user can watch; other agents have their own tabs) and wait for it to load. The first step for checking a local dev server or a deployed page. Returns the HTTP status, final URL and page title; follow with browser_snapshot to read the page.",
                Schema([("url", "string", "Absolute http(s) URL, e.g. http://localhost:3000/settings")], "url"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var status = await p.NavigateAsync(BrowserSession.RequireWebUrl(a.RequiredString("url")));
                    var note = await BrowserSession.LooksLikeLoginAsync(p) ? LoginHint : "";
                    return Result($"{status} {await p.UrlAsync()}".TrimStart() + $"\nTitle: {await p.TitleAsync()}{note}");
                })),

            Tool("browser_snapshot", "Read the current page as a text outline: headings, text, and a ref (e12) for every link, button, input and other interactive element. Use it to learn what is on the page and what to click or type into; it is cheaper and more precise than a screenshot. Refs are valid only until the next snapshot.",
                Schema(),
                (_, _) => b.WithPageAsync(async p =>
                {
                    var outline = await p.StringAsync($"({BrowserSession.SnapshotScript})()");
                    var loginNote = await BrowserSession.LooksLikeLoginAsync(p) ? LoginHint : "";
                    return Result($"Page: {await p.TitleAsync()} ({await p.UrlAsync()}){loginNote}\n{outline}");
                })),

            Tool("browser_screenshot", "Capture a PNG of what the page looks like: the visible viewport by default, the whole scrollable page with fullPage, or a single element with target. Use it to check layout, styling and visual state; use browser_snapshot instead to read text or find elements.",
                Schema([("fullPage", "boolean", "Capture the full scrollable page instead of just the viewport."), Target]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var png = await p.ScreenshotAsync(a.Bool("fullPage") ?? false, HasTarget(a) ? Tgt(a) : null);
                    return (IReadOnlyList<McpContent>)[McpContent.FromImage(png)];
                })),

            Tool("browser_click", "Click an element on the page, found by its ref from browser_snapshot or a CSS selector. Waits for it to be visible and enabled. Returns the URL afterwards, so a navigation shows up.",
                Schema([Target, Element, ("doubleClick", "boolean", "Double-click instead of a single click."), ("button", "string", "Mouse button: left (default), right or middle.")]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var button = (a.String("button") ?? "left").ToLowerInvariant() switch { "right" => "right", "middle" => "middle", _ => "left" };
                    await p.ClickAsync(Tgt(a), button, a.Bool("doubleClick") == true);
                    await Task.Delay(200); // let a navigation the click started show in the URL
                    return Result($"Clicked. Now at {await p.UrlAsync()}");
                })),

            Tool("browser_type", "Set the text of one input, textarea or contenteditable element, replacing whatever it held. For several fields at once use browser_fill_form; to press a key without typing use browser_press_key.",
                Schema([Target, Element, ("text", "string", "The text the field should contain afterwards."), ("submit", "boolean", "Press Enter in the field afterwards, e.g. to submit a search.")], "text"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    await p.FillAsync(Tgt(a), a.String("text") ?? "");
                    if (a.Bool("submit") == true) await p.PressAsync("Enter");
                    return Result("Typed.");
                })),

            Tool("browser_press_key", "Press one key or chord in the page, sent to whatever element has focus: Enter, Escape, Tab, ArrowDown, Control+A, Shift+Enter. Not for entering text; use browser_type for that.",
                Schema([("key", "string", "Key name or chord, e.g. Enter, Escape, Tab, ArrowDown, Backspace, F5, Shift+Tab, Control+A.")], "key"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    await p.PressAsync(a.RequiredString("key"));
                    return Result("Pressed.");
                })),

            Tool("browser_select_option", "Choose an option in a native <select> dropdown by its value or its visible label. For custom dropdowns built from divs, click them with browser_click instead.",
                Schema([Target, Element, ("value", "string", "The option's value attribute or its visible label text.")], "value"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    await p.SelectOptionAsync(Tgt(a), a.RequiredString("value"));
                    return Result("Selected.");
                })),

            Tool("browser_hover", "Move the pointer over an element without clicking, to open hover menus, tooltips and hover styles before a snapshot or screenshot.",
                Schema([Target, Element]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    await p.HoverAsync(Tgt(a));
                    return Result("Hovered.");
                })),

            // Deliberately unrestricted: running script in the page is this tool's purpose. It acts with the
            // page's own privileges and any logins held by the browser, never with the host's.
            Tool("browser_evaluate", "Run JavaScript inside the page and return its result as JSON. Use it to read state the outline does not show (a store, localStorage, computed styles, element counts) or to assert a condition. Runs with the page's privileges; the user approves each call.",
                Schema([("script", "string", "A JavaScript expression or arrow function, e.g. () => document.querySelectorAll('li').length or () => localStorage.getItem('token') !== null. Also accepted as 'function' or 'expression'.")]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    // A function is called; anything else is evaluated as an expression.
                    var result = await p.EvaluateAsync($"(async () => {{ const __codaleValue = ({Script(a)}); return typeof __codaleValue === 'function' ? await __codaleValue() : await __codaleValue; }})()");
                    return Result(result.ValueKind switch
                    {
                        JsonValueKind.Undefined => "undefined",
                        JsonValueKind.String => result.GetString() ?? "",
                        _ => result.GetRawText(),
                    });
                })),

            Tool("browser_wait_for", "Pause until the page reaches a state: some text appears or disappears, an element appears, the URL contains a fragment, or a fixed number of seconds pass. Use it after an action that loads or re-renders, instead of taking repeated snapshots. Give exactly one condition.",
                Schema([("text", "string", "Wait until this text is visible on the page."), ("textGone", "string", "Wait until this text is no longer visible, e.g. \"Loading...\"."), ("selector", "string", "Wait until an element matching this CSS selector is visible."), ("urlContains", "string", "Wait until the address contains this fragment, e.g. /dashboard."), ("seconds", "number", "Just wait this many seconds (max 30) when there is nothing to watch for.")]),
                (a, ct) => b.WithPageAsync(async p =>
                {
                    async Task Until(string predicate, string what)
                    {
                        if (!await p.PollAsync(predicate, ct: ct))
                        {
                            throw new McpToolException($"Timed out after {CdpPage.Timeout.TotalSeconds:0}s waiting for {what}.");
                        }
                    }

                    if (a.String("textGone") is { Length: > 0 } gone)
                    {
                        await Until($"!document.body || !document.body.innerText.includes({Js(gone)})", $"\"{gone}\" to disappear");
                        return Result($"\"{gone}\" is gone.");
                    }

                    if (a.String("selector") is { Length: > 0 } css)
                    {
                        await Until($"[...document.querySelectorAll({Js(css)})].some({VisibleJs})", css);
                        return Result($"Found {css}.");
                    }

                    if (a.String("urlContains") is { Length: > 0 } frag)
                    {
                        await Until($"location.href.toLowerCase().includes({Js(frag.ToLowerInvariant())})", $"the address to contain {frag}");
                        return Result($"At {await p.UrlAsync()}");
                    }

                    if (a.String("text") is { Length: > 0 } text)
                    {
                        await Until($"!!document.body && document.body.innerText.includes({Js(text)})", $"\"{text}\"");
                        return Result($"Found \"{text}\".");
                    }

                    var seconds = Math.Clamp(a.Int("seconds") ?? 1, 0, 30);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                    return Result($"Waited {seconds}s.");
                })),

            Tool("browser_console_messages", "Read the page's console output (log, warn, error) and uncaught JavaScript errors collected since the agent attached or last cleared, newest 200. Check it after exercising a flow, before reporting the UI as working; an empty result means no errors.",
                Schema([("clear", "boolean", "Empty the log after reading, so the next read shows only new messages.")]),
                (a, _) => b.WithPageAsync(_ => Task.FromResult(Result(b.ConsoleLog(a.Bool("clear") ?? false))))),

            Tool("browser_network_requests", "Read the HTTP requests the page made since the agent attached or last cleared, newest 200: method, URL, status, and failures. Use it to find a failing API call, a 404 asset or a request that never went out.",
                Schema([("clear", "boolean", "Empty the log after reading, so the next read shows only new requests.")]),
                (a, _) => b.WithPageAsync(_ => Task.FromResult(Result(b.NetworkLog(a.Bool("clear") ?? false))))),

            Tool("browser_resize", "Set the viewport to an exact width and height in pixels, e.g. to test one specific breakpoint. For named device profiles (mobile, tablet, laptop, desktop, 4k) with touch and pixel-ratio emulation use browser_set_viewport.",
                Schema([("width", "number", "Viewport width in pixels."), ("height", "number", "Viewport height in pixels.")], "width", "height"),
                async (a, _) => await Text(b.SetViewportAsync(null, (a.RequiredInt("width"), a.RequiredInt("height"))))),

            Tool("browser_navigate_back", "Go back one page in the history, like the browser's Back button. browser_history also offers forward and reload.",
                Schema(),
                (_, _) => b.WithPageAsync(async p =>
                {
                    await p.GoAsync(-1);
                    return Result($"At {await p.UrlAsync()}");
                })),

            Tool("browser_set_viewport",
                "Switch the browser to a device profile to test a responsive layout at one size: " + ViewportPreset.Names + ". Mobile and tablet presets also emulate touch, pixel ratio and the device user agent, and reload the page. " +
                "Give either device or a custom width and height; device=reset returns to the window's own size. To compare several sizes in one call use browser_responsive_check.",
                Schema([("device", "string", "Preset name (" + ViewportPreset.Names + ") or reset."), ("width", "number", "Custom viewport width in pixels, used when device is omitted."), ("height", "number", "Custom viewport height in pixels, used when device is omitted.")]),
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
                "Screenshot the current page at several device sizes in one call (default mobile, tablet, laptop, desktop) and restore the previous size afterwards. The quickest way to verify a layout across breakpoints after a CSS change.",
                Schema([("devices", "string", "Comma-separated presets to capture: " + ViewportPreset.Names + ". Default: mobile,tablet,laptop,desktop."), ("fullPage", "boolean", "Capture each full scrollable page instead of just the viewport.")]),
                (a, _) =>
                {
                    var names = (a.String("devices") ?? "mobile,tablet,laptop,desktop")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var presets = names.Select(n => ViewportPreset.Find(n)
                        ?? throw new McpToolException($"Unknown device '{n}'. Choose one of: {ViewportPreset.Names}.")).ToList();
                    return b.ResponsiveShotsAsync(presets, a.Bool("fullPage") ?? false);
                }),

            Tool("browser_emulate", "Change what the page believes about its environment: prefers-color-scheme (dark or light), prefers-reduced-motion, or an offline network. Use it to check a dark theme, a motion-free variant or offline handling without changing the app.",
                Schema([("colorScheme", "string", "dark | light | default"), ("reducedMotion", "string", "reduce | default"), ("offline", "boolean", "true cuts the network for the page; false restores it.")]),
                (a, _) => Text(b.EmulateAsync(a.String("colorScheme"), a.String("reducedMotion"), a.Bool("offline")))),

            Tool("browser_handoff",
                "Hand Codale Browser to the user for a step only they can do: a sign-in, captcha, 2FA code, payment or consent screen. Do NOT give up, ask for credentials or guess them: this brings the window forward on your tab, waits until the user finishes there, and carries on signed in (the window keeps its logins). " +
                "By default it considers the step done when the password field disappears or the address changes; untilUrl / untilText give a precise end condition.",
                Schema([("url", "string", "Page to open for the user; defaults to the current page."), ("untilUrl", "string", "Done when the address contains this fragment, e.g. /dashboard."), ("untilText", "string", "Done when this text is visible, e.g. \"Sign out\"."), ("timeoutSeconds", "number", "How long to wait for the user (default 300, max 900).")]),
                (a, ct) => Text(b.HandoffAsync(a.String("url"), a.String("untilUrl"), a.String("untilText"), a.Int("timeoutSeconds") ?? 300, ct))),

            Tool("browser_session", "Manage the logins Codale Browser keeps between runs: info lists the domains with cookies, clear signs out of everything (save is accepted but unnecessary: logins are kept as they happen). Use info to check whether a site is already signed in before navigating.",
                Schema([("action", "string", "info | save | clear")], "action"),
                (a, _) => Text(b.SessionAsync(a.RequiredKeyword("action")))),

            Tool("browser_history", "Use the Back or Forward button, or reload the page (e.g. after a dev-server rebuild). Returns the URL afterwards.",
                Schema([("action", "string", "back | forward | reload")], "action"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    switch (a.RequiredKeyword("action"))
                    {
                        case "back": await p.GoAsync(-1); break;
                        case "forward": await p.GoAsync(1); break;
                        case "reload": await p.ReloadAsync(); break;
                        default: throw new McpToolException("action must be back, forward or reload.");
                    }

                    return Result($"At {await p.UrlAsync()}");
                })),

            Tool("browser_scroll", "Scroll the page: give target to bring one element into view, or to for the page itself (top, bottom, or up/down by one screen or a number of pixels). Needed before screenshotting content below the fold or triggering infinite-scroll loading.",
                Schema([Target, Element, ("to", "string", "top | bottom | up | down; used when no target is given."), ("pixels", "number", "Distance for up/down in pixels; default is about one screen.")]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    if (HasTarget(a))
                    {
                        await p.CentreAsync(Tgt(a), enabled: false);
                        return Result("Scrolled into view.");
                    }

                    var to = (a.String("to") ?? "down").Trim().ToLowerInvariant();
                    var pixels = a.Int("pixels") ?? 0;
                    var script = to switch
                    {
                        "top" => "window.scrollTo(0, 0)",
                        "bottom" => "window.scrollTo(0, document.documentElement.scrollHeight)",
                        "up" => $"window.scrollBy(0, -({pixels} || window.innerHeight * 0.9))",
                        "down" => $"window.scrollBy(0, {pixels} || window.innerHeight * 0.9)",
                        _ => throw new McpToolException("to must be top, bottom, up or down."),
                    };
                    var y = (await p.EvaluateAsync($"({script}, window.scrollY)")).GetDouble();
                    return Result($"Scrolled; page offset is now {y:0}px.");
                })),

            Tool("browser_drag", "Drag one element and drop it on another, for sortable lists, kanban boards and drop zones. Both are refs from browser_snapshot or CSS selectors.",
                Schema([("from", "string", "Ref or selector of the element to pick up."), ("to", "string", "Ref or selector of the element to drop it on.")], "from", "to"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    await p.DragAsync(a.RequiredString("from"), a.RequiredString("to"));
                    return Result("Dragged.");
                })),

            Tool("browser_upload", "Attach one or more files from the project folder to an <input type=file> on the page, as if chosen in the file dialog. Files outside the project folder are refused. The user approves each call.",
                Schema([Target, Element, ("paths", "string", "Path of the file to attach, relative to the project root or absolute within it; several paths separated by | .")], "paths"),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var files = (a.Array("paths")?.Select(e => e.GetString() ?? "").ToArray()
                            ?? a.RequiredString("paths").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        .Select(ConfineToProject).ToArray();
                    foreach (var f in files.Where(f => !File.Exists(f)))
                    {
                        throw new McpToolException($"No such file: {f}");
                    }

                    await p.SetInputFilesAsync(Tgt(a), files);
                    return Result($"Attached {files.Length} file(s).");
                })),

            Tool("browser_dialog", "Decide in advance how the page's alert(), confirm() and prompt() dialogs are answered from now on; default is accept. Set it before an action that will show a confirm, e.g. dismiss to test the cancel path. Each dialog that appears is recorded in browser_console_messages.",
                Schema([("action", "string", "accept | dismiss"), ("promptText", "string", "Text to answer prompt() dialogs with, when accepting.")], "action"),
                (a, _) =>
                {
                    var accept = a.RequiredKeyword("action") switch
                    {
                        "accept" => true,
                        "dismiss" => false,
                        var other => throw new McpToolException($"Unknown action '{other}': use accept or dismiss."),
                    };
                    b.SetDialogPolicy(accept, a.String("promptText"));
                    return Task.FromResult(Result($"Dialogs will be {(accept ? "accepted" : "dismissed")}."));
                }),

            Tool("browser_get_text", "Read the rendered text of one element, or of the whole page when no target is given, without the outline and refs that browser_snapshot adds. Use it to check a message, a table's contents or a generated value exactly.",
                Schema([Target, Element]),
                (a, _) => b.WithPageAsync(async p =>
                {
                    var text = HasTarget(a)
                        ? (await p.OnElementAsync(Tgt(a), "return el.innerText ?? el.textContent ?? '';")).GetString() ?? ""
                        : await p.StringAsync("document.body ? document.body.innerText : ''");
                    return Result(text.Length > 20000 ? text[..20000] + "\n...(truncated)" : text);
                })),

            Tool("browser_fill_form", "Fill several form fields in one call, in order, then optionally press Enter in the last one. Each field is a target (ref or selector) and a value; true/false toggles a checkbox or radio. Use it for login, sign-up and settings forms instead of repeated browser_type calls.",
                new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["fields"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "The fields to fill, in the order to fill them.",
                            ["items"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new System.Text.Json.Nodes.JsonObject
                                {
                                    ["target"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string", ["description"] = "Ref from browser_snapshot (e12) or a CSS selector." },
                                    ["value"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string", ["description"] = "Text to enter, or true/false to check or uncheck a checkbox or radio." },
                                },
                                ["required"] = new System.Text.Json.Nodes.JsonArray("target", "value"),
                            },
                        },
                        ["submit"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean", ["description"] = "Press Enter in the last field afterwards, to submit the form." },
                    },
                    ["required"] = new System.Text.Json.Nodes.JsonArray("fields"),
                },
                (a, _) => b.WithPageAsync(async p =>
                {
                    var fields = a.RequiredArray("fields");
                    string? last = null;
                    foreach (var f in fields)
                    {
                        var target = (f.TryGetProperty("target", out var t) ? t.GetString() : null)
                            ?? (f.TryGetProperty("ref", out var r) ? r.GetString() : null)
                            ?? throw new McpToolException("Each field needs a target.");
                        var value = f.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
                        last = target;
                        if (value is "true" or "false" &&
                            await p.OnElementAsync(target, "return el.type === 'checkbox' || el.type === 'radio';") is { ValueKind: JsonValueKind.True })
                        {
                            await p.SetCheckedAsync(target, value == "true");
                        }
                        else
                        {
                            await p.FillAsync(target, value);
                        }
                    }

                    if (a.Bool("submit") == true && last is not null) await p.PressAsync("Enter");
                    return Result($"Filled {fields.Count} field(s).");
                })),

            Tool("browser_close", "Let go of Codale Browser and drop any device or media emulation. The window stays open with its logins, and the next browser tool attaches again. Usually unnecessary; call it when the user asks or the connection is wedged.",
                Schema(),
                async (_, _) =>
                {
                    await b.CloseAsync();
                    return Result("Detached from Codale Browser.");
                }),
        ];
    }

    private static IReadOnlyList<McpContent> Result(string text) => [McpContent.FromText(text)];

    private static async Task<IReadOnlyList<McpContent>> Text(Task<string> text) =>
        [McpContent.FromText(await text)];

    private const string LoginHint =
        "\nNOTE: this looks like a sign-in page. Do not stop or guess credentials: call browser_handoff so the user can sign in in Codale Browser, then carry on.";

    private static bool HasTarget(McpArgs a) =>
        (a.String("target") ?? a.String("ref") ?? a.String("selector")) is { Length: > 0 };

    private static System.Text.Json.Nodes.JsonObject Schema(
        IEnumerable<(string, string, string)>? props = null, params string[] required) =>
        McpTool.Schema(props, required);
}
