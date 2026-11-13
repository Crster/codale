using System.Text.RegularExpressions;

namespace Codale.Core.Shell;

/// <summary>
/// Telling a shell command from a plain-language request, for PowerShell, cmd and bash alike. It
/// reads the line with the same splitter and lexer as <see cref="ShellCommandClassifier.Classify"/>
/// and recognises the program against the same database, so the two never disagree on what a
/// command is.
/// </summary>
public static partial class ShellCommandClassifier
{
    // Built-ins that are not programs on PATH, so neither the database nor a PATH lookup finds them.
    private static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        // cmd
        "assoc", "break", "call", "cd", "chdir", "cls", "color", "copy", "date", "del", "dir", "echo", "endlocal",
        "erase", "for", "ftype", "goto", "md", "mkdir", "mklink", "move", "path", "pause", "popd", "prompt",
        "pushd", "rd", "rem", "ren", "rename", "rmdir", "set", "setlocal", "shift", "start", "time", "title",
        "type", "ver", "verify", "vol",
        // bash
        "alias", "bg", "bind", "cd", "compgen", "complete", "dirs", "disown", "eval", "export", "fg", "hash",
        "help", "history", "jobs", "let", "logout", "printf", "pwd", "read", "set", "shopt", "source", "test",
        "trap", "ulimit", "umask", "unalias", "unset", "wait",
        // PowerShell aliases beyond the classified ones
        "cat", "clear", "cp", "diff", "h", "kill", "ls", "man", "mv", "ps", "r", "rm", "sleep", "sc", "wget", "curl",
    };

    // Words a request is written with and a command line almost never is.
    private static readonly HashSet<string> RequestWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "all", "every", "my", "me", "please", "how", "what", "which", "whats", "that", "are", "is", "of",
        "with", "from", "into", "than", "larger", "bigger", "smaller", "files", "folders", "lines",
    };

    private static readonly Regex VerbNoun = new(@"^[A-Za-z]+-[A-Za-z][\w-]*$", RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="text"/> reads as a command for PowerShell, cmd or bash: a known
    /// program, cmdlet, alias, built-in, keyword or script path, an executable found in
    /// <paramref name="cwd"/> or on PATH, or a shell expression (variables, assignments, call
    /// operators). False for plain-language requests such as "list all files".
    /// </summary>
    public static bool IsShellCommand(string? text, string? cwd = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var line = text.Trim();
        if (line.EndsWith('?'))
        {
            return false;
        }

        // An apostrophe in a sentence ("what's here") leaves a quote open: a request, not a command.
        if ((Split(line, bashMode: false) ?? Split(line, bashMode: true)) is not { Segments.Count: > 0 } split)
        {
            return false;
        }

        var lexed = Lex(split.Segments[0].Text);
        var words = lexed.Words;

        // Leading VAR=value (bash), call and dot-source operators (PowerShell, bash's ".").
        var index = 0;
        while (index < words.Count && AssignmentWord.IsMatch(words[index]) && !words[index].StartsWith('$'))
        {
            index++;
        }

        if (index == words.Count)
        {
            return index > 0;
        }

        var first = words[index];
        if (first is "&" or "." && index + 1 < words.Count)
        {
            first = words[++index];
        }
        else if (first.Length > 1 && first[0] == '&')
        {
            first = first[1..];
        }

        if (first[0] is '$' or '(' or '[' or '@' or '%' or '{')
        {
            return true;
        }

        // A path to a program or script: .\build.ps1, ./run.sh, C:\tools\x.exe, "C:\Program Files\...".
        var bare = first.Trim('"', '\'');
        if (bare.Contains('\\') || bare.Contains('/') || (bare.Length > 1 && bare[1] == ':'))
        {
            return true;
        }

        var name = ProgramName(first);
        var known = IsKnownName(name) || VerbNoun.IsMatch(name) || ResolveExecutable(bare, cwd) is not null;
        if (!known)
        {
            return false;
        }

        // "find all json files": a program name opening a sentence. Shell syntax settles it as a command.
        var rest = words.Skip(index + 1).ToList();
        var looksLikeShell = rest.Any(w => w[0] is '-' or '/' or '$' or '.' || w.Contains('\\') || w.Contains('*') || w.Contains('=')) ||
                             split.Segments.Count > 1 || lexed.WritesFile is not null;
        return looksLikeShell || rest.Count(w => RequestWords.Contains(w.Trim(',', '.'))) < 2;
    }

    /// <summary>
    /// Drops a shell executable put in front of a command ("C:\...\pwsh.exe npx prisma ..." becomes
    /// "npx prisma ...") when the command already runs under a shell. A shell called with switches
    /// of its own (<c>pwsh -NoProfile -Command ...</c>, <c>bash -c ...</c>) is kept.
    /// </summary>
    public static string StripShellPrefix(string command)
    {
        var line = command.TrimStart();
        if (Split(line, bashMode: false) is not { Segments.Count: > 0 } split)
        {
            return command;
        }

        var words = Lex(split.Segments[0].Text).Words;
        if (words.Count < 2 || !Db.Value.Shells.Contains(ProgramName(words[0])) || words[1][0] is '-' or '/')
        {
            return command;
        }

        // Cut the shell word off the original text, so the rest keeps its quoting.
        var shellEnd = line[0] is '"' or '\'' ? line.IndexOf(line[0], 1) + 1 : line.IndexOfAny([' ', '\t']);
        return shellEnd <= 0 ? command : line[shellEnd..].TrimStart();
    }

    private static bool IsKnownName(string name)
    {
        var db = Db.Value;
        return db.Programs.ContainsKey(name) || db.Shells.Contains(name) || db.Transparent.Contains(name) ||
               Keywords.Contains(name) || Jumps.Contains(name) || PowerShellAliases.ContainsKey(name) ||
               UnknownCmdlets.Contains(name) || Builtins.Contains(name) ||
               (name.Contains('-') && ClassifyCmdlet(name) is not null);
    }

    /// <summary>The executable <paramref name="name"/> runs, looked up in <paramref name="cwd"/> then PATH with PATHEXT.</summary>
    private static string? ResolveExecutable(string name, string? cwd)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(".ps1");
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Prepend(cwd ?? "");

        foreach (var directory in directories)
        {
            if (directory.Length == 0)
            {
                continue;
            }

            try
            {
                if (Path.HasExtension(name) && File.Exists(Path.Combine(directory, name)))
                {
                    return Path.Combine(directory, name);
                }

                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(directory, name + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}
