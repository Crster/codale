using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Codale.Core.Syntax;

internal static class FilenamePattern
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    internal static bool Matches(string pattern, string path)
    {
        pattern = pattern.Replace('\\', '/');
        if (pattern.Length > 512) return false;
        try
        {
            if (Cache.Count > 2048) Cache.Clear();
            var regex = Cache.GetOrAdd(pattern, Compile);
            var normalized = path.Replace('\\', '/');
            return regex.IsMatch(pattern.Contains('/') ? normalized : normalized[(normalized.LastIndexOf('/') + 1)..]);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { return false; }
    }

    private static Regex Compile(string pattern)
    {
        var expression = new StringBuilder(pattern.Contains('/') ? "(?:^|/)" : "^");
        var braces = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/') { expression.Append("(?:.*/)?"); i++; }
                    else expression.Append(".*");
                    break;
                case '*': expression.Append("[^/]*"); break;
                case '?': expression.Append("[^/]"); break;
                case '{': braces++; expression.Append("(?:"); break;
                case '}' when braces > 0: braces--; expression.Append(')'); break;
                case ',' when braces > 0: expression.Append('|'); break;
                case '[':
                    var close = pattern.IndexOf(']', i + 1);
                    if (close < 0) { expression.Append("\\["); break; }
                    var contents = pattern[(i + 1)..close];
                    expression.Append('[');
                    if (contents.StartsWith('!')) { expression.Append('^'); contents = contents[1..]; }
                    expression.Append(contents.Replace("\\", "\\\\")).Append(']');
                    i = close;
                    break;
                default: expression.Append(Regex.Escape(pattern[i].ToString())); break;
            }
        }
        expression.Append('$');
        return new Regex(expression.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
    }
}
