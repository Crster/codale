using System.Text;
using System.Text.RegularExpressions;

namespace Codale.Search;

/// <summary>
/// What the project index knows that helps a completion model at the caret, written as the comment header
/// of the file being edited: its path and language (so a .cs file is never answered with JavaScript), the
/// neighbouring files, and the declarations elsewhere in the project that the code near the caret refers to.
/// </summary>
public static partial class CompletionContext
{
    private const int MaxSymbols = 10;
    private const int MaxSiblings = 6;
    private const int MaxLineChars = 140;
    private const int MaxChars = 1200;

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]{2,}")]
    private static partial Regex WordRegex();

    /// <summary>
    /// The header for <paramref name="filePath"/> (may be null for an unsaved buffer), or "" when the language has
    /// no line comment to carry it. <paramref name="nearby"/> is the code just before the caret.
    /// </summary>
    public static string Build(SourceSnapshot? snapshot, string root, string? filePath, string? languageId, string nearby)
    {
        var extension = Path.GetExtension(filePath ?? "").ToLowerInvariant();
        var comment = CommentPrefix(extension, languageId);
        if (comment is null)
        {
            return "";
        }

        var relative = RelativeTo(root, filePath);
        var builder = new StringBuilder();
        builder.Append(comment).Append(" File: ").Append(relative ?? "(unsaved)")
            .Append(" (").Append(LanguageName(extension, languageId)).Append(")\n");

        if (snapshot is not null && relative is not null)
        {
            var self = snapshot.Find(relative);
            AppendSiblings(builder, comment, snapshot, relative);
            AppendDeclarations(builder, comment, snapshot, self, nearby);
        }

        var header = builder.ToString();
        return header.Length > MaxChars ? FimLines(header, MaxChars) : header;
    }

    private static string FimLines(string text, int max)
    {
        var cut = text.LastIndexOf('\n', max - 1);
        return cut <= 0 ? text[..max] + "\n" : text[..(cut + 1)];
    }

    private static void AppendSiblings(StringBuilder builder, string comment, SourceSnapshot snapshot, string relative)
    {
        var folder = Path.GetDirectoryName(relative) ?? "";
        var names = snapshot.Files
            .Where(f => string.Equals(Path.GetDirectoryName(f.RelativePath) ?? "", folder, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(f.RelativePath, relative, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f.RelativePath))
            .Take(MaxSiblings)
            .ToList();

        if (names.Count > 0)
        {
            builder.Append(comment).Append(" Same folder: ").Append(string.Join(", ", names)).Append('\n');
        }
    }

    private static void AppendDeclarations(StringBuilder builder, string comment, SourceSnapshot snapshot, SourceFile? self, string nearby)
    {
        // Nearest to the caret first: the words at the end of the text are what the next line will use.
        var words = WordRegex().Matches(nearby).Select(m => m.Value).Reverse().Distinct(StringComparer.Ordinal);
        var lines = new List<string>();

        foreach (var word in words)
        {
            if (lines.Count >= MaxSymbols)
            {
                break;
            }

            var found = snapshot.Definitions(word)
                .Where(d => d.Symbol.Kind != SourceSymbolKind.Variable && !ReferenceEquals(d.File, self))
                .OrderBy(d => d.File.IsTestOrDoc)
                .FirstOrDefault();
            if (found is null)
            {
                continue;
            }

            var source = found.File.ReadLines();
            if (found.Symbol.Line < 1 || found.Symbol.Line > source.Length)
            {
                continue;
            }

            var declaration = source[found.Symbol.Line - 1].Trim();
            if (declaration.Length > MaxLineChars)
            {
                declaration = declaration[..MaxLineChars];
            }

            if (declaration.Length > 0)
            {
                lines.Add($"{comment}   {declaration}   [{found.File.RelativePath.Replace('\\', '/')}]\n");
            }
        }

        if (lines.Count > 0)
        {
            builder.Append(comment).Append(" Declared elsewhere in this project and used nearby:\n");
            foreach (var line in lines)
            {
                builder.Append(line);
            }
        }
    }

    private static string? RelativeTo(string root, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        try
        {
            if (string.IsNullOrEmpty(root))
            {
                return Path.GetFileName(filePath);
            }

            var relative = Path.GetRelativePath(root, filePath);
            return relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(filePath) : relative;
        }
        catch (ArgumentException)
        {
            return Path.GetFileName(filePath);
        }
    }

    /// <summary>The line-comment opener for a file, or null when the format has none (JSON, plain text).</summary>
    private static string? CommentPrefix(string extension, string? languageId)
    {
        var key = extension.TrimStart('.');
        if (key.Length == 0)
        {
            key = (languageId ?? "").ToLowerInvariant();
        }

        return key switch
        {
            "py" or "python" or "rb" or "ruby" or "sh" or "bash" or "ps1" or "powershell" or "yml" or "yaml" or "toml" or "r" or "pl" or "dockerfile" => "#",
            "sql" or "lua" or "hs" => "--",
            "json" or "txt" or "md" or "markdown" or "html" or "htm" or "xml" or "xaml" or "csproj" or "css" or "scss" or "svg" => null,
            _ => "//",
        };
    }

    private static string LanguageName(string extension, string? languageId) => extension switch
    {
        ".cs" => "C#",
        ".ts" => "TypeScript",
        ".tsx" => "TypeScript React",
        ".js" or ".mjs" or ".cjs" => "JavaScript",
        ".jsx" => "JavaScript React",
        ".py" => "Python",
        ".java" => "Java",
        ".kt" => "Kotlin",
        ".go" => "Go",
        ".rs" => "Rust",
        ".cpp" or ".cc" or ".cxx" or ".hpp" => "C++",
        ".c" or ".h" => "C",
        ".swift" => "Swift",
        ".php" => "PHP",
        ".rb" => "Ruby",
        ".ps1" => "PowerShell",
        ".sql" => "SQL",
        _ => string.IsNullOrEmpty(languageId) ? "source" : languageId,
    };
}
