using System.Text.RegularExpressions;

namespace Codale.Core.Shell;

/// <summary>The command typed on a shell prompt line, with the prompt taken off the front.</summary>
public static partial class PromptLine
{
    /// <summary>
    /// The prompts the shells Codale runs draw: PowerShell <c>PS C:\x&gt; </c>, cmd <c>C:\x&gt;</c>,
    /// bash-style <c>user@host:~/x$ </c> or <c>root# </c>. Anchored to the start, so a '&gt;' or '$'
    /// later in a command (a redirect, a variable) is left alone.
    /// </summary>
    [GeneratedRegex(@"^(?:PS [^>\r\n]*>|[A-Za-z]:[^>\r\n]*>|[^\s>$#%]*[$#%] |[^\s>]*> )[ \t]?", RegexOptions.CultureInvariant)]
    private static partial Regex Prompt();

    /// <summary>The command part of a prompt line as the screen shows it; empty when there is none.</summary>
    public static string Command(string line) => Prompt().Replace(line.Trim(), "", 1).Trim();
}
