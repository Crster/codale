using System.Globalization;
using System.Text.Json;

namespace Codale.Core.Syntax;

public sealed record ThemeCatalogEntry(string Id, string Name, string Type, string Url, string Source)
{
    public string License { get; init; } = "";
    public string LicenseUrl { get; init; } = "";
}

/// <summary>Resolves TextMate scope rules and VS Code editor surface colors.</summary>
public sealed class SyntaxTheme
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required IReadOnlyDictionary<CodeTokenKind, uint> Colors { get; init; }
    public required IReadOnlyDictionary<string, uint> EditorColors { get; init; }
    public SyntaxTokenStyle Defaults { get; private init; }
    private sealed record Rule(ScopeSelector Selector, uint? Foreground, uint? Background, SyntaxFontStyle? Style);
    private Rule[] _rules = [];
    private readonly Dictionary<string, SyntaxTokenStyle> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private static readonly string[] Scopes =
    ["", "keyword", "entity.name.type", "string", "constant.numeric", "comment", "keyword.operator",
        "keyword.control.directive", "entity.name.tag", "entity.other.attribute-name", "variable.other.property",
        "constant", "variable", "markup.heading"];

    public SyntaxTokenStyle Resolve(string? scopeStack)
    {
        if (string.IsNullOrEmpty(scopeStack)) return Defaults;
        lock (_gate)
        {
            if (_cache.TryGetValue(scopeStack, out var cached)) return cached;
            var scopes = scopeStack.Split('\u001f');
            uint? foreground = Defaults.Foreground, background = null;
            var style = Defaults.FontStyle;
            int[]? foregroundRank = null, backgroundRank = null, styleRank = null;
            foreach (var rule in _rules)
            {
                var rank = rule.Selector.Match(scopes);
                if (rank is null) continue;
                if (rule.Foreground is { } fg && (foregroundRank is null || ScopeSelector.Compare(rank, foregroundRank) >= 0))
                    (foreground, foregroundRank) = (fg, rank);
                if (rule.Background is { } bg && (backgroundRank is null || ScopeSelector.Compare(rank, backgroundRank) >= 0))
                    (background, backgroundRank) = (bg, rank);
                if (rule.Style is { } font && (styleRank is null || ScopeSelector.Compare(rank, styleRank) >= 0))
                    (style, styleRank) = (font, rank);
            }
            var result = new SyntaxTokenStyle(foreground, background, style);
            if (_cache.Count >= 8192) _cache.Clear();
            _cache[scopeStack] = result;
            return result;
        }
    }

    public static SyntaxTheme Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type is not ("dark" or "light")) throw new InvalidDataException("A theme must declare light or dark type.");
            if (root.TryGetProperty("include", out _)) throw new InvalidDataException("Theme includes must be flattened before importing.");
            var editor = new Dictionary<string, uint>(StringComparer.Ordinal);
            if (root.TryGetProperty("colors", out var gui))
                foreach (var property in gui.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String && TryColor(property.Value.GetString(), out var color)) editor[property.Name] = color;
            var rules = new List<Rule>();
            uint? defaultForeground = null, defaultBackground = null;
            var defaultStyle = SyntaxFontStyle.None;
            var hasSettings = false;
            foreach (var property in new[] { "settings", "tokenColors" })
            {
                if (!root.TryGetProperty(property, out var entries)) continue;
                if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("A theme needs an array of token settings.");
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("settings", out var settings)) continue;
                    uint? foreground = settings.TryGetProperty("foreground", out var fg) && TryColor(fg.GetString(), out var f) ? f : null;
                    uint? background = settings.TryGetProperty("background", out var bg) && TryColor(bg.GetString(), out var b) ? b : null;
                    SyntaxFontStyle? style = null;
                    if (settings.TryGetProperty("fontStyle", out var font))
                    {
                        style = SyntaxFontStyle.None; // Empty string explicitly clears inherited styles.
                        foreach (var part in (font.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            style |= part switch { "italic" => SyntaxFontStyle.Italic, "bold" => SyntaxFontStyle.Bold,
                                "underline" => SyntaxFontStyle.Underline, "strikethrough" => SyntaxFontStyle.Strikethrough, _ => SyntaxFontStyle.None };
                    }
                    if (foreground is null && background is null && style is null) continue;
                    hasSettings = true;
                    var selectors = !entry.TryGetProperty("scope", out var scope) ? [""] : scope.ValueKind == JsonValueKind.String ? [scope.GetString() ?? ""]
                        : scope.ValueKind == JsonValueKind.Array ? scope.EnumerateArray().Select(s => s.GetString() ?? "").ToArray() : [""];
                    foreach (var selector in selectors)
                    {
                        if (string.IsNullOrWhiteSpace(selector))
                        {
                            defaultForeground = foreground ?? defaultForeground;
                            defaultBackground = background ?? defaultBackground;
                            defaultStyle = style ?? defaultStyle;
                        }
                        else foreach (var compiled in ScopeSelector.Parse(selector)) rules.Add(new Rule(compiled, foreground, background, style));
                    }
                }
            }
            if (!hasSettings && editor.Count == 0) throw new InvalidDataException("The theme has no usable colors or token styles.");
            // Workbench editor colors take precedence over legacy global token settings.
            defaultForeground = editor.TryGetValue("editor.foreground", out var editorFg) ? editorFg : defaultForeground;
            defaultBackground = editor.TryGetValue("editor.background", out var editorBg) ? editorBg : defaultBackground;
            defaultForeground ??= type == "dark" ? 0xffd4d4d4u : 0xff333333u;
            defaultBackground ??= type == "dark" ? 0xff1e1e1eu : 0xffffffffu;
            editor["editor.foreground"] = defaultForeground.Value;
            editor["editor.background"] = defaultBackground.Value;
            var colors = new Dictionary<CodeTokenKind, uint>();
            var theme = new SyntaxTheme { Name = root.GetProperty("name").GetString() ?? "Theme", Type = type, Colors = colors,
                EditorColors = editor, Defaults = new(defaultForeground, defaultBackground, defaultStyle), _rules = [.. rules] };
            for (var i = 1; i < Scopes.Length; i++)
                if (theme.Resolve(Scopes[i]).Foreground is { } color) colors[(CodeTokenKind)i] = color;
            return theme;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The file is not a usable TextMate JSON theme.", ex);
        }
    }

    public static bool TryColor(string? value, out uint color)
    {
        color = 0;
        if (value is null || !value.StartsWith('#')) return false;
        var hex = value[1..];
        if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba)) return false;
        color = hex.Length == 8 ? (rgba >> 8) | ((rgba & 0xff) << 24) : rgba | 0xff000000;
        return true;
    }
}

public static class SyntaxThemeCatalog
{
    public static IReadOnlyList<ThemeCatalogEntry> Entries { get; } = Load();

    private static IReadOnlyList<ThemeCatalogEntry> Load()
    {
        var assembly = typeof(SyntaxThemeCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream("Codale.Core.Syntax.Bundled.themes.json")!;
        return JsonSerializer.Deserialize<List<ThemeCatalogEntry>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    public static async Task InstallAsync(ThemeCatalogEntry entry, SyntaxStore store, CancellationToken ct = default)
    {
        var json = await new SyntaxCatalogClient().DownloadAsync(entry.Url, entry.Name, ct).ConfigureAwait(false);
        var theme = SyntaxTheme.Parse(json);
        if (theme.Type != entry.Type) throw new InvalidDataException("The downloaded theme type does not match the catalog.");
        var path = PathFor(store, entry.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, json, ct).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public static SyntaxTheme? Read(SyntaxStore store, string id)
    {
        if (id.Length == 0) return null;
        try { return SyntaxTheme.Parse(File.ReadAllText(PathFor(store, id))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    public static string PathFor(SyntaxStore store, string id) => Path.Combine(store.Root, "themes", SyntaxStore.Slug(id) + ".json");
}
