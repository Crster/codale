using System.Text;
using System.Text.RegularExpressions;

namespace Codale.Search;

/// <summary>
/// The .gitignore rules in effect for one folder of the walk: the root's, then each nested
/// folder's on the way down. A rule from a nested file only applies under its own folder.
/// The last rule that matches decides, and a leading <c>!</c> re-includes.
/// </summary>
internal sealed class IgnoreRules
{
    public static readonly IgnoreRules Empty = new([]);

    private readonly IReadOnlyList<Rule> _rules;

    private IgnoreRules(IReadOnlyList<Rule> rules) => _rules = rules;

    private sealed record Rule(Regex Pattern, bool Negated, bool DirectoryOnly);

    /// <summary>The rules of <paramref name="directory"/>'s own .gitignore added to these, anchored at <paramref name="relative"/>.</summary>
    public IgnoreRules Extend(string directory, string relative)
    {
        var file = Path.Combine(directory, ".gitignore");
        if (!File.Exists(file))
        {
            return this;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return this;
        }

        var rules = new List<Rule>(_rules);
        foreach (var line in lines)
        {
            if (Parse(line, relative) is { } rule)
            {
                rules.Add(rule);
            }
        }

        return rules.Count == _rules.Count ? this : new IgnoreRules(rules);
    }

    /// <summary>True when a file or folder at <paramref name="relative"/> (from the root, with '/') is ignored.</summary>
    public bool IsIgnored(string relative, bool isDirectory)
    {
        var ignored = false;
        foreach (var rule in _rules)
        {
            if ((!rule.DirectoryOnly || isDirectory) && rule.Pattern.IsMatch(relative))
            {
                ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private static Rule? Parse(string raw, string baseRelative)
    {
        var line = raw.TrimEnd();
        if (line.Length == 0 || line[0] == '#')
        {
            return null;
        }

        var negated = false;
        if (line[0] == '!')
        {
            negated = true;
            line = line[1..];
        }
        else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
        {
            line = line[1..];
        }

        var directoryOnly = line.EndsWith('/');
        line = line.TrimEnd('/');
        if (line.Length == 0)
        {
            return null;
        }

        // A pattern with a slash in it is relative to the .gitignore's folder; one without matches at any depth.
        var anchored = line.Contains('/');
        line = line.TrimStart('/');

        var prefix = baseRelative.Length == 0 ? "" : Regex.Escape(baseRelative.Replace('\\', '/') + "/");
        var middle = anchored ? "" : "(?:.*/)?";

        try
        {
            var pattern = new Regex("^" + prefix + middle + GlobBody(line) + "$", RegexOptions.CultureInvariant);
            return new Rule(pattern, negated, directoryOnly);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Translates one gitignore glob to a regular expression body (no anchors).</summary>
    private static string GlobBody(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        // "**/" matches zero or more folders.
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i += 1;
                    }

                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[' when glob.IndexOf(']', i + 1) is var close and > 0:
                    var inner = glob[(i + 1)..close].Replace("\\", "\\\\");
                    if (inner.StartsWith('!'))
                    {
                        inner = "^" + inner[1..];
                    }

                    sb.Append('[').Append(inner).Append(']');
                    i = close;
                    break;
                case '\\' when i + 1 < glob.Length:
                    sb.Append(Regex.Escape(glob[i + 1].ToString()));
                    i++;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return sb.ToString();
    }
}
