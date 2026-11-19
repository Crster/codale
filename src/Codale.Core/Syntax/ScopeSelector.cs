namespace Codale.Core.Syntax;

/// <summary>Compiled TextMate scope selector: alternatives, ancestors, direct children and exclusions.</summary>
internal sealed class ScopeSelector
{
    private readonly string[] _path;
    private readonly bool[] _direct;
    private readonly ScopeSelector[] _exclude;

    private ScopeSelector(string[] path, bool[] direct, ScopeSelector[] exclude) => (_path, _direct, _exclude) = (path, direct, exclude);

    public static IEnumerable<ScopeSelector> Parse(string selector)
    {
        selector = string.Join(" ", selector.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var alternative in SplitAlternatives(selector))
        {
            var exclusions = SplitExclusions(alternative).ToArray();
            var text = exclusions[0].Trim();
            var excluded = exclusions.Skip(1).SelectMany(Parse).ToArray();
            // Expand positive groups separately: A - (B | C) excludes both B and C.
            var open = text.IndexOf('(');
            if (open >= 0)
            {
                var depth = 1;
                var close = open + 1;
                for (; close < text.Length && depth > 0; close++)
                    depth += text[close] == '(' ? 1 : text[close] == ')' ? -1 : 0;
                if (depth != 0) throw new InvalidDataException("Unbalanced theme scope selector.");
                foreach (var option in SplitAlternatives(text[(open + 1)..(close - 1)]))
                    foreach (var expanded in Parse(text[..open] + option + text[close..]))
                        yield return new ScopeSelector(expanded._path, expanded._direct, [.. expanded._exclude, .. excluded]);
                continue;
            }
            if (text.StartsWith("L:", StringComparison.Ordinal) || text.StartsWith("R:", StringComparison.Ordinal)) text = text[2..];
            var parts = text.Replace(">", " > ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var path = new List<string>();
            var direct = new List<bool>();
            var child = false;
            foreach (var part in parts)
            {
                if (part == ">") { child = true; continue; }
                path.Add(part);
                direct.Add(child);
                child = false;
            }
            if (path.Count > 0) yield return new ScopeSelector([.. path], [.. direct], excluded);
        }
    }

    private static IEnumerable<string> SplitExclusions(string selector)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i < selector.Length; i++)
        {
            if (selector[i] == '(') depth++;
            else if (selector[i] == ')') depth--;
            else if (depth == 0 && selector[i] == '-' && i > 0 && i + 1 < selector.Length && selector[i - 1] == ' ' && selector[i + 1] == ' ')
            {
                yield return selector[start..i];
                start = i + 1;
            }
        }
        yield return selector[start..];
    }

    private static IEnumerable<string> SplitAlternatives(string selector)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i < selector.Length; i++)
        {
            if (selector[i] == '(') depth++;
            else if (selector[i] == ')') depth--;
            else if (depth == 0 && selector[i] is ',' or '|')
            {
                yield return selector[start..i];
                start = i + 1;
            }
        }
        yield return selector[start..];
    }

    public int[]? Match(string[] scopes)
    {
        if (_exclude.Any(e => e.Match(scopes) is not null)) return null;
        for (var leaf = scopes.Length - 1; leaf >= 0; leaf--)
            if (Matches(_path[^1], scopes[leaf]) && Ancestors(scopes, _path.Length - 2, leaf - 1))
                return [leaf, Depth(_path[^1]), _path.Length - 1, .. _path.Reverse().Skip(1).Select(s => s.Length)];
        return null;
    }

    private bool Ancestors(string[] scopes, int pattern, int index)
    {
        if (pattern < 0) return true;
        var direct = _direct[pattern + 1];
        for (var i = index; i >= 0; i--)
        {
            if (Matches(_path[pattern], scopes[i]) && Ancestors(scopes, pattern - 1, i - 1)) return true;
            if (direct) break;
        }
        return false;
    }

    private static int Depth(string scope) => scope == "*" ? 0 : scope.Count(c => c == '.') + 1;
    private static bool Matches(string selector, string scope) => selector == "*"
        || scope == selector || scope.StartsWith(selector + ".", StringComparison.Ordinal);

    public static int Compare(int[] left, int[] right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            if (left[i] != right[i]) return left[i].CompareTo(right[i]);
        return left.Length.CompareTo(right.Length);
    }
}
