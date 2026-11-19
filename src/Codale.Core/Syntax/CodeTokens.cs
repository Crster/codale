namespace Codale.Core.Syntax;

/// <summary>What a token is, which is what picks its colour from the palette.</summary>
public enum CodeTokenKind
{
    Plain,
    Keyword,
    Type,
    String,
    Number,
    Comment,
    Operator,
    Preprocessor,
    Tag,
    Attribute,
    Property,
    Constant,
    Variable,
    Heading,
}

/// <summary>One coloured span on one line: an offset into the line, a length, and a kind.</summary>
public readonly record struct CodeToken(int Start, int Length, CodeTokenKind Kind)
{
    /// <summary>Outer-to-inner TextMate scopes separated by U+001F. Value equality keeps incremental caches stable.</summary>
    public string? ScopeStack { get; init; }
}

[Flags]
public enum SyntaxFontStyle { None = 0, Italic = 1, Bold = 2, Underline = 4, Strikethrough = 8 }

public readonly record struct SyntaxTokenStyle(uint? Foreground, uint? Background, SyntaxFontStyle FontStyle);
