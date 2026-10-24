using System.Text.RegularExpressions;

using Codale.Core.Syntax;

namespace Codale.App.Controls;

/// <summary>
/// Completion vocabularies for languages the keyword tables in CodeTokenizer do not cover:
/// stylesheets, JSON, batch files, and the standard names of Java, Kotlin, PHP and Rust.
/// </summary>
public sealed partial class CodeEditor
{
    [GeneratedRegex("\"([^\"\\\\\\r\\n]{1,64})\"\\s*:")]
    private static partial Regex JsonKeyRegex();

    [GeneratedRegex(@"(?im)^\s*set\s+(?:/[ap]\s+)?""?([A-Za-z_][\w.-]*)=")]
    private static partial Regex BatchSetRegex();

    [GeneratedRegex(@"--?[A-Za-z_][\w-]*")]
    private static partial Regex CssWordRegex();

    [GeneratedRegex(@"\$?[A-Za-z_]\w*")]
    private static partial Regex PhpWordRegex();

    private bool IsCss => _languageId is "css" or "scss" or "less" || (_languageId is null && _syntax == CodeSyntax.CSS);

    private bool IsJson => _languageId is "json" or "jsonc" || (_languageId is null && _syntax == CodeSyntax.Json);

    private bool IsBatch => _languageId is "bat" || (_languageId is null && _syntax == CodeSyntax.Batch);

    private Regex? DocWordRegex() => IsCss ? CssWordRegex() : _languageId is "php" ? PhpWordRegex() : null;

    /// <summary>Characters, beyond letters and digits, that belong inside a name in this language.</summary>
    private bool IsExtraNameChar(char ch) => IsMarkup ? ch is '-' or ':' or '.' : IsCss ? ch == '-' : _languageId is "php" && ch == '$';

    /// <summary>Fills the per-language name sets (_docAttrs doubles as JSON keys and batch variables).</summary>
    private void CollectLanguageNames(string text)
    {
        if (IsJson)
        {
            foreach (Match m in JsonKeyRegex().Matches(text))
            {
                _docAttrs.Add(m.Groups[1].Value);
            }
        }
        else if (IsBatch)
        {
            foreach (Match m in BatchSetRegex().Matches(text))
            {
                _docAttrs.Add(m.Groups[1].Value);
            }
        }
    }

    /// <summary>
    /// Candidates for the languages with their own context rules, or null to fall through to the
    /// general list. <paramref name="start"/> is where the word being typed begins on <paramref name="line"/>.
    /// </summary>
    private IEnumerable<(string Text, CompletionKind Kind)>? NativeCandidates(string line, int start)
    {
        if (IsJson)
        {
            var before = line.AsSpan(0, start);
            var afterColon = before.LastIndexOf(':') > Math.Max(before.LastIndexOf(','), before.LastIndexOf('{'));
            return afterColon
                ? new[] { "true", "false", "null" }.Select(w => (w, CompletionKind.Keyword))
                : _docAttrs.Select(k => (k, CompletionKind.Property));
        }

        if (IsBatch)
        {
            var percent = start > 0 && line[start - 1] == '%';
            var vars = _docAttrs.Select(v => (v, CompletionKind.Variable));
            return percent
                ? vars.Concat(BatchEnvironment.Select(v => (v, CompletionKind.Variable)))
                : CodeTokenizer.BatchKeywordsOf().Concat(BatchCommands).Select(w => (w, CompletionKind.Keyword)).Concat(vars);
        }

        if (IsCss)
        {
            if (CssDepth(start) <= 0)
            {
                return null; // selectors: classes, ids and tags come from the file's own words
            }

            var before = line.AsSpan(0, start);
            var inValue = before.LastIndexOf(':') > before.LastIndexOf(';') && before.LastIndexOf(':') > before.LastIndexOf('{');
            return inValue
                ? CssValues.Select(w => (w, CompletionKind.Keyword))
                : CssProperties.Select(w => (w, CompletionKind.Property));
        }

        return null;
    }

    /// <summary>Open braces before the caret: zero means selector position, above zero a declaration block.</summary>
    private int CssDepth(int start)
    {
        var depth = 0;
        var first = Math.Max(0, _caret.Line - 400);
        for (var l = first; l <= _caret.Line && l < _doc.LineCount; l++)
        {
            var text = _doc.GetLine(l);
            var end = l == _caret.Line ? Math.Min(start, text.Length) : text.Length;
            for (var i = 0; i < end; i++)
            {
                if (text[i] == '{')
                {
                    depth++;
                }
                else if (text[i] == '}')
                {
                    depth--;
                }
            }
        }

        return depth;
    }

    private static readonly string[] BatchCommands =
    [
        "attrib", "choice", "curl", "dir", "enabledelayedexpansion", "ipconfig", "mklink", "net", "path", "ping", "powershell",
        "reg", "robocopy", "sc", "taskkill", "timeout", "where", "ver", "vol", "assoc", "label", "more", "sort", "tree",
    ];

    private static readonly string[] BatchEnvironment =
    [
        "APPDATA", "COMPUTERNAME", "CD", "DATE", "ERRORLEVEL", "HOMEPATH", "LOCALAPPDATA", "PATH", "PROGRAMFILES", "RANDOM",
        "SYSTEMROOT", "TEMP", "TIME", "USERNAME", "USERPROFILE", "WINDIR", "~dp0", "~f1", "~n0",
    ];

    private static readonly string[] CssProperties =
    [
        "align-content", "align-items", "align-self", "animation", "animation-delay", "animation-duration", "animation-name", "aspect-ratio",
        "backdrop-filter", "background", "background-attachment", "background-color", "background-image", "background-position", "background-repeat",
        "background-size", "border", "border-bottom", "border-collapse", "border-color", "border-left", "border-radius", "border-right", "border-style",
        "border-top", "border-width", "bottom", "box-shadow", "box-sizing", "clip-path", "color", "column-gap", "columns", "content", "cursor", "display",
        "fill", "filter", "flex", "flex-basis", "flex-direction", "flex-flow", "flex-grow", "flex-shrink", "flex-wrap", "float", "font", "font-family",
        "font-size", "font-style", "font-weight", "gap", "grid", "grid-area", "grid-auto-flow", "grid-column", "grid-row", "grid-template-areas",
        "grid-template-columns", "grid-template-rows", "height", "inset", "justify-content", "justify-items", "justify-self", "left", "letter-spacing",
        "line-height", "list-style", "margin", "margin-bottom", "margin-left", "margin-right", "margin-top", "max-height", "max-width", "min-height",
        "min-width", "object-fit", "opacity", "order", "outline", "overflow", "overflow-x", "overflow-y", "padding", "padding-bottom", "padding-left",
        "padding-right", "padding-top", "place-content", "place-items", "pointer-events", "position", "resize", "right", "row-gap", "scroll-behavior",
        "stroke", "table-layout", "text-align", "text-decoration", "text-overflow", "text-shadow", "text-transform", "top", "transform", "transform-origin",
        "transition", "transition-duration", "transition-property", "user-select", "vertical-align", "visibility", "white-space", "width", "word-break",
        "z-index",
    ];

    private static readonly string[] CssValues =
    [
        "absolute", "auto", "baseline", "block", "bold", "border-box", "both", "center", "column", "contain", "content-box", "cover", "ease", "ease-in",
        "ease-in-out", "ease-out", "fixed", "flex", "flex-end", "flex-start", "grid", "hidden", "inherit", "initial", "inline", "inline-block",
        "inline-flex", "italic", "linear", "none", "normal", "nowrap", "pointer", "relative", "repeat", "row", "scroll", "solid", "space-around",
        "space-between", "space-evenly", "static", "sticky", "stretch", "sans-serif", "monospace", "serif", "transparent", "underline", "unset",
        "visible", "wrap", "calc", "clamp", "min", "max", "rgb", "rgba", "hsl", "var", "url", "linear-gradient", "radial-gradient", "!important",
    ];

    /// <summary>Standard-library names the keyword lists lack, added per language.</summary>
    private static readonly Dictionary<string, string[]> StandardNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["java"] = ["String", "Integer", "Long", "Double", "Boolean", "Object", "List", "ArrayList", "Map", "HashMap", "Set", "HashSet", "Optional", "Stream",
            "Collections", "Arrays", "System", "Math", "StringBuilder", "Exception", "RuntimeException", "IOException", "Override", "FunctionalInterface",
            "Thread", "Runnable", "Iterator", "Comparable", "LocalDate", "Path", "Files", "record", "sealed", "permits", "yield"],
        ["kotlin"] = ["listOf", "mutableListOf", "mapOf", "mutableMapOf", "setOf", "arrayOf", "println", "print", "require", "check", "error", "lazy", "also",
            "apply", "let", "run", "with", "takeIf", "map", "filter", "forEach", "first", "last", "joinToString", "List", "Map", "Set", "MutableList",
            "Pair", "Sequence", "Nothing", "Exception", "launch", "async", "Flow", "coroutineScope", "init", "constructor", "inline", "reified", "crossinline"],
        ["php"] = ["array_filter", "array_key_exists", "array_keys", "array_map", "array_merge", "array_push", "array_values", "count", "empty", "explode", "implode",
            "in_array", "is_array", "is_null", "is_string", "json_decode", "json_encode", "preg_match", "preg_replace", "print_r", "sprintf", "str_contains",
            "str_replace", "strlen", "strpos", "substr", "trim", "var_dump", "__construct", "__toString", "$this", "readonly", "enum", "match", "fn", "namespace",
            "Exception", "DateTime", "PDO", "Closure", "Countable", "Iterator", "JsonSerializable"],
        ["rust"] = ["println!", "print!", "format!", "vec!", "panic!", "assert!", "assert_eq!", "todo!", "unimplemented!", "matches!", "write!", "writeln!", "dbg!",
            "derive", "Debug", "Clone", "Copy", "Default", "PartialEq", "Eq", "Hash", "Display", "From", "Into", "Iterator", "Box", "Rc", "Arc", "RefCell", "Mutex",
            "HashMap", "HashSet", "BTreeMap", "VecDeque", "Path", "PathBuf", "unwrap", "expect", "unwrap_or", "unwrap_or_else", "map_err", "ok_or", "collect",
            "iter", "into_iter", "clone", "to_string", "as_str", "is_some", "is_none", "lifetime", "macro_rules"],
    };
}
