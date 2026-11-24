using System.Text;
using System.Text.Json;

namespace Codale.Browser;

/// <summary>How a request or response body is shown in the request dialog.</summary>
internal static class BodyFormat
{
    /// <summary>Text longer than this is cut in the dialog (Copy still takes all of it); a TextBlock slows down well before this.</summary>
    public const int DisplayLimit = 300_000;

    public static bool IsJsonMime(string mime) =>
        mime.Contains("json", StringComparison.OrdinalIgnoreCase) || mime.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    public static bool IsImageMime(string mime) =>
        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !mime.Contains("svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for MIME types whose bytes are text (so a base64 body is decoded and shown).</summary>
    public static bool IsTextMime(string mime) =>
        mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || IsJsonMime(mime)
        || mime.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || mime.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || mime.Contains("svg", StringComparison.OrdinalIgnoreCase)
        || mime.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
        || mime.Contains("graphql", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pretty-printed, syntax-coloured JSON, or null when <paramref name="text"/> is not JSON.</summary>
    public static List<TextSegment>? Json(string text)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return null;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
            var output = new List<TextSegment>();
            var plain = new StringBuilder();
            Write(document.RootElement, 0, output, plain);
            Flush(output, plain);
            return output;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Write(JsonElement element, int depth, List<TextSegment> output, StringBuilder plain)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                if (properties.Count == 0)
                {
                    plain.Append("{}");
                    return;
                }

                plain.Append('{');
                for (var i = 0; i < properties.Count; i++)
                {
                    plain.Append('\n').Append(' ', (depth + 1) * 2);
                    Emit(output, plain, JsonSerializer.Serialize(properties[i].Name), DevToolsPalette.JsonKey);
                    plain.Append(": ");
                    Write(properties[i].Value, depth + 1, output, plain);
                    if (i < properties.Count - 1) plain.Append(',');
                }

                plain.Append('\n').Append(' ', depth * 2).Append('}');
                return;

            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    plain.Append("[]");
                    return;
                }

                plain.Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    plain.Append('\n').Append(' ', (depth + 1) * 2);
                    Write(items[i], depth + 1, output, plain);
                    if (i < items.Count - 1) plain.Append(',');
                }

                plain.Append('\n').Append(' ', depth * 2).Append(']');
                return;

            case JsonValueKind.String:
                Emit(output, plain, element.GetRawText(), DevToolsPalette.JsonString);
                return;

            case JsonValueKind.Number:
                Emit(output, plain, element.GetRawText(), DevToolsPalette.JsonNumber);
                return;

            default:
                Emit(output, plain, element.GetRawText(), DevToolsPalette.JsonLiteral);
                return;
        }
    }

    private static void Emit(List<TextSegment> output, StringBuilder plain, string text, TextStyle style)
    {
        Flush(output, plain);
        output.Add(new TextSegment(text, style));
    }

    private static void Flush(List<TextSegment> output, StringBuilder plain)
    {
        if (plain.Length == 0) return;
        output.Add(new TextSegment(plain.ToString()));
        plain.Clear();
    }

    /// <summary>The name/value pairs of a query string or an <c>application/x-www-form-urlencoded</c> body.</summary>
    public static List<KeyValuePair<string, string>> FormPairs(string text)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var part in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var name = equals < 0 ? part : part[..equals];
            var value = equals < 0 ? "" : part[(equals + 1)..];
            pairs.Add(new(Decode(name), Decode(value)));
        }

        return pairs;

        static string Decode(string s)
        {
            try
            {
                return Uri.UnescapeDataString(s.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return s;
            }
        }
    }

    /// <summary>A shell command that repeats the request (bash quoting), for "Copy as cURL".</summary>
    public static string Curl(NetworkRequest request, string? body)
    {
        static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
        var command = new StringBuilder("curl ").Append(Quote(request.Url));
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || body is not null) command.Append(" \\\n  -X ").Append(request.Method);
        foreach (var (name, value) in request.RequestHeaders)
        {
            // HTTP/2 pseudo-headers (:authority, :path, ...) are not sent as headers.
            if (name.StartsWith(':')) continue;
            command.Append(" \\\n  -H ").Append(Quote($"{name}: {value}"));
        }

        if (body is not null) command.Append(" \\\n  --data-raw ").Append(Quote(body));
        return command.ToString();
    }

    public static string Header(List<KeyValuePair<string, string>> headers, string name) =>
        headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? "";
}
