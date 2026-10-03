using Codale.Core.Syntax;

namespace Codale.App.Controls;

/// <summary>
/// The languages the editor can colour. Chosen per file extension in EditorTab;
/// everything not listed renders as plain text.
/// </summary>
public enum CodeSyntax
{
    None,
    CSharp,
    Json,
    XML,
    Cpp,
    Python,
    Javascript,
    Html,
    CSS,
    SQL,
    Markdown,
    TOML,
    Batch,
}

/// <summary>
/// State carried from one line to the next: an open block comment, an unterminated
/// tag, a code fence. Editors colour a window of lines at a time, so the state a
/// line starts in has to be reproducible from the cache rather than re-derived from
/// the top of the file on every frame.
/// </summary>
public struct CodeTokenState
{
    /// <summary>Meaning depends on the language; zero is always "nothing open".</summary>
    public int Mode;

    /// <summary>The grammar's rule stack when a TextMate grammar did the colouring; null otherwise.</summary>
    public object? Stack;

    /// <summary>True when two states would colour the following lines identically.</summary>
    public readonly bool SameAs(CodeTokenState other) => Mode == other.Mode && Equals(Stack, other.Stack);
}

/// <summary>
/// Maps a grammar language id to the hand-written scanner that stands in when the grammar cannot
/// load, so a broken or missing grammar degrades to the old colouring rather than to none.
/// </summary>
public static class LegacySyntax
{
    public static CodeSyntax For(string? languageId) => languageId switch
    {
        "csharp" => CodeSyntax.CSharp,
        "json" or "jsonc" => CodeSyntax.Json,
        "xml" or "xsl" => CodeSyntax.XML,
        "c" or "cpp" => CodeSyntax.Cpp,
        "python" => CodeSyntax.Python,
        "javascript" or "typescript" or "javascriptreact" or "typescriptreact" => CodeSyntax.Javascript,
        "html" => CodeSyntax.Html,
        "css" or "scss" or "less" => CodeSyntax.CSS,
        "sql" => CodeSyntax.SQL,
        "markdown" => CodeSyntax.Markdown,
        "toml" => CodeSyntax.TOML,
        "powershell" or "bat" => CodeSyntax.Batch,
        _ => CodeSyntax.None,
    };
}

/// <summary>
/// The colours tokens render in: a One Dark-ish set, picked medium so it holds on
/// the editor in both themes - the same palette the chat tool cards use.
/// </summary>
public static class CodePalette
{
    public static Windows.UI.Color Get(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Keyword => Windows.UI.Color.FromArgb(0xFF, 0xC6, 0x78, 0xDD),
        CodeTokenKind.Type => Windows.UI.Color.FromArgb(0xFF, 0xE5, 0xC0, 0x7B),
        CodeTokenKind.String => Windows.UI.Color.FromArgb(0xFF, 0x98, 0xC3, 0x79),
        CodeTokenKind.Number => Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x9A, 0x66),
        CodeTokenKind.Comment => Windows.UI.Color.FromArgb(0xFF, 0x7F, 0x8C, 0x9B),
        CodeTokenKind.Operator => Windows.UI.Color.FromArgb(0xFF, 0x56, 0xB6, 0xC2),
        CodeTokenKind.Preprocessor => Windows.UI.Color.FromArgb(0xFF, 0xE0, 0x6C, 0x75),
        CodeTokenKind.Tag => Windows.UI.Color.FromArgb(0xFF, 0xE0, 0x6C, 0x75),
        CodeTokenKind.Attribute => Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x9A, 0x66),
        CodeTokenKind.Property => Windows.UI.Color.FromArgb(0xFF, 0x61, 0xAF, 0xEF),
        CodeTokenKind.Constant => Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x9A, 0x66),
        CodeTokenKind.Variable => Windows.UI.Color.FromArgb(0xFF, 0xE0, 0x6C, 0x75),
        CodeTokenKind.Heading => Windows.UI.Color.FromArgb(0xFF, 0x61, 0xAF, 0xEF),
        _ => Windows.UI.Color.FromArgb(0, 0, 0, 0), // plain text keeps the theme foreground
    };
}

/// <summary>
/// Hand-written tokenizers, one per language family. Not parsers: they only have to
/// be right often enough that the colouring reads, and they must never throw on
/// input they do not understand. The generic scanner covers the C-family and the
/// other brace languages; JSON, XML/HTML, Markdown and Batch get their own small
/// loops because their shape does not fit it.
/// </summary>
public static class CodeTokenizer
{
    /// <summary>
    /// Tokenizes one line, carrying multi-line state through. <paramref name="tokens"/>
    /// is cleared and filled; the returned state feeds the next line.
    /// </summary>
    public static CodeTokenState TokenizeLine(string line, string? languageId, CodeSyntax fallback, CodeTokenState stateIn, List<CodeToken> tokens)
    {
        if (languageId is not null
            && SyntaxService.Tokenizer.TryTokenizeLine(languageId, line, stateIn.Stack, tokens, out var stack))
        {
            return new CodeTokenState { Stack = stack };
        }

        return TokenizeLine(line, fallback, stateIn.Stack is null ? stateIn : default, tokens);
    }

    public static CodeTokenState TokenizeLine(string line, CodeSyntax syntax, CodeTokenState stateIn, List<CodeToken> tokens)
    {
        tokens.Clear();
        try
        {
            return syntax switch
            {
                CodeSyntax.Json => Json(line, tokens),
                CodeSyntax.XML or CodeSyntax.Html => Markup(line, tokens, stateIn),
                CodeSyntax.Markdown => Markdown(line, tokens, stateIn),
                CodeSyntax.Batch => Batch(line, tokens),
                _ => Generic(line, GetDef(syntax), stateIn, tokens),
            };
        }
        catch
        {
            // A tokenizer bug must never take the editor down; plain text is an acceptable fallback.
            CrashLog.Warn("editor", $"tokenizer failed for {syntax}; line rendered plain");
            tokens.Clear();
            return default;
        }
    }

    // ------------------------------------------------------------------ generic

    private sealed class LanguageDef
    {
        public string[] LineComments = [];
        public (string Start, string End)? BlockComment;
        public char[] StringChars = ['"'];
        public bool CharLiterals;
        public string[]? TripleStrings;
        public char? PreprocChar;
        public HashSet<string> Keywords = new(StringComparer.Ordinal);
        public HashSet<string> Types = new(StringComparer.Ordinal);
        public HashSet<string> Constants = new(StringComparer.Ordinal);
        public bool IgnoreCase;
        public bool CharTokens;
    }

    private static readonly Dictionary<CodeSyntax, LanguageDef> Defs = new()
    {
        [CodeSyntax.CSharp] = new LanguageDef
        {
            LineComments = ["//"],
            BlockComment = ("/*", "*/"),
            CharLiterals = true,
            PreprocChar = '#',
            Types = new HashSet<string>(StringComparer.Ordinal) { "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte", "float", "double", "decimal",
                 "bool", "char", "string", "object", "void", "var", "dynamic", "nint", "nuint" },
            Keywords = new HashSet<string>(StringComparer.Ordinal) { "abstract", "as", "async", "await", "base", "break", "case", "catch", "checked", "class", "const",
                 "continue", "default", "delegate", "do", "else", "enum", "event", "explicit", "extern", "false",
                 "finally", "fixed", "for", "foreach", "get", "goto", "if", "implicit", "in", "init", "interface",
                 "internal", "is", "lock", "namespace", "new", "not", "null", "operator", "or", "out", "override",
                 "params", "private", "protected", "public", "readonly", "record", "ref", "return", "sealed",
                 "set", "sizeof", "stackalloc", "static", "struct", "switch", "this", "throw", "true", "try",
                 "typeof", "unchecked", "unsafe", "using", "virtual", "volatile", "when", "where", "while", "with", "yield" },
            Constants = new HashSet<string>(StringComparer.Ordinal) { "true", "false", "null" },
        },
        [CodeSyntax.Cpp] = new LanguageDef
        {
            LineComments = ["//"],
            BlockComment = ("/*", "*/"),
            CharLiterals = true,
            PreprocChar = '#',
            Types = new HashSet<string>(StringComparer.Ordinal) { "auto", "bool", "char", "char8_t", "char16_t", "char32_t", "double", "float", "int", "long",
                 "short", "signed", "unsigned", "void", "wchar_t", "size_t", "uint8_t", "uint16_t", "uint32_t",
                 "uint64_t", "int8_t", "int16_t", "int32_t", "int64_t" },
            Keywords = new HashSet<string>(StringComparer.Ordinal) { "alignas", "alignof", "and", "asm", "break", "case", "catch", "class", "co_await", "co_return",
                 "co_yield", "const", "consteval", "constexpr", "constinit", "const_cast", "continue", "decltype",
                 "default", "delete", "do", "dynamic_cast", "else", "enum", "explicit", "export", "extern",
                 "for", "friend", "goto", "if", "inline", "mutable", "namespace", "new", "noexcept", "not",
                 "operator", "or", "private", "protected", "public", "register", "reinterpret_cast", "requires",
                 "return", "sizeof", "static", "static_cast", "struct", "switch", "template", "this", "thread_local",
                 "throw", "try", "typedef", "typeid", "typename", "union", "using", "virtual", "volatile", "while" },
            Constants = new HashSet<string>(StringComparer.Ordinal) { "true", "false", "nullptr", "NULL" },
        },
        [CodeSyntax.Python] = new LanguageDef
        {
            LineComments = ["#"],
            TripleStrings = ["\"\"\"", "'''"],
            StringChars = ['"', '\''],
            Types = new HashSet<string>(StringComparer.Ordinal) { "int", "float", "str", "bool", "bytes", "list", "dict", "set", "tuple", "object" },
            Keywords = new HashSet<string>(StringComparer.Ordinal) { "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif",
                 "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda",
                 "match", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield" },
            Constants = new HashSet<string>(StringComparer.Ordinal) { "True", "False", "None", "self", "cls", "__name__", "__main__" },
        },
        [CodeSyntax.Javascript] = new LanguageDef
        {
            LineComments = ["//"],
            BlockComment = ("/*", "*/"),
            CharLiterals = true,
            StringChars = ['"', '\'', '`'],
            Types = new HashSet<string>(StringComparer.Ordinal) { "number", "string", "boolean", "any", "unknown", "never", "void", "object", "symbol", "bigint" },
            Keywords = new HashSet<string>(StringComparer.Ordinal) { "abstract", "as", "async", "await", "break", "case", "catch", "class", "const", "continue",
                 "debugger", "declare", "default", "delete", "do", "else", "enum", "export", "extends", "finally",
                 "for", "from", "function", "get", "if", "implements", "import", "in", "infer", "instanceof",
                 "interface", "is", "keyof", "let", "namespace", "new", "of", "private", "protected", "public",
                 "readonly", "return", "satisfies", "set", "static", "super", "switch", "this", "throw", "try",
                 "type", "typeof", "var", "void", "while", "with", "yield" },
            Constants = new HashSet<string>(StringComparer.Ordinal) { "true", "false", "null", "undefined", "NaN", "Infinity" },
        },
        [CodeSyntax.CSS] = new LanguageDef
        {
            LineComments = ["//"],
            BlockComment = ("/*", "*/"),
            PreprocChar = '@',
            StringChars = ['"', '\''],
        },
        [CodeSyntax.SQL] = new LanguageDef
        {
            LineComments = ["--"],
            BlockComment = ("/*", "*/"),
            StringChars = ['"', '\''],
            IgnoreCase = true,
            CharTokens = true,
            Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "add", "all", "alter", "and", "as", "asc", "between", "by", "case", "check", "column", "commit",
                 "constraint", "create", "cross", "database", "default", "delete", "desc", "distinct", "drop",
                 "else", "end", "exists", "foreign", "from", "full", "group", "having", "in", "index", "inner",
                 "insert", "into", "is", "join", "key", "left", "like", "limit", "not", "null", "offset", "on",
                 "or", "order", "outer", "primary", "references", "right", "rollback", "select", "set", "table",
                 "then", "top", "truncate", "union", "unique", "update", "values", "view", "when", "where", "with" },
            Constants = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "true", "false", "null", "current_date", "current_time", "current_timestamp" },
        },
        [CodeSyntax.TOML] = new LanguageDef
        {
            LineComments = ["#"],
            StringChars = ['"', '\''],
            Constants = new HashSet<string>(StringComparer.Ordinal) { "true", "false", "inf", "nan" },
        },
    };

    private static LanguageDef GetDef(CodeSyntax syntax) =>
        Defs.TryGetValue(syntax, out var def) ? def : new LanguageDef { StringChars = ['"'] };

    private const int ModeInBlockComment = 1;
    private const int ModeInTripleString = 2;

    private static CodeTokenState Generic(string line, LanguageDef def, CodeTokenState stateIn, List<CodeToken> tokens)
    {
        var state = stateIn.Mode;
        var i = 0;

        if (state == ModeInBlockComment && def.BlockComment is { } comment)
        {
            var end = line.IndexOf(comment.End, StringComparison.Ordinal);
            if (end < 0)
            {
                tokens.Add(new(0, line.Length, CodeTokenKind.Comment));
                return stateIn;
            }

            tokens.Add(new(0, end + comment.End.Length, CodeTokenKind.Comment));
            i = end + comment.End.Length;
            state = 0;
        }
        else if (state == ModeInTripleString && def.TripleStrings is { } triples)
        {
            var end = FindAny(line, triples, 0, out var closer);
            if (end < 0)
            {
                tokens.Add(new(0, line.Length, CodeTokenKind.String));
                return stateIn;
            }

            tokens.Add(new(0, end + closer.Length, CodeTokenKind.String));
            i = end + closer.Length;
            state = 0;
        }

        while (i < line.Length)
        {
            var ch = line[i];

            if (ch is ' ' or '\t')
            {
                i++;
                continue;
            }

            // Line comments.
            var isComment = false;
            for (var c = 0; c < def.LineComments.Length; c++)
            {
                if (line.AsSpan(i).StartsWith(def.LineComments[c], StringComparison.Ordinal))
                {
                    isComment = true;
                    break;
                }
            }

            if (isComment)
            {
                tokens.Add(new(i, line.Length - i, CodeTokenKind.Comment));
                break;
            }

            if (def.BlockComment is { } block && line.AsSpan(i).StartsWith(block.Start, StringComparison.Ordinal))
            {
                var end = line.IndexOf(block.End, i + block.Start.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(new(i, line.Length - i, CodeTokenKind.Comment));
                    state = ModeInBlockComment;
                    break;
                }

                var length = end + block.End.Length - i;
                tokens.Add(new(i, length, CodeTokenKind.Comment));
                i += length;
                continue;
            }

            // Triple-quoted strings (Python) can span lines.
            if (def.TripleStrings is not null && MatchAny(line, i, def.TripleStrings) is { } opener)
            {
                var end = FindAny(line, def.TripleStrings, i + opener.Length, out var closer);
                if (end < 0)
                {
                    tokens.Add(new(i, line.Length - i, CodeTokenKind.String));
                    state = ModeInTripleString;
                    break;
                }

                var length = end + closer.Length - i;
                tokens.Add(new(i, length, CodeTokenKind.String));
                i += length;
                continue;
            }

            if (def.StringChars.Contains(ch))
            {
                var close = ScanQuoted(line, i, ch);
                var kind = CodeTokenKind.String;

                // A JSON-style key: a quoted span a colon follows.
                if (close < line.Length && SyntaxIsKeyed(def))
                {
                    var j = close + 1;
                    while (j < line.Length && line[j] == ' ')
                    {
                        j++;
                    }

                    if (j < line.Length && line[j] == ':')
                    {
                        kind = CodeTokenKind.Property;
                    }
                }

                tokens.Add(new(i, close - i + 1, kind));
                i = close + 1;
                continue;
            }

            if (char.IsDigit(ch) || (ch == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
            {
                var start = i;
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '.' or '_'
                       || (line[i] is '+' or '-' && i > start && (line[i - 1] is 'e' or 'E' or 'p' or 'P'))))
                {
                    i++;
                }

                tokens.Add(new(start, i - start, CodeTokenKind.Number));
                continue;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                {
                    i++;
                }

                var word = line[start..i];
                var compare = def.IgnoreCase ? word.ToLowerInvariant() : word;
                var kind = def.Keywords.Contains(compare) ? CodeTokenKind.Keyword
                    : def.Types.Contains(compare) ? CodeTokenKind.Type
                    : def.Constants.Contains(compare) ? CodeTokenKind.Constant
                    : CodeTokenKind.Plain;

                if (kind != CodeTokenKind.Plain)
                {
                    tokens.Add(new(start, i - start, kind));
                }

                continue;
            }

            if (def.PreprocChar == ch)
            {
                // '#' directives and '@' decorators/preprocessors run to the end of line.
                tokens.Add(new(i, line.Length - i, CodeTokenKind.Preprocessor));
                break;
            }

            if (def.CharLiterals && ch == '\'')
            {
                var close = ScanQuoted(line, i, '\'');
                tokens.Add(new(i, close - i + 1, CodeTokenKind.String));
                i = close + 1;
                continue;
            }

            // Anything else is punctuation; run same-neighbour punctuation into one token.
            var opStart = i;
            while (i < line.Length && IsOperatorChar(line[i]) && !(def.StringChars.Contains(line[i])))
            {
                i++;
            }

            if (i == opStart)
            {
                i++;
            }

            tokens.Add(new(opStart, i - opStart, CodeTokenKind.Operator));
        }

        return new CodeTokenState { Mode = state };
    }

    private static bool SyntaxIsKeyed(LanguageDef def) => !def.CharTokens || def.IgnoreCase;

    private static int ScanQuoted(string line, int open, char quote)
    {
        for (var i = open + 1; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++;
                continue;
            }

            if (line[i] == quote)
            {
                return i;
            }
        }

        return Math.Max(open, line.Length - 1);
    }

    private static bool IsOperatorChar(char ch) =>
        "+-*/%=<>!&|^~?:;,()[]{}.".Contains(ch);

    private static string? MatchAny(string line, int at, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (line.AsSpan(at).StartsWith(candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static int FindAny(string line, string[] candidates, int from, out string found)
    {
        var best = -1;
        found = "";
        foreach (var candidate in candidates)
        {
            var at = line.IndexOf(candidate, from, StringComparison.Ordinal);
            if (at >= 0 && (best < 0 || at < best))
            {
                best = at;
                found = candidate;
            }
        }

        return best;
    }

    // ------------------------------------------------------------------ markup (XML / HTML)

    private const int ModeInTag = 1;
    private const int ModeInMarkupComment = 2;

    private static CodeTokenState Markup(string line, List<CodeToken> tokens, CodeTokenState stateIn)
    {
        var i = 0;
        var state = stateIn.Mode;

        if (state == ModeInMarkupComment)
        {
            var end = line.IndexOf("-->", StringComparison.Ordinal);
            if (end < 0)
            {
                tokens.Add(new(0, line.Length, CodeTokenKind.Comment));
                return stateIn;
            }

            tokens.Add(new(0, end + 3, CodeTokenKind.Comment));
            i = end + 3;
            state = 0;
        }

        while (i < line.Length)
        {
            if (state == ModeInTag)
            {
                while (i < line.Length && line[i] is ' ' or '\t')
                {
                    i++;
                }

                if (i >= line.Length)
                {
                    break;
                }

                var ch = line[i];
                if (ch == '"' || ch == '\'')
                {
                    var close = ScanQuoted(line, i, ch);
                    tokens.Add(new(i, Math.Min(close, line.Length - 1) - i + 1, CodeTokenKind.String));
                    i = close + 1;
                    continue;
                }

                if (ch == '>' || (ch == '/' && i + 1 < line.Length && line[i + 1] == '>'))
                {
                    var length = ch == '>' ? 1 : 2;
                    tokens.Add(new(i, length, CodeTokenKind.Tag));
                    i += length;
                    state = 0;
                    continue;
                }

                var start = i;
                while (i < line.Length && line[i] is not (' ' or '\t' or '=' or '>' or '/'))
                {
                    i++;
                }

                // A malformed tag can leave the scan nowhere to go; step over the byte
                // so the tokenizer never stalls on it.
                if (i == start)
                {
                    i++;
                    continue;
                }

                tokens.Add(new(start, i - start, CodeTokenKind.Attribute));
                continue;
            }

            var open = line.IndexOf('<', i);
            if (open < 0)
            {
                break;
            }

            // Comment openings swallow the rest of the line, or more.
            if (line.AsSpan(open).StartsWith("<!--", StringComparison.Ordinal))
            {
                if (open > i)
                {
                    tokens.Add(new(i, open - i, CodeTokenKind.Plain));
                }

                var end = line.IndexOf("-->", open + 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(new(open, line.Length - open, CodeTokenKind.Comment));
                    state = ModeInMarkupComment;
                    break;
                }

                tokens.Add(new(open, end + 3 - open, CodeTokenKind.Comment));
                i = end + 3;
                continue;
            }

            // A tag opener: '<' plus a name, then the attribute region.
            if (open + 1 < line.Length && (char.IsLetter(line[open + 1]) || line[open + 1] is '/' or '!' or '?'))
            {
                if (open > i)
                {
                    tokens.Add(new(i, open - i, CodeTokenKind.Plain));
                }

                var nameStart = open;
                var nameEnd = open + 1;
                while (nameEnd < line.Length && (char.IsLetterOrDigit(line[nameEnd]) || line[nameEnd] is ':' or '-' or '_' or '.' or '/' or '!' or '?'))
                {
                    nameEnd++;
                }

                tokens.Add(new(nameStart, nameEnd - nameStart, CodeTokenKind.Tag));
                i = nameEnd;
                state = ModeInTag;
                continue;
            }

            // A bare '<' that opens nothing is text.
            tokens.Add(new(open, 1, CodeTokenKind.Plain));
            i = open + 1;
        }

        return new CodeTokenState { Mode = state };
    }

    // ------------------------------------------------------------------ JSON

    private static CodeTokenState Json(string line, List<CodeToken> tokens)
    {
        var i = 0;
        while (i < line.Length)
        {
            var ch = line[i];
            if (ch is ' ' or '\t')
            {
                i++;
                continue;
            }

            if (line.AsSpan(i).StartsWith("//", StringComparison.Ordinal))
            {
                tokens.Add(new(i, line.Length - i, CodeTokenKind.Comment));
                break;
            }

            if (ch == '"' || ch == '\'')
            {
                var close = ScanQuoted(line, i, ch);
                var j = close + 1;
                while (j < line.Length && line[j] == ' ')
                {
                    j++;
                }

                tokens.Add(new(i, close - i + 1, j < line.Length && line[j] == ':' ? CodeTokenKind.Property : CodeTokenKind.String));
                i = close + 1;
                continue;
            }

            if (char.IsDigit(ch) || (ch == '-' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
            {
                var start = i;
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '.' or '+' or '-'))
                {
                    i++;
                }

                tokens.Add(new(start, i - start, CodeTokenKind.Number));
                continue;
            }

            if (char.IsLetter(ch))
            {
                var start = i;
                while (i < line.Length && char.IsLetterOrDigit(line[i]))
                {
                    i++;
                }

                var word = line[start..i];
                tokens.Add(new(start, i - start,
                    word is "true" or "false" or "null" ? CodeTokenKind.Constant : CodeTokenKind.Plain));
                continue;
            }

            tokens.Add(new(i, 1, CodeTokenKind.Operator));
            i++;
        }

        return default;
    }

    // ------------------------------------------------------------------ Markdown

    private const int ModeInCodeFence = 1;

    private static CodeTokenState Markdown(string line, List<CodeToken> tokens, CodeTokenState stateIn)
    {
        if (stateIn.Mode == ModeInCodeFence)
        {
            tokens.Add(new(0, line.Length, line.StartsWith("```", StringComparison.Ordinal) ? CodeTokenKind.Heading : CodeTokenKind.String));
            return new CodeTokenState { Mode = line.StartsWith("```", StringComparison.Ordinal) ? 0 : ModeInCodeFence };
        }

        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            tokens.Add(new(0, line.Length, CodeTokenKind.Heading));
            return new CodeTokenState { Mode = ModeInCodeFence };
        }

        if (trimmed.Length > 0 && trimmed[0] == '#')
        {
            tokens.Add(new(0, line.Length, CodeTokenKind.Heading));
            return stateIn;
        }

        if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
        {
            tokens.Add(new(0, line.Length, CodeTokenKind.Comment));
            return stateIn;
        }

        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal)
            || trimmed.StartsWith("+ ", StringComparison.Ordinal))
        {
            var marker = line.Length - trimmed.Length + 1;
            tokens.Add(new(0, marker, CodeTokenKind.Operator));
        }

        // Inline runs: `code`, **bold**, *italic*.
        var i = 0;
        while (i < line.Length)
        {
            var ch = line[i];
            var run = ch == '`' ? "`" : ch == '*' ? (i + 1 < line.Length && line[i + 1] == '*' ? "**" : "*") : null;
            if (run is null || i + run.Length > line.Length)
            {
                i++;
                continue;
            }

            var end = line.IndexOf(run, i + run.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                i++;
                continue;
            }

            tokens.Add(new(i, end + run.Length - i, ch == '`' ? CodeTokenKind.String : CodeTokenKind.Keyword));
            i = end + run.Length;
        }

        return stateIn;
    }

    // ------------------------------------------------------------------ Batch

    private static readonly HashSet<string> BatchKeywords = new(StringComparer.OrdinalIgnoreCase)
    { "echo", "set", "if", "else", "for", "in", "do", "goto", "call", "exit", "pause", "cd", "chdir",
         "cls", "del", "erase", "copy", "xcopy", "move", "ren", "rename", "md", "mkdir", "rd", "rmdir",
         "pushd", "popd", "start", "title", "shift", "setlocal", "endlocal", "errorlevel", "exist",
         "defined", "not", "equ", "neq", "lss", "leq", "gtr", "geq", "type", "find", "findstr", "tasklist" };

    private static CodeTokenState Batch(string line, List<CodeToken> tokens)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("::", StringComparison.Ordinal)
            || trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("rem", StringComparison.OrdinalIgnoreCase))
        {
            tokens.Add(new(0, line.Length, CodeTokenKind.Comment));
            return default;
        }

        if (trimmed.StartsWith(':') && trimmed.Length > 1)
        {
            tokens.Add(new(0, line.Length, CodeTokenKind.Tag));
            return default;
        }

        var i = 0;
        while (i < line.Length)
        {
            var ch = line[i];
            if (ch is ' ' or '\t')
            {
                i++;
                continue;
            }

            if (ch == '%')
            {
                var end = line.IndexOf('%', i + 1);
                var length = end < 0 ? line.Length - i : end - i + 1;
                tokens.Add(new(i, length, CodeTokenKind.Variable));
                i += length;
                continue;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                {
                    i++;
                }

                if (BatchKeywords.Contains(line[start..i]))
                {
                    tokens.Add(new(start, i - start, CodeTokenKind.Keyword));
                }

                continue;
            }

            i++;
        }

        return default;
    }
}
