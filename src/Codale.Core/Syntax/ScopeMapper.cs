namespace Codale.Core.Syntax;

/// <summary>
/// Turns a TextMate scope stack (<c>source.cs</c>, <c>string.quoted.double.cs</c>, ...) into one of
/// the editor's token kinds. Colours stay with the app's own palette rather than a TextMate theme,
/// so a grammar only has to name things by the standard scopes to get coloured sensibly.
/// </summary>
public static class ScopeMapper
{
    /// <summary>The most specific scope that means something wins; wrappers and punctuation are looked through.</summary>
    public static CodeTokenKind Map(IReadOnlyList<string> scopes)
    {
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (IsTransparent(scope))
            {
                continue;
            }

            var kind = MapOne(scope);
            if (kind != CodeTokenKind.Plain)
            {
                return kind;
            }

            // A named-but-plain scope (variable.other.readwrite) stops the walk: it is the answer.
            if (scope.StartsWith("variable", StringComparison.Ordinal) || scope.StartsWith("entity.name", StringComparison.Ordinal))
            {
                return CodeTokenKind.Plain;
            }
        }

        return CodeTokenKind.Plain;
    }

    private static bool IsTransparent(string scope) =>
        scope.StartsWith("punctuation", StringComparison.Ordinal)
        || scope.StartsWith("meta.", StringComparison.Ordinal)
        || scope.StartsWith("source", StringComparison.Ordinal)
        || scope.StartsWith("text", StringComparison.Ordinal);

    private static CodeTokenKind MapOne(string s)
    {
        if (s.StartsWith("comment", StringComparison.Ordinal)) return CodeTokenKind.Comment;
        if (s.StartsWith("string", StringComparison.Ordinal)) return CodeTokenKind.String;
        if (s.StartsWith("constant.numeric", StringComparison.Ordinal)) return CodeTokenKind.Number;
        if (s.StartsWith("constant", StringComparison.Ordinal)) return CodeTokenKind.Constant;
        if (s.StartsWith("keyword.operator", StringComparison.Ordinal)) return CodeTokenKind.Operator;
        if (s.StartsWith("keyword.type", StringComparison.Ordinal)) return CodeTokenKind.Type;
        if (s.StartsWith("keyword.control.directive", StringComparison.Ordinal)
            || s.StartsWith("keyword.other.preprocessor", StringComparison.Ordinal)) return CodeTokenKind.Preprocessor;
        if (s.StartsWith("keyword", StringComparison.Ordinal)) return CodeTokenKind.Keyword;
        if (s.StartsWith("storage.type.primitive", StringComparison.Ordinal)
            || s.StartsWith("storage.type.built-in", StringComparison.Ordinal)) return CodeTokenKind.Type;
        if (s.StartsWith("storage", StringComparison.Ordinal)) return CodeTokenKind.Keyword;
        if (s.StartsWith("entity.name.tag", StringComparison.Ordinal)) return CodeTokenKind.Tag;
        if (s.StartsWith("entity.other.attribute-name", StringComparison.Ordinal)) return CodeTokenKind.Attribute;
        if (s.StartsWith("entity.name.function", StringComparison.Ordinal)
            || s.StartsWith("support.function", StringComparison.Ordinal)) return CodeTokenKind.Property;
        if (s.StartsWith("entity.name.section", StringComparison.Ordinal)) return CodeTokenKind.Heading;
        if (s.StartsWith("support.type.property-name", StringComparison.Ordinal)) return CodeTokenKind.Property;
        if (s.StartsWith("entity.name.type", StringComparison.Ordinal)
            || s.StartsWith("entity.name.class", StringComparison.Ordinal)
            || s.StartsWith("entity.other.inherited-class", StringComparison.Ordinal)
            || s.StartsWith("support.class", StringComparison.Ordinal)
            || s.StartsWith("support.type", StringComparison.Ordinal)) return CodeTokenKind.Type;
        if (s.StartsWith("variable.language", StringComparison.Ordinal)) return CodeTokenKind.Keyword;
        if (s.StartsWith("variable.other.constant", StringComparison.Ordinal)
            || s.StartsWith("variable.other.enummember", StringComparison.Ordinal)) return CodeTokenKind.Constant;
        if (s.StartsWith("variable.other.property", StringComparison.Ordinal)
            || s.StartsWith("variable.other.object.property", StringComparison.Ordinal)
            || s.StartsWith("variable.other.member", StringComparison.Ordinal)) return CodeTokenKind.Property;
        if (s.StartsWith("markup.heading", StringComparison.Ordinal)) return CodeTokenKind.Heading;
        if (s.StartsWith("markup.inline.raw", StringComparison.Ordinal)
            || s.StartsWith("markup.fenced_code", StringComparison.Ordinal)) return CodeTokenKind.String;
        if (s.StartsWith("markup.underline.link", StringComparison.Ordinal)) return CodeTokenKind.Property;
        if (s.StartsWith("invalid", StringComparison.Ordinal)) return CodeTokenKind.Preprocessor;
        return CodeTokenKind.Plain;
    }
}
