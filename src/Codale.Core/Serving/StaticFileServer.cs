using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Codale.Core.Serving;

/// <summary>
/// A read-only static file server for one folder on <c>http://localhost:PORT/</c>, built on
/// <see cref="HttpListener"/> (the kernel's http.sys), so it needs no package and no admin:
/// a localhost prefix is open to every user. Meant for looking at built output in a browser,
/// not for production traffic: nothing is cached and only GET and HEAD are answered.
/// </summary>
public sealed class StaticFileServer : IDisposable
{
    private const int BindAttempts = 5;

    private readonly HttpListener _listener;
    private readonly bool _spa;
    private readonly CancellationTokenSource _cts = new();

    /// <param name="root">The folder to serve; must exist.</param>
    /// <param name="port">A fixed port, or null for any free one.</param>
    /// <param name="spa">Answer unknown extensionless paths with the root <c>index.html</c> (client-side routing).</param>
    public StaticFileServer(string root, int? port = null, bool spa = false)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException($"Folder not found: {Root}");
        }

        _spa = spa;
        (_listener, Port) = Bind(port);
        Url = $"http://localhost:{Port}/";
        _ = Task.Run(AcceptLoopAsync);
    }

    public string Root { get; }

    public int Port { get; }

    public string Url { get; }

    private static (HttpListener, int) Bind(int? port)
    {
        for (var attempt = 1; ; attempt++)
        {
            var candidate = port ?? FreePort();
            var listener = new HttpListener { IgnoreWriteExceptions = true };
            listener.Prefixes.Add($"http://localhost:{candidate}/");
            try
            {
                listener.Start();
                return (listener, candidate);
            }
            catch (HttpListenerException e)
            {
                listener.Close();

                // A free port can be taken between the probe and the bind; a fixed one cannot be retried.
                if (port is not null || attempt >= BindAttempts)
                {
                    throw new IOException($"Could not listen on port {candidate}: {e.Message}", e);
                }
            }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Stopped.
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            await ServeAsync(context.Request, response).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The browser went away mid-response.
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Static server request failed: {e}");
            TryStatus(response, 500);
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private async Task ServeAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";

        // http.sys already refuses other host names; this keeps a DNS-rebound page out regardless.
        if (!IsLocalHost(request.Url?.Host))
        {
            await TextAsync(response, 403, "Forbidden").ConfigureAwait(false);
            return;
        }

        var head = request.HttpMethod == "HEAD";
        if (request.HttpMethod != "GET" && !head)
        {
            response.Headers["Allow"] = "GET, HEAD";
            await TextAsync(response, 405, "Method not allowed").ConfigureAwait(false);
            return;
        }

        var urlPath = request.Url?.AbsolutePath ?? "/";
        var path = Resolve(urlPath);
        if (path is null)
        {
            await TextAsync(response, 404, "Not found").ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(path))
        {
            if (!urlPath.EndsWith('/'))
            {
                response.StatusCode = 301;
                response.RedirectLocation = urlPath + "/" + request.Url?.Query;
                return;
            }

            var index = Path.Combine(path, "index.html");
            if (File.Exists(index))
            {
                await FileAsync(request, response, index, head).ConfigureAwait(false);
            }
            else
            {
                await HtmlAsync(response, Listing(path, urlPath), head).ConfigureAwait(false);
            }

            return;
        }

        if (File.Exists(path))
        {
            await FileAsync(request, response, path, head).ConfigureAwait(false);
            return;
        }

        var spaIndex = Path.Combine(Root, "index.html");
        if (_spa && Path.GetExtension(path).Length == 0 && File.Exists(spaIndex))
        {
            await FileAsync(request, response, spaIndex, head).ConfigureAwait(false);
            return;
        }

        await TextAsync(response, 404, "Not found").ConfigureAwait(false);
    }

    internal static bool IsLocalHost(string? host) =>
        host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    /// <summary>The file system path a URL path names, or null when it would leave the root.</summary>
    internal string? Resolve(string urlPath)
    {
        string relative;
        try
        {
            relative = Uri.UnescapeDataString(urlPath).TrimStart('/', '\\');
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (relative.Contains('\0') || relative.Contains(':'))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Root, relative)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var inside = full.Equals(Root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return inside ? full : null;
    }

    private static async Task FileAsync(HttpListenerRequest request, HttpListenerResponse response, string path, bool head)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        var length = file.Length;
        response.ContentType = ContentType(path);
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["Last-Modified"] = File.GetLastWriteTimeUtc(path).ToString("R", CultureInfo.InvariantCulture);

        long start = 0, end = length - 1;
        if (request.Headers["Range"] is { } range)
        {
            if (!TryParseRange(range, length, out start, out end))
            {
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{length}";
                return;
            }

            response.StatusCode = 206;
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
        }

        var count = length == 0 ? 0 : end - start + 1;
        response.ContentLength64 = count;
        if (head || count == 0)
        {
            return;
        }

        file.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var left = count;
        while (left > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left))).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await response.OutputStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            left -= read;
        }
    }

    /// <summary>One <c>bytes=a-b</c>, <c>bytes=a-</c> or <c>bytes=-n</c> range; multi-range requests are refused.</summary>
    internal static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',') || length == 0)
        {
            return false;
        }

        var spec = header[6..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var from = spec[..dash].Trim();
        var to = spec[(dash + 1)..].Trim();
        if (from.Length == 0)
        {
            // The last n bytes.
            if (!long.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, length - suffix);
            return true;
        }

        if (!long.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length)
        {
            return false;
        }

        if (to.Length > 0)
        {
            if (!long.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
            {
                return false;
            }

            end = Math.Min(end, length - 1);
        }

        return true;
    }

    private static string Listing(string directory, string urlPath)
    {
        var title = WebUtility.HtmlEncode(Uri.UnescapeDataString(urlPath));
        var html = new StringBuilder()
            .Append("<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">")
            .Append("<title>").Append(title).Append("</title>")
            .Append("<style>body{font:14px system-ui,sans-serif;margin:2em;color-scheme:light dark}a{text-decoration:none}li{padding:2px 0}</style>")
            .Append("<h1>").Append(title).Append("</h1><ul>");

        if (urlPath != "/")
        {
            html.Append("<li><a href=\"../\">../</a></li>");
        }

        var info = new DirectoryInfo(directory);
        foreach (var dir in info.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            Entry(html, dir.Name + "/");
        }

        foreach (var file in info.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            Entry(html, file.Name);
        }

        return html.Append("</ul>").ToString();

        static void Entry(StringBuilder html, string name)
        {
            var href = Uri.EscapeDataString(name.TrimEnd('/')) + (name.EndsWith('/') ? "/" : "");
            html.Append("<li><a href=\"").Append(href).Append("\">").Append(WebUtility.HtmlEncode(name)).Append("</a></li>");
        }
    }

    private static async Task HtmlAsync(HttpListenerResponse response, string html, bool head)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        if (!head)
        {
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
    }

    private static async Task TextAsync(HttpListenerResponse response, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static void TryStatus(HttpListenerResponse response, int status)
    {
        try
        {
            response.StatusCode = status;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Headers already went out.
        }
    }

    private static readonly Dictionary<string, string> s_types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".cjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".webmanifest"] = "application/manifest+json",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".ico"] = "image/x-icon",
        [".bmp"] = "image/bmp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".wasm"] = "application/wasm",
        [".pdf"] = "application/pdf",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".zip"] = "application/zip",
    };

    internal static string ContentType(string path) =>
        s_types.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
