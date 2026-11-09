using Codale.Core.Syntax;

namespace Codale.App.Controls;

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
/// The colours tokens render in: a One Dark-ish set on dark surfaces and its One Light
/// counterpart on light ones (the dark hues - pale yellow, light green - wash out on white).
/// </summary>
public static class CodePalette
{
    /// <summary>The app's resolved theme; set by <see cref="ChatPaletteTheme"/>.</summary>
    public static bool Light { get; set; }

    public static Windows.UI.Color Get(CodeTokenKind kind) => Get(kind, Light);

    public static Windows.UI.Color Get(CodeTokenKind kind, bool light) => light
        ? kind switch
        {
            CodeTokenKind.Keyword => Windows.UI.Color.FromArgb(0xFF, 0x8E, 0x1A, 0xA8),
            CodeTokenKind.Type => Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x4B, 0x00),
            CodeTokenKind.String => Windows.UI.Color.FromArgb(0xFF, 0x11, 0x63, 0x29),
            CodeTokenKind.Number => Windows.UI.Color.FromArgb(0xFF, 0x05, 0x50, 0xAE),
            CodeTokenKind.Comment => Windows.UI.Color.FromArgb(0xFF, 0x57, 0x60, 0x6A),
            CodeTokenKind.Operator => Windows.UI.Color.FromArgb(0xFF, 0x0A, 0x5C, 0x7A),
            CodeTokenKind.Preprocessor => Windows.UI.Color.FromArgb(0xFF, 0xB3, 0x2A, 0x20),
            CodeTokenKind.Tag => Windows.UI.Color.FromArgb(0xFF, 0xB3, 0x2A, 0x20),
            CodeTokenKind.Attribute => Windows.UI.Color.FromArgb(0xFF, 0x7A, 0x4F, 0x00),
            CodeTokenKind.Property => Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x4F, 0xC0),
            CodeTokenKind.Constant => Windows.UI.Color.FromArgb(0xFF, 0x05, 0x50, 0xAE),
            CodeTokenKind.Variable => Windows.UI.Color.FromArgb(0xFF, 0xB3, 0x2A, 0x20),
            CodeTokenKind.Heading => Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x4F, 0xC0),
            _ => Windows.UI.Color.FromArgb(0, 0, 0, 0), // plain text keeps the theme foreground
        }
        : kind switch
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

/// <summary>Colours a line with the language's TextMate grammar. A language with no grammar renders as plain text.</summary>
public static class CodeTokenizer
{
    public static CodeTokenState TokenizeLine(string line, string? languageId, CodeTokenState stateIn, List<CodeToken> tokens)
    {
        tokens.Clear();
        if (languageId is not null
            && SyntaxService.Tokenizer.TryTokenizeLine(languageId, line, stateIn.Stack, tokens, out var stack))
        {
            return new CodeTokenState { Stack = stack };
        }

        tokens.Clear();
        return default;
    }

    private static readonly HashSet<string> BatchKeywords = new(StringComparer.OrdinalIgnoreCase)
    { "echo", "set", "if", "else", "for", "in", "do", "goto", "call", "exit", "pause", "cd", "chdir",
         "cls", "del", "erase", "copy", "xcopy", "move", "ren", "rename", "md", "mkdir", "rd", "rmdir",
         "pushd", "popd", "start", "title", "shift", "setlocal", "endlocal", "errorlevel", "exist",
         "defined", "not", "equ", "neq", "lss", "leq", "gtr", "geq", "type", "find", "findstr", "tasklist" };


    public static IEnumerable<string> BatchKeywordsOf() => BatchKeywords;
}
