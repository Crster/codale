using System.Text.RegularExpressions;

namespace Codale.Core.Shell;

/// <summary>A conservative subset of shell syntax that permits file inspection during planning.</summary>
public static class PlanningShellPolicy
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get-Content", "Get-ChildItem", "Get-Item", "Get-Location", "Test-Path", "Resolve-Path",
        "Select-String", "Select-Object", "Measure-Object", "Get-FileHash",
        "Format-Table", "Format-List", "Out-String",
        "rg", "grep", "cat", "head", "tail", "wc", "ls", "pwd",
    };

    /// <summary>Reads may proceed during planning; writes and plan review still need approval.</summary>
    public static bool AllowsToolInspection(string toolName, string? command = null) => toolName switch
    {
        "Read" or "Glob" or "Grep" or "LS" or "WebSearch" or "WebFetch" => true,
        "Bash" or "PowerShell" => AllowsInspection(command),
        _ => false,
    };

    /// <summary>
    /// Recognizes literal inspection commands and pipelines. Unknown programs,
    /// script expressions, redirection and shell escapes fail closed, regardless of risk rating.
    /// </summary>
    public static bool AllowsInspection(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        // Codex reports PowerShell's transport wrapper rather than just its script.
        var wrapper = Regex.Match(command.Trim(),
            "^(?:\"(?<exe>[^\"]+)\"|(?<exe>\\S+))\\s+-(?:Command|c)\\s+(?<body>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (wrapper.Success)
        {
            var executable = wrapper.Groups["exe"].Value.Replace('\\', '/').Split('/').Last();
            if (executable.ToLowerInvariant() is not ("pwsh" or "pwsh.exe" or "powershell" or "powershell.exe"))
                return false;
            command = wrapper.Groups["body"].Value.Trim();
            if (command.Length < 2 || command[0] is not ('\'' or '"') || command[^1] != command[0])
                return false;
            command = command[1..^1];
        }

        // Deliberately reject these even inside quotes: a rejected read can be rewritten
        // simply, while interpreting interpolation or nested scripts could enable writes.
        if (command.IndexOfAny(['$', '`', '(', ')', '{', '}', '<', '>', '&', '#', '@', '\0']) >= 0)
            return false;

        var quote = '\0';
        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c is '\'' or '"') quote = c;
        }
        if (quote != '\0') return false;

        var segments = ShellCommandAnalyzer.SplitSegments(command.ReplaceLineEndings("\n"));
        return segments.Count > 0 && segments.All(segment =>
        {
            var name = Regex.Match(segment, @"^[A-Za-z]+(?:-[A-Za-z]+)*(?=\s|$)");
            if (!name.Success) return false;

            // Git's other subcommands and global options can execute programs or write files.
            if (name.Value.Equals("git", StringComparison.OrdinalIgnoreCase))
                return Regex.IsMatch(segment, @"^git\s+(?:status|ls-files)(?:\s|$)", RegexOptions.IgnoreCase);

            // Ripgrep can launch a preprocessor or a hostname command even for a search.
            if (name.Value.Equals("rg", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(segment, @"--(?:pre|hostname-bin)(?:[=\s]|$)", RegexOptions.IgnoreCase))
                return false;

            return Commands.Contains(name.Value);
        });
    }
}
