using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Codale.Browser;

/// <summary>
/// What one tab's page sent over the DevTools protocol: network requests and console output. Kept per tab, so the
/// panel shows the page in front of you and a tab keeps its history while it is in the background. Every event
/// arrives on the UI thread (WebView2 raises them there), so the log and its rows are only touched from it.
/// </summary>
public sealed class DevToolsLog
{
    private const int Limit = 1500;
    private readonly Dictionary<string, NetworkRequest> _requests = [];
    private readonly Stack<ConsoleLine> _groups = new();
    private int _redirects;

    public List<NetworkRequest> NetworkRequests { get; } = [];

    public List<ConsoleLine> ConsoleLines { get; } = [];

    public int ErrorCount { get; private set; }

    public int WarningCount { get; private set; }

    /// <summary>A request or console line was added (the panel appends it when it passes the filter).</summary>
    public event Action<object>? Added;

    /// <summary>The oldest entry was dropped to keep the log bounded.</summary>
    public event Action<object>? Removed;

    /// <summary>The network list, the console, or both were emptied.</summary>
    public event Action? Cleared;

    /// <summary>Anything changed (including a request completing); the panel refreshes its counts from it.</summary>
    public event Action? Changed;

    // ---- Network ---------------------------------------------------------------------

    public void OnRequestWillBeSent(JsonElement p)
    {
        var id = Str(p, "requestId");
        if (id.Length == 0) return;
        var request = p.TryGetProperty("request", out var r) ? r : default;

        if (_requests.TryGetValue(id, out var previous))
        {
            // A redirect reuses the request id: the earlier hop is completed with the redirect response and kept as its own row.
            if (p.TryGetProperty("redirectResponse", out var redirect)) previous.ApplyResponse(redirect);
            previous.IsRedirect = true;
            previous.Finish(Num(p, "timestamp"), null);
            _requests.Remove(id);
            _requests[$"{id}#redirect{++_redirects}"] = previous;
        }

        var entry = new NetworkRequest(id)
        {
            Method = Str(request, "method", "GET"),
            Url = Str(request, "url") + Str(request, "urlFragment"),
            Type = Str(p, "type", "Other"),
            Started = Num(p, "wallTime") is { } wall ? DateTimeOffset.FromUnixTimeMilliseconds((long)(wall * 1000)).LocalDateTime : DateTime.Now,
            StartTime = Num(p, "timestamp"),
            RequestHeaders = Headers(request),
            PostData = Str(request, "postData") is { Length: > 0 } body ? body : null,
            HasPostData = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("hasPostData", out var has) && has.ValueKind == JsonValueKind.True,
            Initiator = p.TryGetProperty("initiator", out var initiator) ? Str(initiator, "type") : "",
        };
        _requests[id] = entry;
        Add(NetworkRequests, entry);
    }

    public void OnResponseReceived(JsonElement p)
    {
        if (!_requests.TryGetValue(Str(p, "requestId"), out var request) || !p.TryGetProperty("response", out var response)) return;
        if (Str(p, "type") is { Length: > 0 } type) request.Type = type;
        request.ApplyResponse(response);
        request.Notify();
        Changed?.Invoke();
    }

    public void OnLoadingFinished(JsonElement p)
    {
        if (!_requests.TryGetValue(Str(p, "requestId"), out var request)) return;
        if (Num(p, "encodedDataLength") is { } size) request.TransferSize = (long)size;
        request.Finish(Num(p, "timestamp"), null);
        Changed?.Invoke();
    }

    public void OnLoadingFailed(JsonElement p)
    {
        if (!_requests.TryGetValue(Str(p, "requestId"), out var request)) return;
        var canceled = p.TryGetProperty("canceled", out var c) && c.ValueKind == JsonValueKind.True;
        var reason = Str(p, "blockedReason") is { Length: > 0 } blocked ? $"blocked:{blocked}" : Str(p, "errorText", "Failed");
        request.Finish(Num(p, "timestamp"), canceled ? "canceled" : reason);
        Changed?.Invoke();
    }

    public void OnServedFromCache(JsonElement p)
    {
        if (!_requests.TryGetValue(Str(p, "requestId"), out var request)) return;
        request.CacheSource = "memory cache";
        request.Notify();
    }

    public void OnWebSocketCreated(JsonElement p)
    {
        var id = Str(p, "requestId");
        if (id.Length == 0 || _requests.ContainsKey(id)) return;
        var entry = new NetworkRequest(id) { Method = "GET", Url = Str(p, "url"), Type = "WebSocket", Started = DateTime.Now };
        _requests[id] = entry;
        Add(NetworkRequests, entry);
    }

    public void OnWebSocketHandshake(JsonElement p)
    {
        if (!_requests.TryGetValue(Str(p, "requestId"), out var request) || !p.TryGetProperty("response", out var response)) return;
        request.ApplyResponse(response);
        request.Notify();
        Changed?.Invoke();
    }

    public void OnWebSocketClosed(JsonElement p)
    {
        if (!_requests.TryGetValue(Str(p, "requestId"), out var request)) return;
        request.Finish(null, null);
        Changed?.Invoke();
    }

    // ---- Console ---------------------------------------------------------------------

    public void OnConsoleApiCalled(JsonElement p)
    {
        var kind = Str(p, "type", "log");
        if (kind == "clear")
        {
            ClearConsole();
            AddConsole(new ConsoleLine(ConsoleLevel.Debug, ConsoleFormat.FromText("Console was cleared"), Time(p), "", "", _groups.Count));
            return;
        }

        if (kind == "endGroup")
        {
            if (_groups.Count > 0) _groups.Pop();
            return;
        }

        var segments = ConsoleFormat.FromArgs(p.TryGetProperty("args", out var args) ? args : default);
        if (kind is "assert") segments.Insert(0, new TextSegment("Assertion failed: "));
        if (kind is "startGroup" or "startGroupCollapsed")
        {
            segments.Insert(0, new TextSegment("▾ ", DevToolsPalette.Muted));
            segments = [.. segments.Select(s => s with { Style = s.Style with { Bold = true } })];
        }

        var level = kind switch
        {
            "error" or "assert" => ConsoleLevel.Error,
            "warning" => ConsoleLevel.Warning,
            "info" => ConsoleLevel.Info,
            "debug" => ConsoleLevel.Debug,
            _ => ConsoleLevel.Log,
        };

        var (source, sourceUrl) = SourceOf(p.TryGetProperty("stackTrace", out var stack) ? stack : default);
        var line = new ConsoleLine(level, segments, Time(p), source, sourceUrl, _groups.Count);
        AddConsole(line);
        if (kind is "startGroup" or "startGroupCollapsed") _groups.Push(line);
    }

    public void OnExceptionThrown(JsonElement p)
    {
        var message = "Uncaught error";
        var source = ("", "");
        if (p.TryGetProperty("exceptionDetails", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            var text = Str(details, "text", "Uncaught");
            var description = details.TryGetProperty("exception", out var exception) ? ConsoleFormat.Describe(exception, top: true) : "";
            message = description.Length == 0 ? text : $"{text} {description}";
            source = details.TryGetProperty("stackTrace", out var stack) ? SourceOf(stack) : LocationOf(Str(details, "url"), Num(details, "lineNumber"));
        }

        AddConsole(new ConsoleLine(ConsoleLevel.Error, ConsoleFormat.FromText(message), Time(p), source.Item1, source.Item2, _groups.Count));
    }

    /// <summary>A browser-side message (<c>Log.entryAdded</c>): CSP violations, blocked mixed content, intervention notices.</summary>
    public void OnLogEntry(JsonElement p)
    {
        if (!p.TryGetProperty("entry", out var entry)) return;
        var level = Str(entry, "level") switch
        {
            "error" => ConsoleLevel.Error,
            "warning" => ConsoleLevel.Warning,
            "info" => ConsoleLevel.Info,
            _ => ConsoleLevel.Debug,
        };
        var (source, url) = LocationOf(Str(entry, "url"), Num(entry, "lineNumber"));
        AddConsole(new ConsoleLine(level, ConsoleFormat.FromText(Str(entry, "text")), Time(entry), source, url, _groups.Count));
    }

    private void AddConsole(ConsoleLine line)
    {
        // A message identical to the one before it bumps that line's counter, as DevTools does, instead of adding a row.
        if (ConsoleLines.Count > 0 && ConsoleLines[^1] is var last && last.IsRepeatOf(line))
        {
            last.Repeat(line.Time);
            Count(line.Level, +1);
            Changed?.Invoke();
            return;
        }

        Count(line.Level, +1);
        Add(ConsoleLines, line);
    }

    private void Count(ConsoleLevel level, int delta)
    {
        if (level == ConsoleLevel.Error) ErrorCount = Math.Max(0, ErrorCount + delta);
        else if (level == ConsoleLevel.Warning) WarningCount = Math.Max(0, WarningCount + delta);
    }

    // ---- Shared ----------------------------------------------------------------------

    private void Add<T>(List<T> list, T item) where T : class
    {
        list.Add(item);
        Added?.Invoke(item);
        if (list.Count > Limit)
        {
            var oldest = list[0];
            list.RemoveAt(0);
            if (oldest is NetworkRequest request) Forget(request);
            if (oldest is ConsoleLine line) Count(line.Level, -line.Count);
            Removed?.Invoke(oldest);
        }

        Changed?.Invoke();
    }

    private void Forget(NetworkRequest request)
    {
        foreach (var key in _requests.Where(pair => ReferenceEquals(pair.Value, request)).Select(pair => pair.Key).ToList()) _requests.Remove(key);
    }

    public void ClearNetwork()
    {
        NetworkRequests.Clear();
        _requests.Clear();
        Cleared?.Invoke();
        Changed?.Invoke();
    }

    public void ClearConsole()
    {
        ConsoleLines.Clear();
        _groups.Clear();
        ErrorCount = 0;
        WarningCount = 0;
        Cleared?.Invoke();
        Changed?.Invoke();
    }

    public void Clear()
    {
        NetworkRequests.Clear();
        _requests.Clear();
        ConsoleLines.Clear();
        _groups.Clear();
        ErrorCount = 0;
        WarningCount = 0;
        Cleared?.Invoke();
        Changed?.Invoke();
    }

    private static DateTime Time(JsonElement p) =>
        Num(p, "timestamp") is { } ms and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime : DateTime.Now;

    private static (string Source, string Url) SourceOf(JsonElement stack)
    {
        if (stack.ValueKind != JsonValueKind.Object || !stack.TryGetProperty("callFrames", out var frames) || frames.ValueKind != JsonValueKind.Array) return ("", "");
        foreach (var frame in frames.EnumerateArray())
        {
            var url = Str(frame, "url");
            if (url.Length > 0) return LocationOf(url, Num(frame, "lineNumber"));
        }

        return ("", "");
    }

    private static (string Source, string Url) LocationOf(string url, double? line)
    {
        if (url.Length == 0) return ("", "");
        var name = NetworkRequest.FileName(url);
        return (line is { } n ? $"{name}:{(int)n + 1}" : name, url);
    }

    private static List<KeyValuePair<string, string>> Headers(JsonElement owner)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object) return [];
        return [.. headers.EnumerateObject().Select(h => new KeyValuePair<string, string>(h.Name, h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString() ?? "" : h.Value.GetRawText()))];
    }

    internal static string Str(JsonElement element, string name, string fallback = "") => ConsoleFormat.Str(element, name, fallback);

    internal static double? Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    // ---- NetworkRequest response mapping lives here so the model keeps one parser. ----

    internal static void ApplyResponse(NetworkRequest request, JsonElement response)
    {
        request.Status = response.TryGetProperty("status", out var status) && status.TryGetInt32(out var code) ? code : request.Status;
        request.StatusText = Str(response, "statusText");
        request.MimeType = Str(response, "mimeType");
        request.Protocol = Str(response, "protocol");
        request.ResponseHeaders = Headers(response);
        var ip = Str(response, "remoteIPAddress");
        if (ip.Length > 0)
        {
            var port = Num(response, "remotePort");
            request.RemoteAddress = ip.Contains(':') ? $"[{ip}]" : ip;
            if (port is { } n) request.RemoteAddress += ":" + ((int)n).ToString(CultureInfo.InvariantCulture);
        }

        if (response.TryGetProperty("fromDiskCache", out var disk) && disk.ValueKind == JsonValueKind.True) request.CacheSource = "disk cache";
        else if (response.TryGetProperty("fromServiceWorker", out var worker) && worker.ValueKind == JsonValueKind.True) request.CacheSource = "service worker";
        else if (response.TryGetProperty("fromPrefetchCache", out var prefetch) && prefetch.ValueKind == JsonValueKind.True) request.CacheSource = "prefetch cache";

        // A response carries its own copy of the request headers when the network stack sent extra ones (cookies and the like).
        if (response.TryGetProperty("requestHeaders", out var sent) && sent.ValueKind == JsonValueKind.Object)
        {
            request.RequestHeaders = [.. sent.EnumerateObject().Select(h => new KeyValuePair<string, string>(h.Name, h.Value.GetString() ?? ""))];
        }
    }
}

/// <summary>The resource groups the Network filter offers, in DevTools' order.</summary>
public enum RequestCategory { All, FetchXhr, Document, Css, Script, Font, Image, Media, WebSocket, Other }

/// <summary>One network request: created when the page starts it, completed by its response or failure.</summary>
public sealed class NetworkRequest(string id) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The DevTools request id (redirect hops share it with the request that followed them).</summary>
    public string Id { get; } = id;

    public string Method { get; init; } = "";

    public string Url { get; init; } = "";

    /// <summary>The resource type as the browser reports it (Document, Script, XHR, Fetch, Image, ...).</summary>
    public string Type { get; set; } = "";

    public DateTime Started { get; init; }

    /// <summary>The protocol's monotonic clock (seconds) at the start and end, for the duration.</summary>
    public double? StartTime { get; init; }

    public double? EndTime { get; private set; }

    public List<KeyValuePair<string, string>> RequestHeaders { get; set; } = [];

    public string? PostData { get; set; }

    public bool HasPostData { get; init; }

    public string Initiator { get; init; } = "";

    public int? Status { get; set; }

    public string StatusText { get; set; } = "";

    public string MimeType { get; set; } = "";

    public string Protocol { get; set; } = "";

    public string RemoteAddress { get; set; } = "";

    public string? CacheSource { get; set; }

    public List<KeyValuePair<string, string>> ResponseHeaders { get; set; } = [];

    public long? TransferSize { get; set; }

    public string? Error { get; private set; }

    public bool IsFinished { get; private set; }

    /// <summary>True for a redirect hop: its response sent the browser on to the next request, so it has no body.</summary>
    public bool IsRedirect { get; internal set; }

    public bool IsFailed => Error is not null;

    public RequestCategory Category => Type switch
    {
        "XHR" or "Fetch" or "EventSource" or "Preflight" => RequestCategory.FetchXhr,
        "Document" => RequestCategory.Document,
        "Stylesheet" => RequestCategory.Css,
        "Script" => RequestCategory.Script,
        "Font" => RequestCategory.Font,
        "Image" => RequestCategory.Image,
        "Media" or "TextTrack" => RequestCategory.Media,
        "WebSocket" => RequestCategory.WebSocket,
        _ => RequestCategory.Other,
    };

    // ---- Display (bound by the row template) -----------------------------------------

    public string TimeText => Started.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string StatusLabel => Error switch
    {
        "canceled" => "(canceled)",
        { } error when error.StartsWith("blocked:", StringComparison.Ordinal) => "(blocked)",
        not null when Status is null => "(failed)",
        _ => Status?.ToString(CultureInfo.InvariantCulture) ?? "(pending)",
    };

    public string StatusTooltip => Error is { } error ? error : Status is { } code ? $"{code} {StatusText}".Trim() : "Waiting for a response";

    public string Name => FileName(Url);

    /// <summary>The host and path, dimmed beside the name so two files with the same name can be told apart.</summary>
    public string Location => Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ws" or "wss"
        ? uri.Host + uri.AbsolutePath[..Math.Max(0, uri.AbsolutePath.LastIndexOf('/'))]
        : "";

    public string TypeLabel => Type switch
    {
        "XHR" => "xhr",
        "Fetch" => "fetch",
        "Stylesheet" => "stylesheet",
        "Document" => "document",
        "Script" => "script",
        "Image" => "image",
        "Font" => "font",
        "Media" => "media",
        "WebSocket" => "websocket",
        "EventSource" => "eventsource",
        "Preflight" => "preflight",
        _ => Type.ToLowerInvariant(),
    };

    public string SizeText => CacheSource is { } cache && (TransferSize is null or < 1024) ? $"({cache})"
        : TransferSize is { } size ? FormatSize(size)
        : IsFinished ? "—" : "";

    public double? DurationMs => StartTime is { } start && EndTime is { } end ? Math.Max(0, (end - start) * 1000) : null;

    public string DurationText => DurationMs is { } ms ? FormatDuration(ms) : IsFinished ? "—" : "…";

    public Brush MethodBrush => DevToolsPalette.Method(Method);

    public Brush StatusBrush => DevToolsPalette.Status(Status, IsFailed);

    public Brush RowBackground => DevToolsPalette.RequestRow(Status, IsFailed);

    // ---- Updates -----------------------------------------------------------------------

    internal void ApplyResponse(JsonElement response) => DevToolsLog.ApplyResponse(this, response);

    internal void Finish(double? timestamp, string? error)
    {
        EndTime = timestamp ?? EndTime;
        if (error is not null) Error = error;
        IsFinished = true;
        Notify();
    }

    /// <summary>Tells the bound row to read every property again (one notification per update keeps it cheap).</summary>
    internal void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public static string FileName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (uri.Scheme == "data") return url.Length > 48 ? url[..48] + "…" : url;
        var path = uri.AbsolutePath.TrimEnd('/');
        var name = path.Length == 0 ? uri.Host : Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        return name + uri.Query;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} kB",
        _ => $"{bytes / 1024.0 / 1024.0:0.00} MB",
    };

    public static string FormatDuration(double ms) => ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.00} s";
}

public enum ConsoleLevel { Debug, Log, Info, Warning, Error }

/// <summary>One console message, uncaught error, or browser log entry, already split into styled runs.</summary>
public sealed class ConsoleLine : INotifyPropertyChanged
{
    public ConsoleLine(ConsoleLevel level, IReadOnlyList<TextSegment> segments, DateTime time, string source, string sourceUrl, int depth)
    {
        Level = level;
        Segments = segments;
        Text = ConsoleFormat.PlainText(segments);
        Time = time;
        Source = source;
        SourceUrl = sourceUrl;
        Depth = depth;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConsoleLevel Level { get; }

    public IReadOnlyList<TextSegment> Segments { get; }

    /// <summary>The message without styling, for the filter and for copying.</summary>
    public string Text { get; }

    public DateTime Time { get; private set; }

    /// <summary>Where the message came from, as <c>file.js:12</c>; <see cref="SourceUrl"/> is the full address.</summary>
    public string Source { get; }

    public string SourceUrl { get; }

    /// <summary>How many <c>console.group</c> levels the line sits in.</summary>
    public int Depth { get; }

    /// <summary>How many times the same message arrived in a row.</summary>
    public int Count { get; private set; } = 1;

    // ---- Display (bound by the row template) -----------------------------------------

    public string TimeText => Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string Glyph => Level switch
    {
        ConsoleLevel.Error => "",
        ConsoleLevel.Warning => "",
        ConsoleLevel.Info => "",
        ConsoleLevel.Debug => "",
        _ => "",
    };

    public string CountText => Count.ToString(CultureInfo.InvariantCulture);

    public Visibility CountVisibility => Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SourceVisibility => Source.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string SourceTooltip => SourceUrl.Length > 0 ? SourceUrl : Source;

    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    public Brush TextBrush => DevToolsPalette.ConsoleText(Level);

    public Brush IconBrush => DevToolsPalette.ConsoleIcon(Level);

    public Brush RowBackground => DevToolsPalette.ConsoleRow(Level);

    public Brush DividerBrush => DevToolsPalette.ConsoleDivider(Level);

    public Brush CountBackground => DevToolsPalette.ConsoleIcon(Level == ConsoleLevel.Log ? ConsoleLevel.Debug : Level);

    internal bool IsRepeatOf(ConsoleLine other) =>
        Level == other.Level && Depth == other.Depth && Source == other.Source && Text == other.Text && Segments.Count == other.Segments.Count;

    internal void Repeat(DateTime time)
    {
        Count++;
        Time = time;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
