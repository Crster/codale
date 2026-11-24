using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Windows.UI;

namespace Codale.Browser;

/// <summary>How one run of text is drawn. ANSI colours are kept as palette indexes so they follow the light/dark theme.</summary>
public readonly record struct TextStyle(
    Color? Foreground = null,
    Color? Background = null,
    int? AnsiForeground = null,
    int? AnsiBackground = null,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false)
{
    public bool HasBackground => Background is not null || AnsiBackground is not null;
}

/// <summary>A run of text with one style; a console message or a coloured body is a list of these.</summary>
public sealed record TextSegment(string Text, TextStyle Style = default);

/// <summary>
/// Turns console output into styled text the way DevTools shows it: format specifiers (<c>%s %d %i %f %o %O %c</c>) are
/// applied to the arguments, <c>%c</c> CSS colours become run colours, and ANSI escape sequences (as Node, Vite and other
/// tools print them) become colours instead of showing up as stray characters.
/// </summary>
internal static class ConsoleFormat
{
    /// <summary>The message for a <c>Runtime.consoleAPICalled</c> argument list.</summary>
    public static List<TextSegment> FromArgs(JsonElement args)
    {
        var items = args.ValueKind == JsonValueKind.Array ? args.EnumerateArray().ToList() : [];
        var output = new Builder();
        if (items.Count == 0) return output.Segments;

        var next = 0;
        if (Str(items[0], "type") == "string" && items[0].TryGetProperty("value", out var format) && format.ValueKind == JsonValueKind.String)
        {
            next = 1;
            ApplyFormat(format.GetString() ?? "", items, ref next, output);
            output.Css = default;
        }

        for (; next < items.Count; next++)
        {
            if (next > 0) output.Append(" ");
            output.Append(Describe(items[next], top: true));
        }

        return output.Segments;
    }

    /// <summary>A plain message (an exception, a browser log entry) with any ANSI sequences turned into colours.</summary>
    public static List<TextSegment> FromText(string text)
    {
        var output = new Builder();
        output.Append(text);
        return output.Segments;
    }

    public static string PlainText(IEnumerable<TextSegment> segments) => string.Concat(segments.Select(s => s.Text));

    private static void ApplyFormat(string format, List<JsonElement> items, ref int next, Builder output)
    {
        var literal = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%' || i + 1 >= format.Length)
            {
                literal.Append(c);
                continue;
            }

            var spec = format[i + 1];
            if (spec == '%')
            {
                literal.Append('%');
                i++;
                continue;
            }

            if ("sdifoOc".IndexOf(spec) < 0)
            {
                literal.Append(c);
                continue;
            }

            i++;
            if (next >= items.Count)
            {
                // More specifiers than arguments: DevTools prints the specifier as written.
                literal.Append('%').Append(spec);
                continue;
            }

            output.Append(literal.ToString());
            literal.Clear();
            var arg = items[next++];
            switch (spec)
            {
                case 's':
                    output.Append(Describe(arg, top: true));
                    break;
                case 'd':
                case 'i':
                    output.Append(Number(arg) is { } whole ? Math.Truncate(whole).ToString(CultureInfo.InvariantCulture) : "NaN");
                    break;
                case 'f':
                    output.Append(Number(arg) is { } real ? real.ToString(CultureInfo.InvariantCulture) : "NaN");
                    break;
                case 'o':
                case 'O':
                    output.Append(Describe(arg, top: false));
                    break;
                case 'c':
                    output.Css = ParseCss(arg.TryGetProperty("value", out var css) && css.ValueKind == JsonValueKind.String ? css.GetString() ?? "" : "");
                    break;
            }
        }

        output.Append(literal.ToString());
    }

    private static double? Number(JsonElement arg)
    {
        if (arg.TryGetProperty("value", out var value))
        {
            if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean() ? 1 : 0;
        }

        return null;
    }

    // ---- Describing values ----------------------------------------------------------

    /// <summary>A CDP <c>RemoteObject</c> as DevTools prints it in a message: strings bare at the top level, objects as a short preview.</summary>
    public static string Describe(JsonElement value, bool top)
    {
        if (value.ValueKind != JsonValueKind.Object) return "";
        var type = Str(value, "type");
        var subtype = Str(value, "subtype");
        if (value.TryGetProperty("unserializableValue", out var special) && special.ValueKind == JsonValueKind.String) return special.GetString() ?? "";

        switch (type)
        {
            case "string":
                var text = value.TryGetProperty("value", out var s) ? s.GetString() ?? "" : "";
                return top ? text : JsonSerializer.Serialize(text);
            case "number":
            case "boolean":
                return value.TryGetProperty("value", out var raw) ? raw.GetRawText() : Str(value, "description");
            case "undefined":
                return "undefined";
            case "function":
                return FunctionSignature(Str(value, "description"));
            case "object" when subtype == "null":
                return "null";
            case "object" when subtype is "error" or "node" or "regexp" or "date":
                return Str(value, "description");
            case "object":
                if (value.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.Object) return Preview(preview, Str(value, "description"));
                if (value.TryGetProperty("value", out var plain)) return plain.GetRawText();
                return Str(value, "description", "Object");
            default:
                return Str(value, "description", type);
        }
    }

    private static string FunctionSignature(string source)
    {
        var head = source.Split('\n')[0].Trim();
        var brace = head.IndexOf('{');
        if (brace > 0) head = head[..brace].Trim();
        if (head.StartsWith("function", StringComparison.Ordinal)) head = head["function".Length..].Trim();
        return "ƒ " + (head.Length > 0 ? head : "()");
    }

    /// <summary>An object preview: <c>(3) [1, 2, 3]</c> for arrays, <c>{a: 1, b: "x"}</c> for objects, <c>Map(2) {…}</c> for collections.</summary>
    private static string Preview(JsonElement preview, string description)
    {
        var subtype = Str(preview, "subtype");
        var overflow = preview.TryGetProperty("overflow", out var more) && more.ValueKind == JsonValueKind.True;
        var parts = new List<string>();
        if (preview.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Array)
        {
            foreach (var property in properties.EnumerateArray())
            {
                var shown = PreviewValue(property);
                parts.Add(subtype is "array" or "typedarray" ? shown : $"{Str(property, "name")}: {shown}");
            }
        }

        if (preview.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var item = entry.TryGetProperty("value", out var v) ? Preview(v, Str(v, "description")) : "";
                parts.Add(entry.TryGetProperty("key", out var k) ? $"{Preview(k, Str(k, "description"))} => {item}" : item);
            }
        }

        if (overflow) parts.Add("…");
        var body = string.Join(", ", parts);

        if (subtype is "array" or "typedarray")
        {
            var count = description.IndexOf('(') is var open and >= 0 && description.IndexOf(')', open) is var close and > 0
                ? $"({description[(open + 1)..close]}) "
                : "";
            return $"{count}[{body}]";
        }

        if (subtype is "map" or "set" or "weakmap" or "weakset") return $"{description} {{{body}}}";
        if (Str(preview, "type") is "string" or "number" or "boolean") return Str(preview, "description");

        var prefix = description is "" or "Object" ? "" : description + " ";
        return $"{prefix}{{{body}}}";
    }

    private static string PreviewValue(JsonElement property)
    {
        var type = Str(property, "type");
        var value = Str(property, "value");
        if (property.TryGetProperty("valuePreview", out var nested) && nested.ValueKind == JsonValueKind.Object) return Preview(nested, value);
        return type switch
        {
            "string" => JsonSerializer.Serialize(value),
            "function" => "ƒ",
            "object" when Str(property, "subtype") == "null" => "null",
            "accessor" => "(...)",
            _ => value,
        };
    }

    // ---- ANSI and CSS ---------------------------------------------------------------

    /// <summary>Collects styled runs. <see cref="Css"/> is the current <c>%c</c> style; ANSI state is tracked across appends.</summary>
    private sealed class Builder
    {
        private TextStyle _ansi;
        private readonly StringBuilder _run = new();

        public List<TextSegment> Segments { get; } = [];

        public TextStyle Css { get; set; }

        private TextStyle Effective => new(
            _ansi.Foreground ?? Css.Foreground,
            _ansi.Background ?? Css.Background,
            _ansi.AnsiForeground ?? (_ansi.Foreground is null ? Css.AnsiForeground : null),
            _ansi.AnsiBackground ?? (_ansi.Background is null ? Css.AnsiBackground : null),
            _ansi.Bold || Css.Bold,
            _ansi.Italic || Css.Italic,
            _ansi.Underline || Css.Underline);

        public void Append(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c != '\u001b')
                {
                    _run.Append(c);
                    continue;
                }

                Flush();
                if (i + 1 >= text.Length) break;
                var kind = text[i + 1];
                if (kind == '[')
                {
                    // CSI: parameters, then a final byte in @..~. Only SGR ('m') changes the style; cursor moves and the like are dropped.
                    var end = i + 2;
                    while (end < text.Length && (text[end] < '@' || text[end] > '~')) end++;
                    if (end >= text.Length) break;
                    if (text[end] == 'm') ApplySgr(text[(i + 2)..end]);
                    i = end;
                }
                else if (kind == ']')
                {
                    // OSC (titles, hyperlinks): skipped up to BEL or ESC \.
                    var end = i + 2;
                    while (end < text.Length && text[end] != '\u0007' && !(text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\')) end++;
                    i = end < text.Length && text[end] == '\u001b' ? end + 1 : end;
                }
                else
                {
                    i++;
                }
            }

            Flush();
        }

        private void Flush()
        {
            if (_run.Length == 0) return;
            var style = Effective;
            var text = _run.ToString();
            _run.Clear();
            if (Segments.Count > 0 && Segments[^1].Style == style)
            {
                Segments[^1] = Segments[^1] with { Text = Segments[^1].Text + text };
            }
            else
            {
                Segments.Add(new TextSegment(text, style));
            }
        }

        private void ApplySgr(string parameters)
        {
            var codes = parameters.Length == 0 ? [0] : parameters.Split(';', ':').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            for (var i = 0; i < codes.Length; i++)
            {
                var code = codes[i];
                switch (code)
                {
                    case 0: _ansi = default; break;
                    case 1: _ansi = _ansi with { Bold = true }; break;
                    case 3: _ansi = _ansi with { Italic = true }; break;
                    case 4: _ansi = _ansi with { Underline = true }; break;
                    case 22: _ansi = _ansi with { Bold = false }; break;
                    case 23: _ansi = _ansi with { Italic = false }; break;
                    case 24: _ansi = _ansi with { Underline = false }; break;
                    case >= 30 and <= 37: _ansi = _ansi with { AnsiForeground = code - 30, Foreground = null }; break;
                    case >= 90 and <= 97: _ansi = _ansi with { AnsiForeground = code - 90 + 8, Foreground = null }; break;
                    case 39: _ansi = _ansi with { AnsiForeground = null, Foreground = null }; break;
                    case >= 40 and <= 47: _ansi = _ansi with { AnsiBackground = code - 40, Background = null }; break;
                    case >= 100 and <= 107: _ansi = _ansi with { AnsiBackground = code - 100 + 8, Background = null }; break;
                    case 49: _ansi = _ansi with { AnsiBackground = null, Background = null }; break;
                    case 38:
                    case 48:
                        var color = Extended(codes, ref i, out var index);
                        _ansi = code == 38
                            ? _ansi with { Foreground = color, AnsiForeground = index }
                            : _ansi with { Background = color, AnsiBackground = index };
                        break;
                }
            }
        }

        /// <summary>Reads <c>38;5;n</c> (256-colour) or <c>38;2;r;g;b</c> (true colour) after the 38/48.</summary>
        private static Color? Extended(int[] codes, ref int i, out int? index)
        {
            index = null;
            if (i + 1 >= codes.Length) return null;
            if (codes[i + 1] == 5 && i + 2 < codes.Length)
            {
                var n = codes[i + 2];
                i += 2;
                if (n < 16)
                {
                    index = n;
                    return null;
                }

                return Ansi256(n);
            }

            if (codes[i + 1] == 2 && i + 4 < codes.Length)
            {
                var color = Color.FromArgb(255, (byte)codes[i + 2], (byte)codes[i + 3], (byte)codes[i + 4]);
                i += 4;
                return color;
            }

            return null;
        }

        private static Color Ansi256(int n)
        {
            if (n >= 232)
            {
                var level = (byte)(8 + (n - 232) * 10);
                return Color.FromArgb(255, level, level, level);
            }

            n -= 16;
            static byte Step(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return Color.FromArgb(255, Step(n / 36), Step(n / 6 % 6), Step(n % 6));
        }
    }

    /// <summary>The parts of a <c>%c</c> style the console shows: colour, background colour, bold, italic, underline.</summary>
    internal static TextStyle ParseCss(string css)
    {
        var style = default(TextStyle);
        foreach (var declaration in css.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            var name = declaration[..colon].Trim().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Replace("!important", "", StringComparison.OrdinalIgnoreCase).Trim();
            switch (name)
            {
                case "color":
                    if (ParseColor(value) is { } fg) style = style with { Foreground = fg };
                    break;
                case "background":
                case "background-color":
                    if (ParseColor(value) is { } bg) style = style with { Background = bg };
                    break;
                case "font-weight":
                    style = style with { Bold = value is "bold" or "bolder" || (int.TryParse(value, out var weight) && weight >= 600) };
                    break;
                case "font-style":
                    style = style with { Italic = value is "italic" or "oblique" };
                    break;
                case "text-decoration":
                case "text-decoration-line":
                    style = style with { Underline = value.Contains("underline", StringComparison.OrdinalIgnoreCase) };
                    break;
            }
        }

        return style;
    }

    // The CSS named colours, read once from the WinUI colour table (it has the same names). If that table cannot be
    // read, names are simply not recognised; the rest of the formatting still works.
    private static readonly Lazy<Dictionary<string, Color>> NamedColors = new(() =>
    {
        try
        {
            return typeof(Microsoft.UI.Colors)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(Color))
                .ToDictionary(p => p.Name, p => (Color)p.GetValue(null)!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is TargetInvocationException or TypeInitializationException or COMException or InvalidOperationException)
        {
            return new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        }
    });

    /// <summary>A CSS colour: a name, <c>#rgb</c>/<c>#rrggbb</c>(<c>aa</c>), or <c>rgb()</c>/<c>rgba()</c>/<c>hsl()</c>. Anything else (gradients, variables) is null.</summary>
    internal static Color? ParseColor(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return null;

        if (value[0] == '#')
        {
            var hex = value[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(h => new string(h, 2)));
            if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
            return hex.Length == 6
                ? Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                : Color.FromArgb((byte)rgb, (byte)(rgb >> 24), (byte)(rgb >> 16), (byte)(rgb >> 8));
        }

        var open = value.IndexOf('(');
        if (open > 0 && value.EndsWith(')'))
        {
            var function = value[..open].Trim().ToLowerInvariant();
            var args = value[(open + 1)..^1].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (args.Length < 3) return null;
            var alpha = args.Length > 3 ? Channel(args[3], 1) : 1;
            if (function is "rgb" or "rgba")
            {
                return Color.FromArgb(ToByte(alpha * 255), ToByte(Channel(args[0], 255)), ToByte(Channel(args[1], 255)), ToByte(Channel(args[2], 255)));
            }

            if (function is "hsl" or "hsla")
            {
                var h = double.TryParse(args[0].Replace("deg", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var hue) ? hue : 0;
                return FromHsl(h, Channel(args[1], 1), Channel(args[2], 1), alpha);
            }

            return null;
        }

        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;
        return NamedColors.Value.TryGetValue(value, out var named) ? named : null;
    }

    /// <summary>A colour channel: a percentage of <paramref name="scale"/> or a plain number.</summary>
    private static double Channel(string text, double scale)
    {
        if (text.EndsWith('%') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) return percent / 100 * scale;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static Color FromHsl(double h, double s, double l, double alpha)
    {
        h = ((h % 360) + 360) % 360 / 360;
        double Hue(double t)
        {
            t = (t + 1) % 1;
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        return Color.FromArgb(ToByte(alpha * 255), ToByte(Hue(h + 1.0 / 3) * 255), ToByte(Hue(h) * 255), ToByte(Hue(h - 1.0 / 3) * 255));
    }

    internal static string Str(JsonElement element, string name, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
}
