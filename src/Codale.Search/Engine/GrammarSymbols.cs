using Codale.Core.Syntax;

namespace Codale.Search;

/// <summary>
/// User symbols supplied by the file's TextMate grammar, with C++ as the fallback.
/// Generic variable scopes require an assignment target; these remain lexical
/// evidence, not a semantic parser.
/// </summary>
internal static class GrammarSymbols
{
    // Index workers have their own registry so background scans do not hold the editor's tokenizer lock.
    private static readonly ThreadLocal<(SyntaxStore? Store, GrammarTokenizer? Tokenizer)> Workers = new();

    public static IEnumerable<SourceSymbol> Extract(string path, string text, StringPool? pool, SyntaxStore? syntaxStore = null)
    {
        var store = syntaxStore ?? SyntaxService.Store;
        var catalog = syntaxStore is null ? SyntaxService.Catalog : new LanguageCatalog(store,
            new TextMateSharp.Grammars.RegistryOptions(TextMateSharp.Grammars.ThemeName.DarkPlus));
        var firstEnd = text.IndexOfAny(['\r', '\n']);
        var language = catalog.ForFile(path, firstEnd < 0 ? text : text[..firstEnd])?.Id;

        var worker = Workers.Value;
        if (!ReferenceEquals(worker.Store, store))
        {
            worker = (store, new GrammarTokenizer(store, catalog,
                new TextMateSharp.Grammars.RegistryOptions(TextMateSharp.Grammars.ThemeName.DarkPlus),
                lineBudget: TimeSpan.FromMilliseconds(250)));
            Workers.Value = worker;
        }

        var tokenizer = worker.Tokenizer!;
        // The file's own grammar answers when it recognised anything, even with no declarations (Markdown, JSON, YAML):
        // re-tokenising those with the C++ grammar finds only noise. The C++ fallback is for files no grammar claims,
        // and for a grammar that failed or matched nothing at all.
        if (language is not null && tokenizer.Supports(language) &&
            TryExtract(language, text, pool, tokenizer, out var recognised) is { } symbols && recognised) return symbols;
        return language != "cpp" && tokenizer.Supports("cpp")
            ? TryExtract("cpp", text, pool, tokenizer, out _) ?? []
            : [];
    }

    /// <summary>
    /// The declarations under <paramref name="language"/>'s grammar, or null when a line cannot be tokenised.
    /// <paramref name="recognised"/> is true when the grammar scoped any token beyond its root scope.
    /// </summary>
    private static SourceSymbol[]? TryExtract(string language, string text, StringPool? pool, GrammarTokenizer tokenizer, out bool recognised)
    {
        recognised = false;
        var symbols = new List<SourceSymbol>();
        object? state = null;
        var tokens = new List<CodeToken>();
        var lines = SourceText.SplitLines(text);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            // Discard partial results on failure; the fallback starts again from the
            // beginning so multiline comment and string state stays consistent.
            if (!tokenizer.TryTokenizeLine(language, line, state, tokens, out state)) return null;
            seen.Clear();
            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                var token = tokens[tokenIndex];
                var scopes = token.ScopeStack?.Split('\u001f') ?? [];
                if (scopes.Length > 1) recognised = true;
                var leaf = scopes.LastOrDefault() ?? "";
                if (scopes.Any(s => IsScope(s, "comment") || IsScope(s, "string") || IsScope(s, "variable.language"))) continue;
                var kind = Kind(leaf);
                // The C++ grammar leaves some declaration names as plain spans between
                // a scoped type and assignment operator.
                if (kind is null && language == "cpp" && token.Kind == CodeTokenKind.Plain &&
                    FollowedByAssignment(line, tokens, tokenIndex) && HasDeclarationType(tokens, tokenIndex))
                    kind = SourceSymbolKind.Variable;
                if (kind is null) continue;
                // A number of grammars use entity.name scopes for both definitions and
                // uses. Keep only declaration scopes, excluding calls and type references.
                if (kind == SourceSymbolKind.Type && !IsTypeDeclaration(leaf, scopes, tokens, tokenIndex)) continue;
                if (kind == SourceSymbolKind.Function &&
                    (IsScope(leaf, "entity.name.function.call") ||
                     scopes.Any(s => IsScope(s, "meta.function-call") || IsScope(s, "meta.method-call")) ||
                     language == "cpp" && IsMemberCall(line, token.Start) ||
                     language == "csharp" && !HasDeclarationType(tokens, tokenIndex))) continue;

                // Generic variable scopes also cover references. Require an assignment target
                // unless the grammar explicitly identifies a declaration or parameter.
                if (kind == SourceSymbolKind.Variable && !IsScope(leaf, "entity.name.variable") && !IsScope(leaf, "variable.parameter") &&
                    !scopes.Any(s => IsScope(s, "meta.definition.variable")) &&
                    !FollowedByAssignment(line, tokens, tokenIndex)) continue;

                var name = line.Substring(token.Start, token.Length).Trim().TrimStart('$', '@', '%');
                if (name.Length == 0 || name.Length > SourceTokens.MaxLength ||
                    !SourceTokens.IsStart(name[0]) || !name.All(SourceTokens.IsPart) || !seen.Add(name)) continue;
                var end = kind == SourceSymbolKind.Variable ? lineNumber : SourceSymbols.BodyEnd(lines, lineNumber);
                symbols.Add(new SourceSymbol(pool?.Intern(name) ?? name, kind.Value, lineNumber, end));
            }
        }
        return [.. symbols];
    }

    private static bool IsScope(string scope, string prefix) =>
        scope == prefix || scope.StartsWith(prefix + ".", StringComparison.Ordinal);

    private static bool IsMemberCall(string line, int start)
    {
        var prefix = line.AsSpan(0, start).TrimEnd();
        return prefix.EndsWith(".") || prefix.EndsWith("->");
    }

    private static SourceSymbolKind? Kind(string scope)
    {
        if (IsScope(scope, "entity.name.type") || IsScope(scope, "entity.name.class") ||
            IsScope(scope, "entity.name.struct") || IsScope(scope, "entity.name.interface") ||
            IsScope(scope, "entity.name.enum") || IsScope(scope, "entity.name.trait")) return SourceSymbolKind.Type;
        if (IsScope(scope, "entity.name.function")) return SourceSymbolKind.Function;
        if (IsScope(scope, "entity.name.variable.property")) return SourceSymbolKind.Property;
        if (IsScope(scope, "entity.name.variable") || IsScope(scope, "variable.other") ||
            IsScope(scope, "variable.parameter")) return SourceSymbolKind.Variable;
        return null;
    }

    private static bool IsTypeDeclaration(string leaf, string[] scopes, List<CodeToken> tokens, int index)
    {
        if (scopes.Any(s => IsScope(s, "meta.type.annotation") || IsScope(s, "meta.class.inheritance") ||
                            IsScope(s, "meta.function-call"))) return false;
        if (new[] { "class", "struct", "interface", "enum", "record", "trait", "alias", "typedef", "protocol", "union" }
            .Any(category => IsScope(leaf, "entity.name.type." + category) || IsScope(leaf, "entity.name." + category))) return true;
        if (scopes.Any(s => IsScope(s, "meta.definition.type"))) return true;
        var previous = PreviousToken(tokens, index);
        return previous >= 0 && (tokens[previous].ScopeStack?.Split('\u001f').LastOrDefault() is { } scope) &&
               IsScope(scope, "storage.type") && !IsScope(scope, "storage.type.primitive");
    }

    // C# gives calls and declarations the same function scope. The immediately
    // preceding grammar tokens supply the return type (or a constructor modifier).
    private static bool HasDeclarationType(List<CodeToken> tokens, int index)
    {
        var previous = PreviousToken(tokens, index);
        while (previous >= 0)
        {
            var scope = tokens[previous].ScopeStack?.Split('\u001f').LastOrDefault() ?? "";
            if (IsScope(scope, "entity.name.type") || IsScope(scope, "keyword.type") ||
                IsScope(scope, "storage.type") || IsScope(scope, "storage.modifier") &&
                !IsScope(scope, "storage.modifier.new")) return true;
            if (!IsScope(scope, "punctuation.definition.typeparameters.end") &&
                !IsScope(scope, "punctuation.squarebracket.close") &&
                !IsScope(scope, "punctuation.squarebracket.open") &&
                !IsScope(scope, "punctuation.separator.comma") &&
                !IsScope(scope, "punctuation.separator.question-mark") &&
                !IsScope(scope, "keyword.operator.nullable")) return false;
            previous = PreviousToken(tokens, previous);
        }
        return false;
    }

    private static int PreviousToken(List<CodeToken> tokens, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var scope = tokens[i].ScopeStack?.Split('\u001f').LastOrDefault() ?? "";
            if (!IsScope(scope, "source") && !IsScope(scope, "comment")) return i;
        }
        return -1;
    }

    private static bool FollowedByAssignment(string line, List<CodeToken> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var text = line.AsSpan(token.Start, token.Length).Trim();
            if (text.IsEmpty) continue;
            return text.SequenceEqual("=") && token.ScopeStack?.Split('\u001f')
                .Any(s => IsScope(s, "keyword.operator.assignment")) == true;
        }
        return false;
    }
}
