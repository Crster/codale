using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Codale.Browser;

/// <summary>
/// A bounded, filterable list of rows. <see cref="Visible"/> is what the list shows; <see cref="All"/>
/// keeps every row (up to the limit) so a filter can be changed without asking the page again.
/// </summary>
public sealed class Feed<T>(Func<T, string> searchText) where T : DiagnosticsRow
{
    public const int Limit = 500;

    private readonly List<T> _all = [];
    private readonly List<T> _pending = [];
    private readonly List<T> _evicted = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private bool _dirty;
    private string _filter = "";
    private Func<T, bool>? _predicate;

    /// <summary>Every row kept (up to the limit), whatever the filters show.</summary>
    public IReadOnlyList<T> All => _all;

    public ObservableCollection<T> Visible { get; } = [];

    /// <summary>Raised when rows are added, removed or re-filtered (not when a row's own values change).</summary>
    public event Action? Changed;

    public int Total => _all.Count;

    /// <summary>
    /// Adds a row and returns the oldest row it pushed out, if the limit was reached. The list the UI binds to
    /// is updated in batches (see <see cref="Flush"/>): a page logging or fetching hundreds of times a second
    /// would otherwise flood the UI thread with collection changes.
    /// </summary>
    public T? Add(T row)
    {
        _all.Add(row);
        _pending.Add(row);
        T? evicted = null;
        if (_all.Count > Limit)
        {
            evicted = _all[0];
            _all.RemoveAt(0);
            _evicted.Add(evicted);
        }

        Schedule();
        return evicted;
    }

    /// <summary>Call after a row's values change: with a filter on, the row may now match (or stop matching).</summary>
    public void Refresh(T row)
    {
        if (_predicate is null && _filter.Length == 0) return;
        _dirty = true;
        Schedule();
    }

    public void SetFilter(string filter)
    {
        _filter = filter;
        _dirty = true;
        Flush();
    }

    /// <summary>A category filter (the chips above the list); null shows every category.</summary>
    public void SetPredicate(Func<T, bool>? predicate)
    {
        _predicate = predicate;
        _dirty = true;
        Flush();
    }

    public void Clear()
    {
        _all.Clear();
        _pending.Clear();
        _evicted.Clear();
        _dirty = false;
        Visible.Clear();
        Changed?.Invoke();
    }

    private void Schedule()
    {
        _timer ??= CreateTimer();
        if (_timer is { IsRunning: false } timer) timer.Start();
        else if (_timer is null) Flush();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? CreateTimer()
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return null;
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Flush();
        return timer;
    }

    /// <summary>Brings <see cref="Visible"/> up to date with the rows added, evicted or re-filtered since the last flush.</summary>
    private void Flush()
    {
        if (_dirty)
        {
            _dirty = false;
            _pending.Clear();
            _evicted.Clear();
            Visible.Clear();
            foreach (var row in _all) if (Matches(row)) Visible.Add(row);
            Changed?.Invoke();
            return;
        }

        if (_pending.Count == 0 && _evicted.Count == 0) return;
        foreach (var row in _pending) if (Matches(row)) Visible.Add(row);
        foreach (var gone in _evicted)
        {
            if (Visible.Count > 0 && ReferenceEquals(Visible[0], gone)) Visible.RemoveAt(0);
            else Visible.Remove(gone);
        }

        _pending.Clear();
        _evicted.Clear();
        Changed?.Invoke();
    }

    private bool Matches(T row) => (_predicate is null || _predicate(row))
        && (_filter.Length == 0 || searchText(row).Contains(_filter, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The page's activity for one tab, from DevTools: requests (Network domain) and console output
/// (Runtime domain). Events arrive on the UI thread. Each tab keeps its own history.
/// </summary>
public sealed class DiagnosticsFeed
{
    private readonly Dictionary<string, NetworkRow> _byId = [];
    private CoreWebView2? _core;

    public Feed<NetworkRow> Network { get; } = new(r => r.SearchText);
    public Feed<ConsoleRow> Console { get; } = new(r => r.SearchText);

    /// <summary>Raised when a request finishes or fails, so an open detail view can show its response.</summary>
    public event Action<NetworkRow>? RequestUpdated;

    public void Attach(CoreWebView2 core)
    {
        _core = core;
        Listen(core, "Network.requestWillBeSent", Request);
        Listen(core, "Network.responseReceived", Response);
        Listen(core, "Network.loadingFinished", Finished);
        Listen(core, "Network.loadingFailed", Failed);
        Listen(core, "Runtime.consoleAPICalled", ConsoleMessage);
        Listen(core, "Runtime.exceptionThrown", ConsoleException);
    }

    public void SetFilter(string filter)
    {
        Network.SetFilter(filter);
        Console.SetFilter(filter);
    }

    public void Clear()
    {
        Network.Clear();
        Console.Clear();
        _byId.Clear();
    }

    /// <summary>Loads a finished text response's body (up to 1 MB, truncated for display). Returns false when it is not available.</summary>
    public async Task<bool> LoadResponseBodyAsync(NetworkRow entry)
    {
        if (entry.BodyLoaded || !entry.Finished || entry.IsRedirect || entry.Error.Length > 0 || entry.Size > 1024 * 1024 || !IsText(entry.Mime) || _core is null) return false;
        entry.BodyLoaded = true;
        try
        {
            var result = await _core.CallDevToolsProtocolMethodAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId = entry.Id }));
            using var json = JsonDocument.Parse(result);
            var body = Value(json.RootElement, "body");
            if (json.RootElement.TryGetProperty("base64Encoded", out var encoded) && encoded.ValueKind == JsonValueKind.True)
                body = Encoding.UTF8.GetString(Convert.FromBase64String(body));
            entry.ResponseBody = body.Length > 100_000 ? body[..100_000] + "\r\n… (truncated)" : body;
        }
        catch (Exception ex) when (ex is COMException or JsonException or InvalidOperationException or FormatException)
        {
            entry.ResponseBody = "(body unavailable)";
        }

        return true;
    }

    private static void Listen(CoreWebView2 core, string name, Action<JsonElement> action)
    {
        core.GetDevToolsProtocolEventReceiver(name).DevToolsProtocolEventReceived += (_, e) =>
        {
            try
            {
                using var json = JsonDocument.Parse(e.ParameterObjectAsJson);
                action(json.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        };
    }

    private void Request(JsonElement e)
    {
        var id = Value(e, "requestId");
        if (e.TryGetProperty("redirectResponse", out var redirect) && _byId.TryGetValue(id, out var previous))
        {
            SetResponse(previous, redirect);
            previous.End = Number(e, "timestamp");
            previous.Finished = true;
            previous.IsRedirect = true;
            Touch(previous);
        }

        var request = e.GetProperty("request");
        var entry = new NetworkRow(id)
        {
            Url = Value(request, "url"),
            Method = Value(request, "method"),
            Type = Value(e, "type"),
            Start = Number(e, "timestamp"),
            RequestHeaders = Headers(request),
            RequestBody = Value(request, "postData"),
        };
        entry.PathText = DisplayUrl(entry.Url);
        _byId[id] = entry;
        if (Network.Add(entry) is { } evicted && _byId.TryGetValue(evicted.Id, out var stored) && ReferenceEquals(stored, evicted))
            _byId.Remove(evicted.Id);
    }

    private void Response(JsonElement e)
    {
        if (!_byId.TryGetValue(Value(e, "requestId"), out var entry)) return;
        SetResponse(entry, e.GetProperty("response"));
        entry.Type = Value(e, "type");
        Touch(entry);
    }

    private void Finished(JsonElement e)
    {
        if (!_byId.TryGetValue(Value(e, "requestId"), out var entry)) return;
        entry.End = Number(e, "timestamp");
        entry.Size = (long)Number(e, "encodedDataLength");
        entry.Finished = true;
        Touch(entry);
        RequestUpdated?.Invoke(entry);
    }

    private void Failed(JsonElement e)
    {
        if (!_byId.TryGetValue(Value(e, "requestId"), out var entry)) return;
        entry.End = Number(e, "timestamp");
        entry.Error = Value(e, "errorText");
        entry.Finished = true;
        Touch(entry);
        RequestUpdated?.Invoke(entry);
    }

    private void ConsoleMessage(JsonElement e)
    {
        var level = Value(e, "type");
        var message = e.TryGetProperty("args", out var args)
            ? string.Join(" ", args.EnumerateArray().Select(a => a.TryGetProperty("value", out var v) ? v.ToString() : Value(a, "description")))
            : "";
        Console.Add(new ConsoleRow(level, message, DateTime.Now) { Source = SourceOf(e) });
    }

    private void ConsoleException(JsonElement e)
    {
        var details = e.GetProperty("exceptionDetails");
        var message = details.TryGetProperty("exception", out var exception) ? Value(exception, "description") : Value(details, "text");
        var source = SourceOf(details);
        Console.Add(new ConsoleRow("error", message, DateTime.Now) { Source = source.Length > 0 ? source : FrameText(details) });
    }

    /// <summary>Where a console call came from: the first frame of its stack, as url:line:column (empty when unknown).</summary>
    private static string SourceOf(JsonElement e) =>
        e.TryGetProperty("stackTrace", out var trace) && trace.TryGetProperty("callFrames", out var frames)
        && frames.ValueKind == JsonValueKind.Array && frames.GetArrayLength() > 0
            ? FrameText(frames[0])
            : "";

    private static string FrameText(JsonElement frame)
    {
        var url = Value(frame, "url");
        if (url.Length == 0) return "";
        // CDP line and column numbers are zero-based; people count from one.
        return $"{url}:{Number(frame, "lineNumber") + 1}:{Number(frame, "columnNumber") + 1}";
    }

    private void Touch(NetworkRow entry)
    {
        entry.Changed();
        Network.Refresh(entry);
    }

    private string DisplayUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var target)
            && Uri.TryCreate(_core?.Source, UriKind.Absolute, out var page)
            && target.Scheme == page.Scheme && target.Host == page.Host && target.Port == page.Port)
            return target.PathAndQuery;
        return url;
    }

    private static void SetResponse(NetworkRow entry, JsonElement response)
    {
        entry.Status = Value(response, "status");
        entry.Mime = Value(response, "mimeType");
        entry.ResponseHeaders = Headers(response);
    }

    private static string Value(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var p) && p.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? p.ToString() : "";

    private static double Number(JsonElement obj, string key) => obj.TryGetProperty(key, out var p) && p.TryGetDouble(out var n) ? n : 0;

    private static string Headers(JsonElement obj) =>
        obj.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object
            ? string.Join(Environment.NewLine, h.EnumerateObject().Select(x => $"{x.Name}: {x.Value}"))
            : "(not available)";

    private static bool IsText(string mime) =>
        mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mime.Contains("json", StringComparison.OrdinalIgnoreCase)
        || mime.Contains("xml", StringComparison.OrdinalIgnoreCase) || mime.Contains("javascript", StringComparison.OrdinalIgnoreCase);
}
