using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Codale.Search;

/// <summary>Recognizes file operands of common shell readers; never executes shell text.</summary>
public static class ShellReadFiles
{
    private static readonly Regex Tokens = new("\"([^\"]*)\"|'([^']*)'|([^\\s;|&(),]+)", RegexOptions.Compiled);
    private static readonly Regex Readers = new(@"\b(Get-Content|gc|cat|type|head|tail|sed|awk|rg|grep|Select-String|sls|read_text|read_bytes|readFileSync|readFile|open)\b", RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> Find(string root, string command, string? cwd, string? output)
    {
        if (!Readers.IsMatch(command) || Regex.IsMatch(command, @"\brg\s+--files\b", RegexOptions.IgnoreCase)) return [];
        root = Path.GetFullPath(root);
        var directory = string.IsNullOrWhiteSpace(cwd) ? root : Path.GetFullPath(cwd, root);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            try
            {
                var full = Path.GetFullPath(path, directory);
                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (relative == ".." || relative.StartsWith("../") || Path.IsPathRooted(relative)) return;
                if (File.Exists(full) && ProjectKnowledgeIndex.Eligible(relative)) found.Add(relative);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        }
        var tokens = Tokens.Matches(command).Select(m => m.Groups[1].Success ? m.Groups[1].Value :
            m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToArray();
        // Handle an explicit directory change before a reader, including quoted paths.
        for (var i = 0; i + 1 < tokens.Length; i++)
            if (tokens[i].Equals("cd", StringComparison.OrdinalIgnoreCase) || tokens[i].Equals("Set-Location", StringComparison.OrdinalIgnoreCase))
            {
                var target = tokens[i + 1];
                if (target.Equals("-LiteralPath", StringComparison.OrdinalIgnoreCase) && i + 2 < tokens.Length) target = tokens[i + 2];
                try { directory = Path.GetFullPath(target, directory); } catch (ArgumentException) { }
            }
        foreach (var token in tokens)
        {
            // Wrapped powershell/bash commands contain their operands inside one token.
            if (token != command && token.IndexOfAny([' ', '(', ';']) >= 0 && Readers.IsMatch(token))
            {
                foreach (var path in Find(root, token, directory, null)) found.Add(path);
                continue;
            }
            if (token.IndexOfAny(['*', '?']) >= 0 && !token.StartsWith('-'))
            {
                var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                matcher.AddInclude(token.Replace('\\', '/'));
                try
                {
                    foreach (var path in matcher.GetResultsInFullPath(directory).Take(100)) Add(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            else Add(token);
        }
        // Search results expose the files whose content the agent actually saw, even
        // when the command searched a directory or used a shell variable.
        foreach (var line in (output ?? "").Split('\n'))
        {
            var match = Regex.Match(line, @"^(.*?):\d+(?::\d+)?:");
            if (match.Success) Add(match.Groups[1].Value);
            else if (!string.IsNullOrWhiteSpace(line)) Add(line.Trim());
        }
        return [.. found];
    }
}
