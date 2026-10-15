using System.Text.RegularExpressions;

namespace Codale.Core.Text;

/// <summary>
/// A URL or file reference found in text; Line, Column, EndLine and EndColumn are 1-based when given.
/// </summary>
public sealed record LinkMatch(
    int Index,
    int Length,
    string Text,
    string? Url,
    string? Path,
    int Line = 0,
    int Column = 0,
    int EndLine = 0,
    int EndColumn = 0);

/// <summary>
/// Finds what a reader would click in terminal output or a chat message: web URLs, and
/// file paths with an optional location suffix - the shapes compilers, test
/// runners, linters, and agents print (e.g. <c>src\Foo.cs:42:7</c>, <c>src/Foo.cs(42,7)</c>,
/// <c>X:/test.js:10-5</c>, <c>src/Foo.cs:10-50</c>, <c>C:\x\y.txt</c>).
/// </summary>
public static partial class LinkFinder
{
    private const int MatchTimeoutMs = 250;

    [GeneratedRegex(@"https?://[^\s<>""'`\]\)]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex UrlPattern();

    // Drive or UNC-rooted, or a relative path with at least one separator; a name.ext
    // pair alone is matched only when the caller asks for it (a code span, not prose).
    [GeneratedRegex(
        @"(?<![\w/\\.:-])(?<path>(?:[A-Za-z]:[\\/]|\\\\|\.{1,2}[\\/]|~[\\/])(?:[\w@.+\- ]+[\\/])*[\w@.+\-]+|(?:[\w@.+\-]+[\\/])+[\w@.+\-]*\.[A-Za-z0-9]{1,8})(?:(?::(?<line>\d+)(?:(?::(?<col>\d+)(?:-(?:(?<endline2>\d+):)?(?<endcol>\d+))?)|(?:-(?<num2>\d+)(?::(?<endcol2>\d+))?))?)|(?:\((?<line2>\d+)(?:(?:,\s*(?<col2>\d+)(?:(?:,\s*(?<endline3>\d+),\s*|-(?:(?<endline4>\d+):)?)(?<endcol3>\d+))?)|(?:-(?<num3>\d+)(?::(?<endcol4>\d+))?))?\)))?",
        RegexOptions.None,
        matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex PathPattern();

    [GeneratedRegex(
        @"^(?<path>[\w@.+\-]+\.[A-Za-z][A-Za-z0-9]{0,7})(?:(?::|\s+)(?<line>\d+)(?:(?::(?<col>\d+)(?:-(?:(?<endline2>\d+):)?(?<endcol>\d+))?)|(?:-(?<num2>\d+)(?::(?<endcol2>\d+))?))?|(?:\((?<line2>\d+)(?:(?:,\s*(?<col2>\d+)(?:(?:,\s*(?<endline3>\d+),\s*|-(?:(?<endline4>\d+):)?)(?<endcol3>\d+))?)|(?:-(?<num3>\d+)(?::(?<endcol4>\d+))?))?\)))?$",
        RegexOptions.None,
        matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex BareFilePattern();

    [GeneratedRegex(@"^v?\d+(\.\d+)+$", RegexOptions.None, matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex VersionPattern();

    // Prose mention of a bare file name (test.cs, Foo.xaml:12, Foo.cs 40:20): only for
    // well-known source extensions, so "e.g." and "Node.js" stay plain text.
    [GeneratedRegex(
        @"(?<![\w/\\.:@-])(?<path>[\w@+\-]+(?:\.[\w@+\-]+)*\.(?:cs|csproj|sln|xaml|xml|json|md|txt|ts|tsx|js|jsx|py|rs|go|java|kt|cpp|c|h|hpp|css|scss|html|yml|yaml|toml|sh|ps1|sql|razor|props|targets|config))(?![\w])(?:(?::(?<line>\d+)(?:(?::(?<col>\d+)(?:-(?:(?<endline2>\d+):)?(?<endcol>\d+))?)|(?:-(?<num2>\d+)(?::(?<endcol2>\d+))?))?)|(?:[ \t]+(?<line3>\d+)(?::(?<col3>\d+)|-(?<num4>\d+)))|(?:\((?<line2>\d+)(?:(?:,\s*(?<col2>\d+)(?:(?:,\s*(?<endline3>\d+),\s*|-(?:(?<endline4>\d+):)?)(?<endcol3>\d+))?)|(?:-(?<num3>\d+)(?::(?<endcol4>\d+))?))?\)))?",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex ProseFilePattern();

    [GeneratedRegex(
        @"^(?:L(?<line>\d+)(?:(?:C|:)(?<col>\d+))?(?:-(?:L(?<l2>\d+)|(?<num2>\d+))(?:(?:C|:)(?<endcol>\d+))?)?|(?<line3>\d+)(?:(?::(?<col3>\d+)(?:-(?:(?<endline2>\d+):)?(?<endcol2>\d+))?)|(?:-(?<num3>\d+)(?::(?<endcol3>\d+))?))?)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex HashLocationPattern();

    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', '!', '?', '\'', '"'];

    /// <summary>Every URL and path in the text, in order, without overlaps.</summary>
    public static IReadOnlyList<LinkMatch> Find(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var found = new List<LinkMatch>();

        // Characters already claimed by an earlier (stronger) pattern: a later match that touches any is dropped.
        var claimed = new bool[text.Length];

        void Add(LinkMatch link)
        {
            found.Add(link);
            Array.Fill(claimed, true, link.Index, link.Length);
        }

        bool Overlaps(Match m) => Array.IndexOf(claimed, true, m.Index, m.Length) >= 0;

        try
        {
            foreach (Match m in UrlPattern().Matches(text))
            {
                var url = m.Value.TrimEnd(TrailingPunctuation);
                if (Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    Add(new LinkMatch(m.Index, url.Length, url, url, null));
                }
            }

            foreach (Match m in PathPattern().Matches(text))
            {
                if (Overlaps(m))
                {
                    continue;
                }

                var path = m.Groups["path"].Value;
                if (LooksLikeVersion(path))
                {
                    continue;
                }

                var (line, col, endLine, endCol) = ParseLocation(m);
                Add(new LinkMatch(m.Index, m.Length, m.Value, null, path, line, col, endLine, endCol));
            }

            foreach (Match m in ProseFilePattern().Matches(text))
            {
                if (Overlaps(m))
                {
                    continue;
                }

                var (line, col, endLine, endCol) = ParseLocation(m);
                Add(new LinkMatch(m.Index, m.Length, m.Value, null, m.Groups["path"].Value, line, col, endLine, endCol));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological text: keep the links found so far.
        }

        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        return found;
    }

    /// <summary>The link under a column of one line of text, or null.</summary>
    public static LinkMatch? At(string text, int column) =>
        Find(text).FirstOrDefault(m => column >= m.Index && column < m.Index + m.Length);

    /// <summary>
    /// A whole string that is one file reference - the content of a code span such as
    /// <c>Program.cs:42</c> - where a bare file name is enough evidence.
    /// </summary>
    public static LinkMatch? AsFileReference(string text)
    {
        text = text.Trim();

        if (Find(text) is [{ } only] && only.Index == 0 && only.Length == text.Length)
        {
            return only;
        }

        var bare = BareFilePattern().Match(text);
        if (!bare.Success || LooksLikeVersion(bare.Groups["path"].Value))
        {
            return null;
        }

        var (line, col, endLine, endCol) = ParseLocation(bare);
        return new LinkMatch(0, text.Length, text, null, bare.Groups["path"].Value, line, col, endLine, endCol);
    }

    /// <summary>Parses location parts from a URL hash fragment (e.g. #L10-L25, #L10C5, #10-5, #10:5).</summary>
    public static (int Line, int Column, int EndLine, int EndColumn) ParseHashLocation(string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return (0, 0, 0, 0);
        }

        var m = HashLocationPattern().Match(fragment.Trim());
        if (!m.Success)
        {
            return (0, 0, 0, 0);
        }

        var line = FirstNumber(m, "line", "line3");
        var col = FirstNumber(m, "col", "col3");
        var endLine = FirstNumber(m, "l2", "endline2");
        var endCol = FirstNumber(m, "endcol", "endcol2");
        var num = FirstNumber(m, "num2", "num3");

        if (num > 0 && endLine == 0)
        {
            if (num >= line)
            {
                endLine = num;
            }
            else if (col == 0)
            {
                col = num;
            }
        }

        return (line, col, endLine, endCol);
    }

    private static (int Line, int Column, int EndLine, int EndColumn) ParseLocation(Match m)
    {
        var line = FirstNumber(m, "line", "line2", "line3");
        var col = FirstNumber(m, "col", "col2", "col3");
        var endLine = FirstNumber(m, "endline2", "endline3", "endline4");
        var endCol = FirstNumber(m, "endcol", "endcol2", "endcol3", "endcol4");
        var num = FirstNumber(m, "num2", "num3", "num4");

        if (num > 0 && endLine == 0)
        {
            if (num >= line)
            {
                endLine = num;
            }
            else if (col == 0)
            {
                col = num;
            }
        }

        if (endLine > 0 && endLine == line && endCol > 0 && col == 0)
        {
            col = 1;
        }

        return (line, col, endLine, endCol);
    }

    private static int FirstNumber(Match m, params string[] groups)
    {
        foreach (var name in groups)
        {
            if (m.Groups[name] is { Success: true } g && int.TryParse(g.Value, out var n))
            {
                return n;
            }
        }

        return 0;
    }

    /// <summary>"1.2.3" and "v10.0.1" have dots and digits but name no file.</summary>
    private static bool LooksLikeVersion(string path) =>
        !path.Any(char.IsLetter) || VersionPattern().IsMatch(path);
}