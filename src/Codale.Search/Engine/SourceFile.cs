using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Codale.Search;

public enum SourceSymbolKind
{
    Type,
    Function,
    Property,
    Variable,
}

/// <summary>
/// A declaration found by the scan: a type, function, property or variable. <see cref="Line"/>
/// is where it starts and <see cref="EndLine"/> where its body ends (both 1-based, equal for a
/// variable or a declaration with no block).
/// </summary>
public sealed record SourceSymbol(string Name, SourceSymbolKind Kind, int Line, int EndLine);

/// <summary>
/// One text file of the project as the scan saw it: its identifier counts, its
/// declarations and - within the index's memory budget - its text.
/// </summary>
/// <remarks>
/// Immutable, so a refresh hands an unchanged file to the next snapshot as is: only
/// files whose size or write time moved are read again.
/// </remarks>
public sealed class SourceFile
{
    /// <summary>Strict, so text that would not survive the round trip is not retained at all.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The retained text as UTF-8: half the size of the string for source code.</summary>
    private readonly byte[]? _utf8;
    private readonly SourceSymbol[] _symbols;

    internal SourceFile(
        string relativePath,
        string fullPath,
        long length,
        DateTime lastWriteUtc,
        string? text,
        string[] termKeys,
        int[] termCounts,
        int tokenCount,
        SourceSymbol[] symbols,
        WordPair[]? transitions = null)
    {
        RelativePath = relativePath;
        FullPath = fullPath;
        Length = length;
        LastWriteUtc = lastWriteUtc;
        _utf8 = Encode(text);
        TermKeys = termKeys;
        TermCounts = termCounts;
        TokenCount = tokenCount;
        _symbols = symbols;
        Transitions = transitions ?? [];
        NameKey = Path.GetFileNameWithoutExtension(relativePath).ToLowerInvariant();
        DirectoryKey = (Path.GetDirectoryName(relativePath) ?? "").ToLowerInvariant();
        IsTestOrDoc = CodeDiscovery.IsTestOrDoc(relativePath);
    }

    /// <summary>Root-relative, in this system's separators.</summary>
    public string RelativePath { get; }

    public string FullPath { get; }

    public long Length { get; }

    public DateTime LastWriteUtc { get; }

    public IReadOnlyList<SourceSymbol> Symbols => _symbols;

    internal SourceSymbol[] SymbolArray => _symbols;

    /// <summary>The identifier pairs written next to each other in the file, with their counts (the model's transition channel).</summary>
    internal WordPair[] Transitions { get; }

    /// <summary>Identifiers (and their camelCase/snake_case parts) in the file: its length for ranking.</summary>
    public int TokenCount { get; }

    /// <summary>
    /// A file past the full-index size: searchable by its tokens, but with no declarations, no retained text and
    /// no place in the workspace model.
    /// </summary>
    public bool IsLarge { get; init; }

    /// <summary>True when the text is held in memory; false past the index's budget, when it is read from disk.</summary>
    public bool IsRetained => _utf8 is not null;

    public bool IsTestOrDoc { get; }

    /// <summary>Lower-case file name without extension.</summary>
    internal string NameKey { get; }

    /// <summary>Lower-case folder part of the path.</summary>
    internal string DirectoryKey { get; }

    /// <summary>Lower-case tokens, interned by the snapshot that owns the file.</summary>
    internal string[] TermKeys { get; }

    internal int[] TermCounts { get; }

    /// <summary>The file's text: the retained copy, else read from disk; null when it can no longer be read.</summary>
    public string? ReadText()
    {
        if (_utf8 is not null)
        {
            return Utf8.GetString(_utf8);
        }

        try
        {
            return File.ReadAllText(FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The retained text decoded into a pooled buffer, for a pass over many files that should not allocate a
    /// string per file. False when the text is not retained; the buffer goes back to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    internal bool TryRentText(out char[] buffer, out int length)
    {
        if (_utf8 is null)
        {
            buffer = [];
            length = 0;
            return false;
        }

        buffer = ArrayPool<char>.Shared.Rent(Math.Max(1, _utf8.Length));
        length = Utf8.GetChars(_utf8, buffer);
        return true;
    }

    private static byte[]? Encode(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return Utf8.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            // A lone surrogate: the text is read from disk instead.
            return null;
        }
    }

    public string[] ReadLines() => ReadText() is { } text ? SourceText.SplitLines(text) : [];

    public override string ToString() => RelativePath;
}

/// <summary>
/// One instance per distinct word across an index: its files' terms and word pairs and the workspace model's
/// aggregates all point at the same strings. Grows with the project's vocabulary and is never trimmed.
/// </summary>
internal sealed class StringPool
{
    private readonly ConcurrentDictionary<string, string> _strings = new(StringComparer.Ordinal);

    public string Intern(string value) => _strings.GetOrAdd(value, value);

    public string Intern(ReadOnlySpan<char> value) =>
        _strings.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(value, out var known)
            ? known
            : Intern(value.ToString());
}

internal static class SourceText
{
    public static string[] SplitLines(string text)
    {
        var lines = new List<string>(text.Length / 32 + 1);
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            lines.Add(line.ToString());
        }

        // A trailing newline ends the last line; it does not start an empty one.
        if (lines.Count > 0 && lines[^1].Length == 0 && text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r'))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return [.. lines];
    }

    /// <summary>The 1-based line of a character offset, given every line's start offset.</summary>
    public static int LineOf(List<int> lineStarts, int offset)
    {
        var index = lineStarts.BinarySearch(offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }

    public static List<int> LineStarts(string text)
    {
        var starts = new List<int>(text.Length / 32 + 1) { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    /// <summary>A NUL in the opening stretch: what git takes to mean binary.</summary>
    public static bool LooksBinary(string text) => text.AsSpan(0, Math.Min(text.Length, 8000)).Contains('\0');
}

/// <summary>
/// Splits code into the tokens the index ranks by: each identifier whole
/// (<c>retrypolicy</c>) and its camelCase / snake_case parts (<c>retry</c>, <c>policy</c>),
/// lower-cased, so a search for either the full name or one word of it lands.
/// </summary>
internal static class SourceTokens
{
    public const int MinLength = 2;
    public const int MaxLength = 64;
    public const int MinPartLength = 3;

    public static (string[] Keys, int[] Counts, int Total) Count(string text, StringPool? pool = null)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lookup = counts.GetAlternateLookup<ReadOnlySpan<char>>();
        Span<char> lower = stackalloc char[MaxLength];
        var total = 0;
        var span = text.AsSpan();

        for (var i = 0; i < span.Length;)
        {
            if (!IsStart(span[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < span.Length && IsPart(span[i]))
            {
                i++;
            }

            var word = span[start..i];
            if (word.Length < MinLength || word.Length > MaxLength)
            {
                continue;
            }

            var length = word.ToLowerInvariant(lower);
            Add(lookup, lower[..length], ref total);

            foreach (var (partStart, partLength) in Parts(word))
            {
                // The whole word was counted already; a "part" spanning all of it is not a part.
                if (partLength >= MinPartLength && partLength < word.Length)
                {
                    Add(lookup, lower.Slice(partStart, partLength), ref total);
                }
            }
        }

        var keys = new string[counts.Count];
        var values = new int[counts.Count];
        var n = 0;
        foreach (var (key, value) in counts)
        {
            keys[n] = pool?.Intern(key) ?? key;
            values[n++] = value;
        }

        return (keys, values, total);
    }

    private static void Add(Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup, ReadOnlySpan<char> key, ref int total)
    {
        ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(lookup, key, out _);
        count++;
        total++;
    }

    /// <summary>The lower-case tokens of free text or an identifier: the whole word first, then its parts.</summary>
    public static List<string> Of(string text)
    {
        var tokens = new List<string>();
        var span = text.AsSpan();
        for (var i = 0; i < span.Length;)
        {
            if (!IsStart(span[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < span.Length && IsPart(span[i]))
            {
                i++;
            }

            var word = span[start..i];
            if (word.Length < MinLength || word.Length > MaxLength)
            {
                continue;
            }

            var lower = word.ToString().ToLowerInvariant();
            if (!tokens.Contains(lower))
            {
                tokens.Add(lower);
            }

            foreach (var (partStart, partLength) in Parts(word))
            {
                if (partLength >= MinPartLength && partLength < word.Length &&
                    lower.Substring(partStart, partLength) is var part && !tokens.Contains(part))
                {
                    tokens.Add(part);
                }
            }
        }

        return tokens;
    }

    public static bool IsStart(char c) => char.IsLetter(c) || c == '_' || c == '$';

    /// <summary>The most identifier pairs one file contributes to the model; the most frequent are kept.</summary>
    public const int MaxTransitions = 500;

    /// <summary>
    /// The identifier pairs written one straight after the other, most frequent first: the words a
    /// model learns what tends to follow what from. Only identifiers of at least <see cref="MinLength"/>
    /// letters count, and a short word breaks the chain.
    /// </summary>
    public static WordPair[] Transitions(string text, StringPool? pool = null)
    {
        var counts = new Dictionary<(string, string), int>();
        string? previous = null;
        var span = text.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            if (!IsStart(span[i]))
            {
                i++;
                continue;
            }

            var start = i++;
            while (i < span.Length && IsPart(span[i]))
            {
                i++;
            }

            var letters = span[start..i];
            if (letters.Length < MinLength)
            {
                previous = null;
                continue;
            }

            // A run past any identifier's length is data, not a word worth keeping one copy of.
            var word = pool is null || letters.Length > MaxLength ? letters.ToString() : pool.Intern(letters);

            if (previous is not null)
            {
                counts.TryGetValue((previous, word), out var seen);
                counts[(previous, word)] = seen + 1;
            }

            previous = word;
        }

        return counts
            .OrderByDescending(p => p.Value)
            .Take(MaxTransitions)
            .Select(p => new WordPair(p.Key.Item1, p.Key.Item2, p.Value))
            .ToArray();
    }

    public static bool IsPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// <summary>
    /// The word boundaries inside one identifier: underscores, lower-to-upper
    /// (<c>retry|Policy</c>), and the end of an acronym (<c>HTTP|Client</c>).
    /// </summary>
    private static List<(int Start, int Length)> Parts(ReadOnlySpan<char> word)
    {
        var parts = new List<(int, int)>(4);
        var start = 0;

        for (var i = 1; i <= word.Length; i++)
        {
            var boundary = i == word.Length ||
                           word[i] == '_' || word[i] == '$' ||
                           (char.IsUpper(word[i]) && char.IsLower(word[i - 1])) ||
                           (char.IsUpper(word[i]) && i + 1 < word.Length && char.IsLower(word[i + 1]) && char.IsUpper(word[i - 1])) ||
                           (char.IsDigit(word[i]) != char.IsDigit(word[i - 1]));

            if (!boundary)
            {
                continue;
            }

            // Separators themselves are never part of a part.
            var s = start;
            while (s < i && (word[s] == '_' || word[s] == '$'))
            {
                s++;
            }

            if (i > s)
            {
                parts.Add((s, i - s));
            }

            start = i;
        }

        return parts;
    }
}

/// <summary>
/// Declarations by pattern, across the languages a project is likely to mix: C#, Java,
/// Kotlin, TypeScript/JavaScript, Python, Go, Rust, Swift. Not a parser - it only needs to
/// say "this name is defined here" often enough to follow a reference to its source.
/// </summary>
internal static partial class SourceSymbols
{
    /// <summary>A block cannot run longer than this many lines; past it the body is cut short rather than scanned on.</summary>
    private const int MaxBodyLines = 5000;

    public static SourceSymbol[] Extract(string text, StringPool? pool = null)
    {
        var symbols = new List<SourceSymbol>();
        var lineStarts = SourceText.LineStarts(text);
        var lines = SourceText.SplitLines(text);
        var seen = new HashSet<(string, int)>();

        void Add(Match match, SourceSymbolKind kind)
        {
            var name = match.Groups["name"].Value;
            if (name.Length < 2 || Reserved.Contains(name))
            {
                return;
            }

            var line = SourceText.LineOf(lineStarts, match.Groups["name"].Index);
            if (seen.Add((name, line)))
            {
                // A variable is one line; a type or function runs to the end of the block it opens.
                var end = kind == SourceSymbolKind.Variable ? line : BodyEnd(lines, line);
                symbols.Add(new SourceSymbol(pool?.Intern(name) ?? name, kind, line, end));
            }
        }

        foreach (Match match in TypeDeclaration().Matches(text))
        {
            Add(match, SourceSymbolKind.Type);
        }

        foreach (Match match in FunctionDeclaration().Matches(text))
        {
            Add(match, SourceSymbolKind.Function);
        }

        foreach (Match match in MemberDeclaration().Matches(text))
        {
            Add(match, match.Groups["prop"].Success ? SourceSymbolKind.Property : SourceSymbolKind.Function);
        }

        foreach (Match match in BoundFunction().Matches(text))
        {
            Add(match, SourceSymbolKind.Function);
        }

        // Variables come last so a name bound to a function on the same line stays a function.
        foreach (Match match in VariableDeclaration().Matches(text))
        {
            Add(match, SourceSymbolKind.Variable);
        }

        foreach (Match match in FieldDeclaration().Matches(text))
        {
            Add(match, SourceSymbolKind.Variable);
        }

        foreach (Match match in ScriptVariable().Matches(text))
        {
            Add(match, SourceSymbolKind.Variable);
        }

        symbols.Sort((a, b) => a.Line.CompareTo(b.Line));
        return [.. symbols];
    }

    /// <summary>
    /// The last line (1-based) of the body a declaration on <paramref name="line"/> opens. A brace
    /// block ends where its braces balance; a declaration ending in <c>:</c> (a Python def or
    /// class) runs over the lines indented past it. One with no block ends on its own line.
    /// </summary>
    internal static int BodyEnd(string[] lines, int line)
    {
        var start = line - 1;
        if (start < 0 || start >= lines.Length)
        {
            return line;
        }

        var declaration = Code(lines[start]).TrimEnd();
        if (declaration.EndsWith(':'))
        {
            var indent = Indent(lines[start]);
            var last = line;
            for (var i = start + 1; i < lines.Length && i - start < MaxBodyLines; i++)
            {
                if (lines[i].Trim().Length == 0)
                {
                    continue;
                }

                if (Indent(lines[i]) <= indent)
                {
                    break;
                }

                last = i + 1;
            }

            return last;
        }

        var depth = 0;
        var opened = false;
        for (var i = start; i < lines.Length && i - start < MaxBodyLines; i++)
        {
            var code = Code(lines[i]);
            foreach (var c in code)
            {
                if (c == '{')
                {
                    depth++;
                    opened = true;
                }
                else if (c == '}' && opened && --depth == 0)
                {
                    return i + 1;
                }
            }

            if (!opened && (i - start >= 2 || code.TrimEnd().EndsWith(';')))
            {
                return line;
            }
        }

        return line;
    }

    /// <summary>The line without its string literals and trailing // comment, so braces inside them do not count.</summary>
    private static string Code(string line)
    {
        var sb = new StringBuilder(line.Length);
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                quote = c;
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                break;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static int Indent(string line)
    {
        var n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t'))
        {
            n++;
        }

        return n;
    }

    /// <summary>Locals and constants: <c>var x =</c>, <c>let x</c>, <c>const x</c>, <c>val x</c>, <c>my $x</c>.</summary>
    [GeneratedRegex(
        @"^[ \t]*(?:(?:export|public|private|protected|internal|static|readonly|final|const|pub|mut|declare)[ \t]+)*" +
        @"(?:var|let|const|val|auto|local|my|our)[ \t]+(?<name>[A-Za-z_$][\w$]*)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex VariableDeclaration();

    /// <summary>A class field: at least one modifier, a type, a name, then <c>=</c> or <c>;</c>.</summary>
    [GeneratedRegex(
        @"^[ \t]*(?:(?:public|private|protected|internal|static|readonly|volatile|final|const)[ \t]+)+" +
        @"[\w.<>\[\],?]+(?:[ \t]*<[^>\n]*>)?[?\[\]]*[ \t]+(?<name>[A-Za-z_]\w*)[ \t]*(?:=[^=]|;)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FieldDeclaration();

    /// <summary>PowerShell script variables: <c>$total = ...</c>.</summary>
    [GeneratedRegex(@"^[ \t]*\$(?<name>[A-Za-z_]\w*)[ \t]*=(?!=)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptVariable();

    /// <summary>Words a loose pattern may take for a name.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "await",
        "get", "set", "init", "add", "remove", "value", "var", "let", "const", "this", "base", "super",
        "true", "false", "null", "void", "int", "string", "bool", "object", "struct", "class",
    };

    [GeneratedRegex(
        @"^[ \t]*(?:export[ \t]+)?(?:default[ \t]+)?(?:declare[ \t]+)?" +
        @"(?:(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|ref|final|open|data|inner|enum|annotation|value|pub(?:\([a-z]+\))?|file|unsafe)[ \t]+)*" +
        @"(?:record[ \t]+(?:struct|class)|class|interface|record|struct|enum|trait|protocol|object|type|typealias)[ \t]+" +
        @"(?<name>[A-Za-z_$][\w$]*)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();

    /// <summary>Keyword-led functions: <c>function</c>, <c>def</c>, <c>fn</c>, <c>func</c> (with a Go receiver), <c>fun</c>.</summary>
    [GeneratedRegex(
        @"^[ \t]*(?:export[ \t]+)?(?:default[ \t]+)?(?:(?:pub(?:\([a-z]+\))?|async|static|private|public|internal|override|suspend|inline|unsafe|const|extern)[ \t]+)*" +
        @"(?:function\*?|def|fn|func|fun)[ \t]+(?:\([^)\n]*\)[ \t]*)?(?:<[^>\n]*>[ \t]*)?(?<name>[A-Za-z_$][\w$]*)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FunctionDeclaration();

    /// <summary>
    /// C#/Java-style members: at least one modifier, an optional type, a name, then
    /// <c>(</c> for a method or <c>{</c> / <c>=&gt;</c> for a property. Requiring a
    /// modifier is what keeps calls and statements out.
    /// </summary>
    [GeneratedRegex(
        @"^[ \t]*(?:\[[^\]\n]*\][ \t]*)*" +
        @"(?:(?:public|private|protected|internal|static|async|override|virtual|abstract|sealed|extern|unsafe|partial|final|synchronized|required|readonly|event)[ \t]+)+" +
        @"(?:[\w.<>\[\],?()]+(?:<[^>\n]*>)?[?\[\]]*[ \t]+)?" +
        @"(?<name>[A-Za-z_]\w*)[ \t]*(?:<[^>\n]*>)?[ \t]*(?:\(|(?<prop>\{|=>))",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MemberDeclaration();

    /// <summary>JavaScript/TypeScript functions bound to a name: <c>const run = async (</c>, <c>let f = function</c>.</summary>
    [GeneratedRegex(
        @"^[ \t]*(?:export[ \t]+)?(?:const|let|var)[ \t]+(?<name>[A-Za-z_$][\w$]*)[ \t]*(?::[^=\n]+)?=[ \t]*(?:async[ \t]*)?(?:function\b|\([^)\n]*\)[ \t]*(?::[^=\n]+)?=>|[A-Za-z_$][\w$]*[ \t]*=>)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BoundFunction();
}

internal static class StringBuilderExtensions
{
    public static StringBuilder AppendPath(this StringBuilder builder, string relativePath) =>
        builder.Append(relativePath.Replace('\\', '/'));
}