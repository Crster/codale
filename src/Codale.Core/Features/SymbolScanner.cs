using System.Text.RegularExpressions;

namespace Codale.Core.Features;

/// <summary>
/// Finds declarations (types, functions, methods, tables) line by line with language-agnostic patterns: no parser,
/// so it works on any text and never fails, at the price of missing exotic syntax. Used to cut files at symbol
/// boundaries and to list the definitions a prompt is about.
/// </summary>
public static partial class SymbolScanner
{
    /// <summary>A declaration found on one line. <see cref="IsContainer"/> types hold members; <see cref="IsBoundary"/> ones start a new unit of code.</summary>
    public readonly record struct Declaration(string Kind, string Name)
    {
        public bool IsContainer => Kind is "class" or "interface" or "record" or "struct" or "enum";
        public bool IsBoundary => Kind is not ("const" or "let" or "var" or "namespace");
    }

    private static readonly HashSet<string> Primitive = new(StringComparer.Ordinal)
        { "int", "long", "bool", "string", "double", "float", "char", "byte", "short", "uint", "ulong", "decimal", "object", "var", "TimeSpan", "DateTime" };

    [GeneratedRegex(
        @"^\s*(?:export\s+)?(?:(?:public|private|protected|internal|static|async|sealed|partial|abstract|default|readonly|override|virtual|pub|declare)\s+)*" +
        @"(?<kind>class|interface|record|struct|enum|function|def|fn|func|type|const|let|var|namespace)\s+" +
        @"(?<name>[A-Za-z_$][\w$]*)", RegexOptions.IgnoreCase)]
    private static partial Regex DefinitionPattern();

    [GeneratedRegex(@"^\s*func\s*\([^)]*\)\s*(?<name>[A-Za-z_]\w*)\s*\(")]
    private static partial Regex GoMethodPattern();

    // SQL usually sits inside a string (db.exec(`CREATE TABLE ...`)), so tables match anywhere on the line.
    [GeneratedRegex(@"\bcreate\s+(?:virtual\s+)?table\s+(?:if\s+not\s+exists\s+)?[`""\[]?(?<name>[A-Za-z_][\w]*)", RegexOptions.IgnoreCase)]
    private static partial Regex TablePattern();

    // C#/Java members: at least one modifier, a return type, a name and an opening parenthesis.
    [GeneratedRegex(
        @"^\s*(?:(?:public|private|protected|internal|static|async|sealed|override|virtual|abstract|partial|final|synchronized)\s+)+" +
        @"[\w<>\[\],.?]+\s+(?<name>[A-Za-z_]\w*)\s*[(<]")]
    private static partial Regex MethodPattern();

    // const handler = (e) => ..., const f = async function ...: the unit of code in modern JS and TS.
    [GeneratedRegex(@"=\s*(?:async\s+)?(?:\([^)]*\)\s*(?::\s*[\w<>\[\]|, .]+)?\s*=>|function\b|[A-Za-z_$][\w$]*\s*=>)")]
    private static partial Regex ArrowPattern();

    /// <summary>The declaration on <paramref name="line"/>, or null when there is none.</summary>
    public static Declaration? Match(string line)
    {
        var m = DefinitionPattern().Match(line);
        if (m.Success)
        {
            var kind = m.Groups["kind"].Value.ToLowerInvariant();
            var name = m.Groups["name"].Value;
            // "const int Max", "namespace Codale": the keyword is followed by a type or a container, never the thing defined.
            if (kind == "namespace" || (kind is "const" or "var" or "let" && Primitive.Contains(name))) return null;
            if (kind is "const" or "let" or "var") return ArrowPattern().IsMatch(line) ? new Declaration("function", name) : new Declaration(kind, name);
            return new Declaration(kind, name);
        }

        if ((m = GoMethodPattern().Match(line)).Success) return new Declaration("method", m.Groups["name"].Value);
        if ((m = TablePattern().Match(line)).Success) return new Declaration("table", m.Groups["name"].Value);
        if ((m = MethodPattern().Match(line)).Success) return new Declaration("method", m.Groups["name"].Value);
        return null;
    }

    /// <summary>Columns of leading whitespace, a tab counting as four.</summary>
    public static int Indent(string line)
    {
        var columns = 0;
        foreach (var c in line)
        {
            if (c == ' ') columns++;
            else if (c == '\t') columns += 4;
            else break;
        }
        return columns;
    }
}
