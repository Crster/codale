using System.Text.Json;

namespace Codale.Mcp.Browser;

/// <summary>
/// The page in Codale Browser, driven over the DevTools Protocol: navigation, script, element
/// actions with real mouse and keyboard input, and screenshots. Elements are named by a
/// snapshot ref (e12, stamped by <see cref="BrowserSession.SnapshotScript"/>) or a CSS selector.
/// </summary>
public sealed class CdpPage(CdpConnection cdp)
{
    /// <summary>How long an action waits for its element, a navigation or a condition.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string Find = "(t => /^e\\d+$/.test(t) ? document.querySelector('[data-codale-ref=\"' + t + '\"]') : document.querySelector(t))";

    public CdpConnection Cdp { get; } = cdp;

    private static string Js(string value) => JsonSerializer.Serialize(value);

    public static bool IsRef(string target)
    {
        target = target.Trim();
        return target.Length > 1 && target[0] == 'e' && target[1..].All(char.IsAsciiDigit);
    }

    // ---- Script ------------------------------------------------------------------------

    /// <summary>Evaluates an expression in the page and returns its JSON value (undefined as default).</summary>
    public async Task<JsonElement> EvaluateAsync(string expression)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await Cdp.SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true, userGesture = true });
            if (result.TryGetProperty("exceptionDetails", out var details))
            {
                var message = details.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d)
                    ? d.GetString() : details.TryGetProperty("text", out var t) ? t.GetString() : "Script failed.";

                // A navigation swapped the document under the call; the new one gets a second try.
                if (attempt == 0 && message?.Contains("context", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await Task.Delay(300);
                    continue;
                }

                throw new McpToolException(message ?? "Script failed.");
            }

            var value = result.GetProperty("result");
            return value.TryGetProperty("value", out var v) ? v : default;
        }
    }

    public async Task<string> StringAsync(string expression) =>
        await EvaluateAsync(expression) is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";

    public Task<string> UrlAsync() => StringAsync("location.href");

    public Task<string> TitleAsync() => StringAsync("document.title");

    /// <summary>Polls a boolean expression until it holds; false after the timeout.</summary>
    public async Task<bool> PollAsync(string predicate, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (true)
        {
            try
            {
                if (await EvaluateAsync($"!!({predicate})") is { ValueKind: JsonValueKind.True }) return true;
            }
            catch (McpToolException) when (DateTime.UtcNow < deadline)
            {
                // Mid-navigation; look again.
            }

            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(150, ct);
        }
    }

    // ---- Navigation --------------------------------------------------------------------

    /// <summary>Opens a URL, waits for its load event, and returns the document's HTTP status if known.</summary>
    public async Task<int?> NavigateAsync(string url)
    {
        var statuses = new Dictionary<string, int>();
        using var responses = Cdp.On("Network.responseReceived", e =>
        {
            if (e.TryGetProperty("type", out var type) && type.GetString() == "Document" && e.TryGetProperty("loaderId", out var loader))
            {
                lock (statuses) statuses[loader.GetString() ?? ""] = e.GetProperty("response").GetProperty("status").GetInt32();
            }
        });
        var loaded = Cdp.WaitForEventAsync("Page.loadEventFired", null, Timeout);
        var result = await Cdp.SendAsync("Page.navigate", new { url });
        if (result.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 } text)
        {
            throw new McpToolException($"Could not open {url}: {text}");
        }

        // A same-document navigation (only the #hash changed) has no loader and fires no load.
        if (!result.TryGetProperty("loaderId", out var loaderId)) return null;
        await loaded;
        lock (statuses) return statuses.TryGetValue(loaderId.GetString() ?? "", out var status) ? status : null;
    }

    /// <summary>Back (-1) or forward (+1) in the history; false when there is nowhere to go.</summary>
    public async Task<bool> GoAsync(int delta)
    {
        var history = await Cdp.SendAsync("Page.getNavigationHistory");
        var index = history.GetProperty("currentIndex").GetInt32() + delta;
        var entries = history.GetProperty("entries");
        if (index < 0 || index >= entries.GetArrayLength()) return false;
        var settled = SettledAsync();
        await Cdp.SendAsync("Page.navigateToHistoryEntry", new { entryId = entries[index].GetProperty("id").GetInt32() });
        await settled;
        return true;
    }

    public async Task ReloadAsync()
    {
        var loaded = Cdp.WaitForEventAsync("Page.loadEventFired", null, Timeout);
        await Cdp.SendAsync("Page.reload");
        await loaded;
    }

    /// <summary>A history step finishes with a load, or - from the back/forward cache or within the document - without one.</summary>
    private async Task SettledAsync()
    {
        var load = Cdp.WaitForEventAsync("Page.loadEventFired", null, Timeout);
        var inDocument = Cdp.WaitForEventAsync("Page.navigatedWithinDocument", null, Timeout);
        var restored = Cdp.WaitForEventAsync("Page.frameNavigated", e => e.GetProperty("type").GetString() == "BackForwardCacheRestore", Timeout);
        await Task.WhenAny(load, inDocument, restored);
    }

    // ---- Elements ----------------------------------------------------------------------

    /// <summary>
    /// Waits until the element exists, is visible (and enabled, when asked), scrolls it into view
    /// and returns its centre in viewport pixels, where mouse input lands.
    /// </summary>
    public async Task<(double X, double Y)> CentreAsync(string target, bool enabled = true)
    {
        var script = $$"""
            (() => {
              const el = {{Find}}({{Js(target)}});
              if (!el) return { s: 'missing' };
              const r0 = el.getBoundingClientRect(), st = getComputedStyle(el);
              if (!(r0.width > 0 && r0.height > 0 && st.visibility !== 'hidden' && st.display !== 'none')) return { s: 'hidden' };
              if ({{(enabled ? "true" : "false")}} && el.disabled) return { s: 'disabled' };
              let r = r0;
              if (r.top < 0 || r.left < 0 || r.bottom > innerHeight || r.right > innerWidth) {
                el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
                r = el.getBoundingClientRect();
              }
              return { s: 'ok', x: r.left + r.width / 2, y: r.top + r.height / 2 };
            })()
            """;
        var deadline = DateTime.UtcNow + Timeout;
        var state = "missing";
        while (true)
        {
            var found = await EvaluateAsync(script);
            state = found.GetProperty("s").GetString() ?? "missing";
            if (state == "ok") return (found.GetProperty("x").GetDouble(), found.GetProperty("y").GetDouble());
            if (state == "missing" && IsRef(target))
            {
                throw new McpToolException($"No element {target} on this page. Refs only last until the next browser_snapshot (or a navigation): take a fresh browser_snapshot and use its refs.");
            }

            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(150);
        }

        throw new McpToolException(state switch
        {
            "hidden" => $"{target} is on the page but not visible.",
            "disabled" => $"{target} is disabled.",
            _ => $"Nothing matches {target}.",
        });
    }

    /// <summary>Fails fast on a stale ref, before a multi-step action half happens.</summary>
    public async Task EnsureRefExistsAsync(string target)
    {
        if (IsRef(target) && await EvaluateAsync($"!!{Find}({Js(target)})") is not { ValueKind: JsonValueKind.True })
        {
            throw new McpToolException($"No element {target.Trim()} on this page. Refs only last until the next browser_snapshot (or a navigation): take a fresh browser_snapshot and use its refs.");
        }
    }

    /// <summary>Runs a function on the element: <paramref name="body"/> sees it as <c>el</c>.</summary>
    public async Task<JsonElement> OnElementAsync(string target, string body)
    {
        await EnsureRefExistsAsync(target);
        return await EvaluateAsync($$"""
            (async () => {
              const el = {{Find}}({{Js(target)}});
              if (!el) throw new Error('Nothing matches ' + {{Js(target)}} + '.');
              {{body}}
            })()
            """);
    }

    // ---- Mouse and keyboard ------------------------------------------------------------

    private Task Mouse(string type, double x, double y, string button = "none", int clickCount = 0) =>
        Cdp.SendAsync("Input.dispatchMouseEvent", new
        {
            type, x, y, button, clickCount,
            buttons = type == "mousePressed" ? button switch { "left" => 1, "right" => 2, "middle" => 4, _ => 0 } : 0,
        });

    public async Task ClickAsync(string target, string button = "left", bool doubleClick = false)
    {
        var (x, y) = await CentreAsync(target);
        await Mouse("mouseMoved", x, y);
        for (var count = 1; count <= (doubleClick ? 2 : 1); count++)
        {
            await Mouse("mousePressed", x, y, button, count);
            await Mouse("mouseReleased", x, y, button, count);
        }
    }

    public async Task HoverAsync(string target)
    {
        var (x, y) = await CentreAsync(target, enabled: false);
        await Mouse("mouseMoved", x, y);
    }

    /// <summary>
    /// Replaces the field's contents as typing would: focus, select all, insert the text (so
    /// frameworks see real input events), with a value-setter fallback for date, color and range inputs.
    /// </summary>
    public async Task FillAsync(string target, string text)
    {
        await CentreAsync(target);
        var state = (await OnElementAsync(target, """
            el.focus();
            if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) { try { el.select(); } catch { } return 'field'; }
            if (el.isContentEditable) {
              const range = document.createRange(); range.selectNodeContents(el);
              const sel = getSelection(); sel.removeAllRanges(); sel.addRange(range);
              return 'editable';
            }
            return 'other';
            """)).GetString();
        if (state == "other") throw new McpToolException($"{target} is not a text field.");

        if (text.Length == 0) await EvaluateAsync("document.execCommand('delete')");
        else await Cdp.SendAsync("Input.insertText", new { text });

        if (state == "field")
        {
            await OnElementAsync(target, $$"""
                const want = {{Js(text)}};
                if (el.value === want) return;
                const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, want);
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
                """);
        }
    }

    public async Task SetCheckedAsync(string target, bool value)
    {
        var current = await OnElementAsync(target, "return !!el.checked;");
        if (current.ValueKind == (value ? JsonValueKind.True : JsonValueKind.False)) return;
        await ClickAsync(target);
    }

    public async Task SelectOptionAsync(string target, string value)
    {
        var ok = await OnElementAsync(target, $$"""
            if (!(el instanceof HTMLSelectElement)) throw new Error('Not a <select> element.');
            const want = {{Js(value)}};
            const option = [...el.options].find(o => o.value === want) || [...el.options].find(o => o.label.trim() === want.trim());
            if (!option) return false;
            el.value = option.value;
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            return true;
            """);
        if (ok.ValueKind != JsonValueKind.True) throw new McpToolException($"No option '{value}' in {target}.");
    }

    public async Task DragAsync(string from, string to)
    {
        await EnsureRefExistsAsync(from);
        await EnsureRefExistsAsync(to);
        var start = await CentreAsync(from);
        var end = await CentreAsync(to, enabled: false);

        // HTML5 drag and drop does not follow synthetic mouse input on its own: intercept the drag
        // Chromium starts and replay it at the drop point. Pointer-driven lists just see the moves.
        try { await Cdp.SendAsync("Input.setInterceptDrags", new { enabled = true }); } catch (CdpException) { }
        try
        {
            var intercepted = Cdp.WaitForEventAsync("Input.dragIntercepted", null, TimeSpan.FromMilliseconds(600));
            await Mouse("mouseMoved", start.X, start.Y);
            await Mouse("mousePressed", start.X, start.Y, "left", 1);
            for (var step = 1; step <= 10; step++)
            {
                await Mouse("mouseMoved", start.X + (end.X - start.X) * step / 10, start.Y + (end.Y - start.Y) * step / 10, "left");
            }

            if (await intercepted is { } drag && drag.TryGetProperty("data", out var data))
            {
                foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
                {
                    await Cdp.SendAsync("Input.dispatchDragEvent", new { type, x = end.X, y = end.Y, data });
                }
            }

            await Mouse("mouseReleased", end.X, end.Y, "left", 1);
        }
        finally
        {
            try { await Cdp.SendAsync("Input.setInterceptDrags", new { enabled = false }); } catch (CdpException) { }
        }
    }

    /// <summary>Presses a key or chord such as Enter, Escape, ArrowDown, Shift+Tab or Control+A.</summary>
    public async Task PressAsync(string chord)
    {
        var parts = chord.Length > 1 && chord.EndsWith("++", StringComparison.Ordinal)
            ? [.. chord[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries), "+"]
            : chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new McpToolException("Missing key.");

        var modifiers = 0;
        var held = new List<(string Key, int Code)>();
        foreach (var part in parts[..^1])
        {
            var (bit, key, code) = part.ToLowerInvariant() switch
            {
                "alt" => (1, "Alt", 18),
                "control" or "ctrl" => (2, "Control", 17),
                "meta" or "cmd" or "command" or "win" => (4, "Meta", 91),
                "shift" => (8, "Shift", 16),
                _ => throw new McpToolException($"Unknown modifier '{part}': use Control, Shift, Alt or Meta."),
            };
            modifiers |= bit;
            held.Add((key, code));
        }

        var info = KeyInfo(parts[^1]);
        var shift = (modifiers & 8) != 0;
        var text = info.Text is { } t && (modifiers & 7) == 0 ? (shift ? t.ToUpperInvariant() : t) : null;
        var keyName = text is { Length: 1 } ? text : info.Key;
        var command = (modifiers & 2) != 0 ? info.Key.ToLowerInvariant() switch
        {
            "a" => "selectAll", "c" => "copy", "x" => "cut", "v" => "paste", "z" => shift ? "redo" : "undo", "y" => "redo",
            _ => null,
        } : null;

        var mask = 0;
        foreach (var (key, code) in held)
        {
            mask |= key switch { "Alt" => 1, "Control" => 2, "Meta" => 4, _ => 8 };
            await Cdp.SendAsync("Input.dispatchKeyEvent", new { type = "rawKeyDown", key, code = key + "Left", windowsVirtualKeyCode = code, modifiers = mask });
        }

        await Cdp.SendAsync("Input.dispatchKeyEvent", new
        {
            type = text is null ? "rawKeyDown" : "keyDown",
            key = keyName, code = info.Code, windowsVirtualKeyCode = info.KeyCode, modifiers,
            text = text ?? "", unmodifiedText = text ?? "",
            commands = command is null ? Array.Empty<string>() : [command],
        });
        await Cdp.SendAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = keyName, code = info.Code, windowsVirtualKeyCode = info.KeyCode, modifiers });

        for (var i = held.Count - 1; i >= 0; i--)
        {
            var (key, code) = held[i];
            mask &= ~(key switch { "Alt" => 1, "Control" => 2, "Meta" => 4, _ => 8 });
            await Cdp.SendAsync("Input.dispatchKeyEvent", new { type = "keyUp", key, code = key + "Left", windowsVirtualKeyCode = code, modifiers = mask });
        }
    }

    private static (string Key, string Code, int KeyCode, string? Text) KeyInfo(string name)
    {
        var lower = name.ToLowerInvariant();
        switch (lower)
        {
            case "enter" or "return": return ("Enter", "Enter", 13, "\r");
            case "tab": return ("Tab", "Tab", 9, null);
            case "escape" or "esc": return ("Escape", "Escape", 27, null);
            case "backspace": return ("Backspace", "Backspace", 8, null);
            case "delete" or "del": return ("Delete", "Delete", 46, null);
            case "space": return (" ", "Space", 32, " ");
            case "arrowup" or "up": return ("ArrowUp", "ArrowUp", 38, null);
            case "arrowdown" or "down": return ("ArrowDown", "ArrowDown", 40, null);
            case "arrowleft" or "left": return ("ArrowLeft", "ArrowLeft", 37, null);
            case "arrowright" or "right": return ("ArrowRight", "ArrowRight", 39, null);
            case "home": return ("Home", "Home", 36, null);
            case "end": return ("End", "End", 35, null);
            case "pageup": return ("PageUp", "PageUp", 33, null);
            case "pagedown": return ("PageDown", "PageDown", 34, null);
            case "insert": return ("Insert", "Insert", 45, null);
        }

        if (lower.Length is 2 or 3 && lower[0] == 'f' && int.TryParse(lower[1..], out var f) && f is >= 1 and <= 12)
        {
            return ($"F{f}", $"F{f}", 111 + f, null);
        }

        if (name.Length == 1)
        {
            var c = name[0];
            var upper = char.ToUpperInvariant(c);
            return char.IsAsciiLetter(c) ? (c.ToString(), $"Key{upper}", upper, c.ToString())
                : char.IsAsciiDigit(c) ? (c.ToString(), $"Digit{c}", c, c.ToString())
                : (c.ToString(), "", 0, c.ToString());
        }

        throw new McpToolException($"Unknown key '{name}'. Use a key name such as Enter, Escape, Tab, ArrowDown, Backspace, F5, or a single character.");
    }

    // ---- Files and pictures ------------------------------------------------------------

    public async Task SetInputFilesAsync(string target, string[] files)
    {
        await EnsureRefExistsAsync(target);
        var handle = await Cdp.SendAsync("Runtime.evaluate", new { expression = $"{Find}({Js(target)})" });
        if (!handle.GetProperty("result").TryGetProperty("objectId", out var objectId))
        {
            throw new McpToolException($"Nothing matches {target}.");
        }

        await Cdp.SendAsync("DOM.setFileInputFiles", new { files, objectId = objectId.GetString() });
    }

    /// <summary>A screenshot of the viewport, the whole page, or one element.</summary>
    public async Task<byte[]> ScreenshotAsync(bool fullPage = false, string? target = null, bool jpeg = false)
    {
        object? clip = null;
        if (target is { Length: > 0 })
        {
            await CentreAsync(target, enabled: false);
            var box = await OnElementAsync(target, "const r = el.getBoundingClientRect(); return { x: r.left + scrollX, y: r.top + scrollY, width: r.width, height: r.height };");
            clip = new { x = box.GetProperty("x").GetDouble(), y = box.GetProperty("y").GetDouble(), width = box.GetProperty("width").GetDouble(), height = box.GetProperty("height").GetDouble(), scale = 1 };
        }
        else if (fullPage)
        {
            var metrics = await Cdp.SendAsync("Page.getLayoutMetrics");
            var size = metrics.TryGetProperty("cssContentSize", out var css) ? css : metrics.GetProperty("contentSize");
            clip = new { x = 0, y = 0, width = Math.Ceiling(size.GetProperty("width").GetDouble()), height = Math.Ceiling(size.GetProperty("height").GetDouble()), scale = 1 };
        }

        var shot = await Cdp.SendAsync("Page.captureScreenshot", new
        {
            format = jpeg ? "jpeg" : "png",
            quality = jpeg ? 75 : (int?)null,
            clip,
            captureBeyondViewport = clip is not null,
        }, TimeSpan.FromSeconds(60));
        return Convert.FromBase64String(shot.GetProperty("data").GetString() ?? "");
    }
}
