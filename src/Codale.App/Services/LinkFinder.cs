using System.Text.RegularExpressions;

namespace Codale.App.Services;

/// <summary>A URL or file reference found in text; Line and Column are 1-based when given.</summary>
public sealed record LinkMatch(int Index, int Length, string Text, string? Url, string? Path, int Line = 0, int Column = 0);

/// <summary>
/// Finds what a reader would click in terminal output or a chat message: web URLs, and
/// file paths with an optional <c>:line[:col]</c> suffix - the shapes compilers, test
/// runners and agents print (<c>src\Foo.cs:42:7</c>, <c>src/Foo.cs(42,7)</c>, <c>C:\x\y.txt</c>).
/// </summary>
public static partial class LinkFinder
{
    // A URL runs to whitespace or a closing quote/bracket; trailing sentence punctuation is trimmed after.
    // Every pattern runs over arbitrary terminal output, so each carries a match timeout
    // (see Find: a pathological line costs its links, not the UI thread).
    private const int MatchTimeoutMs = 250;

    [GeneratedRegex(@"https?://[^\s<>""'`\]\)]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex UrlPattern();

    // Drive or UNC-rooted, or a relative path with at least one separator; a name.ext
    // pair alone is matched only when the caller asks for it (a code span, not prose).
    [GeneratedRegex(
        @"(?<![\w/\\.:-])(?<path>(?:[A-Za-z]:[\\/]|\\\\|\.{1,2}[\\/]|~[\\/])?(?:[\w@.+\-]+[\\/])+[\w@.+\-]*\.[A-Za-z0-9]{1,8}|(?:[A-Za-z]:[\\/])(?:[\w@.+\- ]+[\\/])*[\w@.+\-]+)(?:(?::(?<line>\d+)(?::(?<col>\d+))?)|(?:\((?<line2>\d+)(?:,\s*(?<col2>\d+))?\)))?",
        RegexOptions.None,
        matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^(?<path>[\w@.+\-]+\.[A-Za-z][A-Za-z0-9]{0,7})(?:(?::|\s+)(?<line>\d+)(?::(?<col>\d+))?)?$", RegexOptions.None, matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex BareFilePattern();

    [GeneratedRegex(@"^v?\d+(\.\d+)+$", RegexOptions.None, matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex VersionPattern();

    // Prose mention of a bare file name (test.cs, Foo.xaml:12, Foo.cs 40:20): only for
    // well-known source extensions, so "e.g." and "Node.js" stay plain text.
    [GeneratedRegex(
        @"(?<![\w/\\.:@-])(?<path>[\w@+\-]+(?:\.[\w@+\-]+)*\.(?:cs|csproj|sln|xaml|xml|json|md|txt|ts|tsx|js|jsx|py|rs|go|java|kt|cpp|c|h|hpp|css|scss|html|yml|yaml|toml|sh|ps1|sql|razor|props|targets|config))(?![\w])(?:(?::(?<line>\d+)(?::(?<col>\d+))?)|(?:[ \t]+(?<line3>\d+):(?<col3>\d+))|(?:\((?<line2>\d+)(?:,\s*(?<col2>\d+))?\)))?",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: MatchTimeoutMs)]
    private static partial Regex ProseFilePattern();

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

                var line = FirstNumber(m, "line", "line2");
                var col = FirstNumber(m, "col", "col2");
                Add(new LinkMatch(m.Index, m.Length, m.Value, null, path, line, col));
            }

            foreach (Match m in ProseFilePattern().Matches(text))
            {
                if (Overlaps(m))
                {
                    continue;
                }

                Add(new LinkMatch(m.Index, m.Length, m.Value, null, m.Groups["path"].Value,
                    FirstNumber(m, "line", "line2", "line3"), FirstNumber(m, "col", "col2", "col3")));
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

        return new LinkMatch(0, text.Length, text, null, bare.Groups["path"].Value,
            FirstNumber(bare, "line"), FirstNumber(bare, "col"));
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
