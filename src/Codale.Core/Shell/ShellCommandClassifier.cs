using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codale.Core.Shell;

/// <summary>What a shell command does to the machine, lowest impact first.</summary>
public enum ShellClass
{
    /// <summary>Only reads or prints: listing, searching, git status/log/diff, version checks.</summary>
    ReadOnly,

    /// <summary>Builds, tests, restores or lints the project; writes only build output.</summary>
    Build,

    /// <summary>Creates or changes files, repository state, settings or installed software.</summary>
    Write,

    /// <summary>Removes files, branches or other data.</summary>
    Delete,

    /// <summary>An executable or construct the database does not know, so its effect cannot be judged.</summary>
    Unknown,

    /// <summary>Can damage the system or lose data beyond recovery, or reaches outside the project.</summary>
    Danger,
}

/// <summary>The class of a command with a one-phrase reason naming what decided it.</summary>
public sealed record ShellClassification(ShellClass Class, string Reason);

/// <summary>
/// Reads a shell command the way a shell would - quotes, chains, pipes, redirects, command
/// substitutions, wrapper shells (<c>pwsh -Command</c>, <c>bash -c</c>, <c>cmd /c</c>) - and
/// classifies each program it runs against the executable database in
/// <c>shell_commands.json</c>. The result is the highest class of any part. Whatever the parser
/// cannot read, or the database does not know, is <see cref="ShellClass.Unknown"/>, never safe.
/// </summary>
public static class ShellCommandClassifier
{
    private const int MaxDepth = 6;

    private sealed record Esc(string Flag, ShellClass Class, bool Bundled);

    private sealed class Entry
    {
        public ShellClass Class;
        public ShellClass? SubDefault;
        public Dictionary<string, ShellClass> Sub = new(StringComparer.OrdinalIgnoreCase);
        public List<Esc> Escalations = [];
        public Dictionary<string, List<Esc>> SubEscalations = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ShellClass> RunScripts = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Database
    {
        public Dictionary<string, Entry> Programs = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReadVerbs = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> WriteVerbs = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DeleteVerbs = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DangerVerbs = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Transparent = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Lazy<Database> Db = new(Load);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elif", "else", "elseif", "while", "until", "for", "foreach", "switch", "do", "then", "fi", "done", "esac",
        "try", "catch", "finally", "function", "param", "begin", "process", "end", "in", "case",
    };

    private static readonly HashSet<string> Jumps = new(StringComparer.OrdinalIgnoreCase)
    {
        "return", "break", "continue", "exit", "throw", "local", "declare", "typeset", "readonly",
    };

    // PowerShell's short aliases for the cmdlets the database knows by full name.
    private static readonly Dictionary<string, ShellClass> PowerShellAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gc"] = ShellClass.ReadOnly, ["gci"] = ShellClass.ReadOnly, ["gi"] = ShellClass.ReadOnly, ["gl"] = ShellClass.ReadOnly,
        ["gcm"] = ShellClass.ReadOnly, ["gps"] = ShellClass.ReadOnly, ["gsv"] = ShellClass.ReadOnly, ["gp"] = ShellClass.ReadOnly,
        ["sls"] = ShellClass.ReadOnly, ["select"] = ShellClass.ReadOnly, ["where"] = ShellClass.ReadOnly, ["foreach"] = ShellClass.ReadOnly,
        ["%"] = ShellClass.ReadOnly, ["?"] = ShellClass.ReadOnly, ["measure"] = ShellClass.ReadOnly, ["sort"] = ShellClass.ReadOnly,
        ["ft"] = ShellClass.ReadOnly, ["fl"] = ShellClass.ReadOnly, ["fw"] = ShellClass.ReadOnly, ["gm"] = ShellClass.ReadOnly,
        ["sl"] = ShellClass.ReadOnly, ["pushd"] = ShellClass.ReadOnly, ["popd"] = ShellClass.ReadOnly, ["gjb"] = ShellClass.ReadOnly,
        ["echo"] = ShellClass.ReadOnly, ["write"] = ShellClass.ReadOnly, ["oss"] = ShellClass.ReadOnly, ["ogv"] = ShellClass.ReadOnly,
        ["ni"] = ShellClass.Write, ["mi"] = ShellClass.Write, ["cpi"] = ShellClass.Write, ["rni"] = ShellClass.Write,
        ["si"] = ShellClass.Write, ["sp"] = ShellClass.Write, ["ac"] = ShellClass.Write, ["clc"] = ShellClass.Delete,
        ["ri"] = ShellClass.Delete, ["rp"] = ShellClass.Delete, ["rv"] = ShellClass.ReadOnly, ["sv"] = ShellClass.ReadOnly,
        ["iex"] = ShellClass.Danger, ["saps"] = ShellClass.Unknown, ["start"] = ShellClass.Unknown, ["icm"] = ShellClass.Unknown,
        ["ii"] = ShellClass.Unknown,
    };

    private static readonly HashSet<string> UnknownCmdlets = new(StringComparer.OrdinalIgnoreCase)
    {
        "start-process", "start-job", "start-transcript", "invoke-command", "invoke-item", "invoke-expression", "invoke-wmimethod",
        "invoke-cimmethod", "invoke-history", "import-module", "add-type", "new-object", "register-objectevent", "start-threadjob",
        "enter-pssession", "new-pssession", "set-psbreakpoint", "foreach-object-parallel",
    };

    private static readonly Regex QuotedText = new("'[^']*'|\"[^\"]*\"", RegexOptions.Compiled);
    private static readonly Regex AssignmentWord = new(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.Compiled);
    private static readonly Regex SimpleExpression = new(@"^\$[\w:{}]+(\.\w+|\[[^\]]*\])*$|^\[[\w.\[\],]+\]\$?[\w:]*$", RegexOptions.Compiled);
    private static readonly Regex IdentifierThenParen = new(@"(?<id>[A-Za-z_][\w.\-]*)$", RegexOptions.Compiled);

    /// <summary>The class of the whole command; the reason names the part that set it.</summary>
    public static ShellClassification Classify(string? command, string? cwd = null, IReadOnlyList<string>? extraRoots = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ShellClassification(ShellClass.Unknown, "no command text to check");
        }

        var text = command.Trim();
        var findings = ShellCommandAnalyzer.Analyze(text, cwd, extraRoots);
        if (findings.FirstOrDefault(f => f.Level == ShellRisk.Danger) is { } danger)
        {
            return new ShellClassification(ShellClass.Danger, danger.Reason);
        }

        var best = new ShellClassification(ShellClass.ReadOnly, "reads or prints only");
        foreach (var bashMode in new[] { false, true })
        {
            var result = ClassifyText(text, bashMode, 0);
            if (result.Class > best.Class)
            {
                best = result;
            }
        }

        // A script the token reader could not judge may still be a plain read-only PowerShell
        // tree (loops, variables, pipelines) that the syntax-tree validator proves harmless.
        if (best.Class == ShellClass.Unknown && PlanningShellPolicy.AllowsInspection(text))
        {
            return new ShellClassification(ShellClass.ReadOnly, "reads or prints only");
        }

        return best;
    }

    private static ShellClassification ClassifyText(string text, bool bashMode, int depth)
    {
        if (depth > MaxDepth)
        {
            return new ShellClassification(ShellClass.Unknown, "nested too deeply to check");
        }

        var split = Split(text, bashMode);
        if (split is null)
        {
            return new ShellClassification(ShellClass.Unknown, "has an unfinished quote or bracket");
        }

        var best = new ShellClassification(ShellClass.ReadOnly, "reads or prints only");
        void Take(ShellClassification candidate)
        {
            if (candidate.Class > best.Class)
            {
                best = candidate;
            }
        }

        foreach (var inner in split.Substitutions)
        {
            Take(ClassifyText(inner, bashMode, depth + 1));
        }

        foreach (var segment in split.Segments)
        {
            Take(ClassifySegment(segment, bashMode, depth));
        }

        return best;
    }

    private static ShellClassification ClassifySegment(Segment segment, bool bashMode, int depth)
    {
        var lexed = Lex(segment.Text);
        if (lexed.WritesFile is { } file)
        {
            return new ShellClassification(ShellClass.Write, $"redirects output into {file}");
        }

        var words = lexed.Words;
        if (words.Count == 0 || (words.Count == 1 && lexed.FirstQuoted))
        {
            return new ShellClassification(ShellClass.ReadOnly, "reads or prints only");
        }

        if (segment.HasInvocation || QuotedText.Replace(segment.Text, "").Contains("::", StringComparison.Ordinal))
        {
            return new ShellClassification(ShellClass.Unknown, "calls a method or .NET member");
        }

        return ClassifyWords(words, bashMode, depth, segment);
    }

    private static ShellClassification ClassifyWords(List<string> input, bool bashMode, int depth, Segment segment)
    {
        var words = new List<string>(input);

        // Leading VAR=value words and call operators do not change what runs.
        while (words.Count > 0 && AssignmentWord.IsMatch(words[0]) && !words[0].StartsWith('$'))
        {
            words.RemoveAt(0);
        }

        if (words.Count == 0)
        {
            return new ShellClassification(ShellClass.ReadOnly, "sets a variable");
        }

        var first = words[0];
        if (first is "&" or ".")
        {
            words.RemoveAt(0);
            if (words.Count == 0)
            {
                return new ShellClassification(ShellClass.ReadOnly, "reads or prints only");
            }

            first = words[0];
            if (first.StartsWith('$') || first.StartsWith('('))
            {
                return new ShellClassification(ShellClass.Unknown, "runs a program held in a variable");
            }
        }
        else if (first.StartsWith('&') && first.Length > 1 && !first.StartsWith("&>"))
        {
            words[0] = first = first[1..];
        }

        // $x = <command>, $env:A = 'b', $a += 1
        if (first.StartsWith('$'))
        {
            var eq = first.IndexOf('=');
            if (eq > 0 || (words.Count > 1 && words[1] is "=" or "+=" or "-="))
            {
                var rest = new List<string>();
                if (eq > 0 && eq < first.Length - 1)
                {
                    rest.Add(first[(eq + 1)..]);
                }

                rest.AddRange(words.Skip(eq > 0 ? 1 : 2));
                return rest.Count == 0
                    ? new ShellClassification(ShellClass.ReadOnly, "sets a variable")
                    : ClassifyWords(rest, bashMode, depth + 1, segment);
            }

            // $f in Get-ChildItem   (the head of a foreach)
            if (words.Count > 2 && words[1].Equals("in", StringComparison.OrdinalIgnoreCase))
            {
                return ClassifyWords(words.Skip(2).ToList(), bashMode, depth + 1, segment);
            }

            return SimpleExpression.IsMatch(first) || first.StartsWith("$_", StringComparison.Ordinal) || words.Count > 1 && words[1].StartsWith('-')
                ? new ShellClassification(ShellClass.ReadOnly, "evaluates an expression")
                : new ShellClassification(ShellClass.Unknown, "runs an expression the checker cannot read");
        }

        if (first.StartsWith('.') && first.Length > 1 && char.IsLetter(first[1]))
        {
            return new ShellClassification(ShellClass.ReadOnly, "reads a property of a value");
        }

        if (first.Length > 0 && (char.IsDigit(first[0]) || first[0] is '@' or '[' or '+') && !first.Contains('\\') && !first.Contains('/'))
        {
            return new ShellClassification(ShellClass.ReadOnly, "evaluates an expression");
        }

        if (Keywords.Contains(first))
        {
            words.RemoveAt(0);
            return words.Count == 0
                ? new ShellClassification(ShellClass.ReadOnly, "control flow")
                : ClassifyWords(words, bashMode, depth + 1, segment);
        }

        if (Jumps.Contains(first))
        {
            words.RemoveAt(0);
            return words.Count == 0 || first is "exit" or "local" or "declare" or "typeset" or "readonly" or "throw"
                ? new ShellClassification(ShellClass.ReadOnly, "control flow")
                : ClassifyWords(words, bashMode, depth + 1, segment);
        }

        var program = ProgramName(first);
        var args = words.Skip(1).ToList();

        // A lone quoted phrase is a string value, not a program to run.
        if (words.Count == 1 && first.Contains(' ') && !first.Contains('\\') && !first.Contains('/'))
        {
            return new ShellClassification(ShellClass.ReadOnly, "prints a string");
        }

        var db = Db.Value;

        // A shell run with a script: judge the script. An interactive shell or script file is unknown.
        if (db.Shells.Contains(program))
        {
            return ClassifyShellInvocation(program, args, bashMode, depth);
        }

        if (db.Transparent.Contains(program))
        {
            return ClassifyTransparent(program, args, bashMode, depth, segment);
        }

        if (program is "cmd")
        {
            return new ShellClassification(ShellClass.Unknown, "starts cmd without a command line");
        }

        if (args.Count == 1 && args[0] is "--version" or "-V" or "--help" && !db.Shells.Contains(program))
        {
            if (db.Programs.TryGetValue(program, out var known) && known.Class == ShellClass.Danger && program is "sudo" or "su" or "runas" or "gsudo" or "doas")
            {
                return new ShellClassification(ShellClass.Danger, "runs with elevated rights");
            }

            return new ShellClassification(ShellClass.ReadOnly, $"{program} prints its version or help");
        }

        if (db.Programs.TryGetValue(program, out var entry))
        {
            return ClassifyKnown(program, entry, args);
        }

        if (PowerShellAliases.TryGetValue(program, out var aliasClass))
        {
            return new ShellClassification(aliasClass, aliasClass == ShellClass.ReadOnly ? "reads or prints only" : $"{program} changes files or state");
        }

        if (program.Contains('-') && ClassifyCmdlet(program) is { } cmdlet)
        {
            return cmdlet;
        }

        // Relative scripts and local executables (./build.sh, .\tool.exe) are the project's own code.
        return new ShellClassification(ShellClass.Unknown, $"runs {Display(first)}, which is not a recognised command");
    }

    private static ShellClassification? ClassifyCmdlet(string name)
    {
        var db = Db.Value;
        if (UnknownCmdlets.Contains(name))
        {
            return new ShellClassification(name == "invoke-expression" ? ShellClass.Danger : ShellClass.Unknown, $"{name} runs other code");
        }

        if (db.ReadVerbs.Contains(name))
        {
            return new ShellClassification(ShellClass.ReadOnly, "reads or prints only");
        }

        if (db.DangerVerbs.Contains(name))
        {
            return new ShellClassification(ShellClass.Danger, $"{name} can damage the system");
        }

        if (db.DeleteVerbs.Contains(name))
        {
            return new ShellClassification(ShellClass.Delete, $"{name} removes data");
        }

        if (db.WriteVerbs.Contains(name))
        {
            return new ShellClassification(ShellClass.Write, $"{name} changes files or state");
        }

        var verb = name[..name.IndexOf('-')];
        if (db.ReadVerbs.Contains(verb))
        {
            return new ShellClassification(ShellClass.ReadOnly, "reads or prints only");
        }

        if (db.DeleteVerbs.Contains(verb))
        {
            return new ShellClassification(ShellClass.Delete, $"{name} removes data");
        }

        if (db.DangerVerbs.Contains(verb))
        {
            return new ShellClassification(ShellClass.Danger, $"{name} can damage the system");
        }

        if (db.WriteVerbs.Contains(verb))
        {
            return new ShellClassification(ShellClass.Write, $"{name} changes files or state");
        }

        return null;
    }

    private static ShellClassification ClassifyKnown(string program, Entry entry, List<string> args)
    {
        var cls = entry.Class;
        string? sub = null;
        var rest = args;

        if (entry.Sub.Count > 0)
        {
            var skip = program == "git" ? GitGlobalPrefix(args) : 0;
            var index = -1;
            for (var i = skip; i < args.Count; i++)
            {
                if (!args[i].StartsWith('-') || entry.Sub.ContainsKey(args[i]))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                sub = args[index];
                rest = args.Skip(index + 1).ToList();
                if (entry.Sub.TryGetValue(sub, out var subClass))
                {
                    cls = subClass;
                }
                else if (entry.SubDefault is { } fallback)
                {
                    cls = fallback;
                }
            }
        }
        else if (entry.RunScripts.Count > 0)
        {
            rest = args;
        }

        if (entry.RunScripts.Count > 0)
        {
            var scriptWords = sub is "run" or "run-script" ? rest : sub is not null && entry.Sub.ContainsKey(sub) ? null : args;
            if (scriptWords is not null)
            {
                var script = scriptWords.FirstOrDefault(a => !a.StartsWith('-'));
                cls = script is not null && entry.RunScripts.TryGetValue(script, out var scriptClass) ? scriptClass : ShellClass.Write;
            }
        }

        if (program == "git" && sub is not null && cls == ShellClass.ReadOnly && !GitReadGate(sub, rest))
        {
            cls = ShellClass.Write;
        }

        var all = args;
        var escalations = new List<Esc>(entry.Escalations);
        if (sub is not null && entry.SubEscalations.TryGetValue(sub, out var forSub))
        {
            escalations.AddRange(forSub);
        }

        var up = new List<ShellClass>();
        var down = new List<ShellClass>();
        foreach (var esc in escalations)
        {
            if (!Matches(esc, all, sub))
            {
                continue;
            }

            if (esc.Class > cls)
            {
                up.Add(esc.Class);
            }
            else if (esc.Class < cls)
            {
                down.Add(esc.Class);
            }
        }

        if (up.Count > 0)
        {
            cls = up.Max();
        }
        else if (down.Count > 0)
        {
            cls = down.Min();
        }

        var label = sub is not null && entry.Sub.Count > 0 ? $"{program} {sub}" : program;
        return new ShellClassification(cls, Describe(cls, label));
    }

    private static string Describe(ShellClass cls, string label) => cls switch
    {
        ShellClass.ReadOnly => "reads or prints only",
        ShellClass.Build => $"{label} builds or tests the project",
        ShellClass.Write => $"{label} changes files, repository state or settings",
        ShellClass.Delete => $"{label} deletes data",
        ShellClass.Danger => $"{label} can damage the system or lose data",
        _ => $"{label} is not a recognised command",
    };

    private static int GitGlobalPrefix(List<string> args)
    {
        var i = 0;
        while (i < args.Count && args[i].StartsWith('-'))
        {
            i += args[i] is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" or "--exec-path" or "--super-prefix" ? 2 : 1;
        }

        return Math.Min(i, args.Count);
    }

    // git subcommands that both read and write depending on their arguments.
    private static bool GitReadGate(string sub, List<string> rest)
    {
        var positional = rest.Where(a => !a.StartsWith('-')).ToList();
        bool Has(params string[] flags) => rest.Any(a => flags.Contains(a, StringComparer.Ordinal) || flags.Any(f => f.StartsWith("--") && a.StartsWith(f + "=", StringComparison.Ordinal)));
        return sub.ToLowerInvariant() switch
        {
            "branch" => positional.Count == 0 || Has("-l", "--list", "--contains", "--merged", "--no-merged", "--points-at", "-a", "-r", "-v", "-vv"),
            "tag" => positional.Count == 0 || Has("-l", "--list", "--contains", "--points-at", "--merged", "--no-merged", "-n", "-v"),
            "remote" => positional.Count == 0 || positional[0] is "show" or "get-url",
            "config" => Has("--get", "--get-all", "--get-regexp", "--list", "-l", "--show-origin", "--show-scope", "--get-urlmatch"),
            "stash" => positional.Count > 0 && positional[0] is "list" or "show",
            "worktree" => positional.Count > 0 && positional[0] is "list",
            "submodule" => positional.Count == 0 || positional[0] is "status" or "summary",
            "notes" => positional.Count == 0 || positional[0] is "list" or "show",
            "reflog" => positional.Count == 0 || positional[0] is "show" or "exists",
            _ => true,
        };
    }

    private static bool Matches(Esc esc, List<string> args, string? sub)
    {
        var flag = esc.Flag;
        if (flag.Contains(' '))
        {
            return string.Join(' ', args).Contains(flag, StringComparison.OrdinalIgnoreCase);
        }

        var comparison = flag.Length == 2 && flag[0] == '-' ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var isDash = flag.StartsWith('-') || flag.StartsWith('/');
        var scope = isDash ? args : args.Where(a => !a.StartsWith('-')).Take(4).ToList();
        foreach (var arg in scope)
        {
            if (flag.EndsWith('-') && !isDash ? arg.StartsWith(flag, comparison) : arg.Equals(flag, comparison))
            {
                return true;
            }

            if (isDash && flag.StartsWith("--") && arg.StartsWith(flag + "=", comparison))
            {
                return true;
            }

            if (esc.Bundled && flag.Length == 2 && arg.Length > 2 && arg[0] == '-' && arg[1] != '-' && !arg.Contains('=') &&
                arg.AsSpan(1).Contains(flag[1]))
            {
                return true;
            }
        }

        return false;
    }

    private static ShellClassification ClassifyTransparent(string program, List<string> args, bool bashMode, int depth, Segment segment)
    {
        var rest = new List<string>(args);
        switch (program.ToLowerInvariant())
        {
            case "env":
                while (rest.Count > 0 && (rest[0].StartsWith('-') || AssignmentWord.IsMatch(rest[0])))
                {
                    rest.RemoveAt(0);
                }

                if (rest.Count == 0)
                {
                    return new ShellClassification(ShellClass.ReadOnly, "prints the environment");
                }

                break;
            case "timeout":
                while (rest.Count > 0 && rest[0].StartsWith('-'))
                {
                    rest.RemoveAt(0);
                }

                if (rest.Count > 0)
                {
                    rest.RemoveAt(0);
                }

                break;
            case "xargs":
                while (rest.Count > 0 && rest[0].StartsWith('-'))
                {
                    var takesValue = rest[0] is "-I" or "-n" or "-P" or "-d" or "-L" or "-s" or "-E" or "-a" or "--max-args" or "--max-procs" or "--delimiter";
                    rest.RemoveAt(0);
                    if (takesValue && rest.Count > 0)
                    {
                        rest.RemoveAt(0);
                    }
                }

                if (rest.Count == 0)
                {
                    return new ShellClassification(ShellClass.ReadOnly, "prints its input");
                }

                break;
            default:
                while (rest.Count > 0 && rest[0].StartsWith('-'))
                {
                    rest.RemoveAt(0);
                }

                if (rest.Count == 0)
                {
                    return new ShellClassification(ShellClass.ReadOnly, "reads or prints only");
                }

                break;
        }

        return ClassifyWords(rest, bashMode, depth + 1, segment);
    }

    private static ShellClassification ClassifyShellInvocation(string shell, List<string> args, bool bashMode, int depth)
    {
        string? script = null;
        var lowered = shell.ToLowerInvariant();
        if (lowered is "pwsh" or "powershell")
        {
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i].ToLowerInvariant();
                if (arg is "-encodedcommand" or "-enc" or "-ec" or "-e")
                {
                    return new ShellClassification(ShellClass.Danger, "runs an encoded, unreadable command");
                }

                if (arg is "-file" or "-f")
                {
                    return new ShellClassification(ShellClass.Unknown, "runs a script file");
                }

                if (arg is "-command" or "-c")
                {
                    script = string.Join(' ', args.Skip(i + 1));
                    break;
                }
            }
        }
        else if (lowered == "cmd")
        {
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i].Equals("/c", StringComparison.OrdinalIgnoreCase) || args[i].Equals("/k", StringComparison.OrdinalIgnoreCase))
                {
                    script = string.Join(' ', args.Skip(i + 1));
                    break;
                }
            }
        }
        else if (lowered == "wsl")
        {
            var rest = args.SkipWhile(a => a.StartsWith('-') && a is not "--").ToList();
            if (rest.Count > 0 && rest[0] == "--")
            {
                rest.RemoveAt(0);
            }

            if (rest.Count > 1 && Db.Value.Shells.Contains(ProgramName(rest[0])))
            {
                return ClassifyShellInvocation(ProgramName(rest[0]), rest.Skip(1).ToList(), true, depth + 1);
            }

            script = rest.Count > 0 ? string.Join(' ', rest) : null;
        }
        else
        {
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] == "-c" || (args[i].StartsWith('-') && !args[i].StartsWith("--") && args[i].EndsWith('c') && args[i].Length <= 4))
                {
                    script = i + 1 < args.Count ? args[i + 1] : null;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(script))
        {
            return new ShellClassification(ShellClass.Unknown, $"starts {shell} without a command line");
        }

        return ClassifyText(script, lowered is not ("pwsh" or "powershell" or "cmd"), depth + 1);
    }

    private static string Display(string word) => word.Length > 40 ? word[..40] + "..." : word;

    /// <summary>"C:\tools\Git.EXE" -> "git"; extensions that only mark an executable are dropped.</summary>
    private static string ProgramName(string word)
    {
        var name = word.Trim('"', '\'', '`', '(', '{');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".com", ".ps1" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && name.Length > extension.Length)
            {
                name = name[..^extension.Length];
                break;
            }
        }

        return name.ToLowerInvariant();
    }

    // ---- reading the command line ----

    private sealed record Segment(string Text, bool HasInvocation);

    private sealed record SplitResult(List<Segment> Segments, List<string> Substitutions);

    /// <summary>
    /// Splits into the commands that run: separators outside quotes (<c>| || &amp;&amp; ; &amp;</c>, line breaks,
    /// parentheses and braces), the text of <c>$(...)</c>, backtick and <c>&lt;(...)</c> substitutions, with
    /// comments, here-documents and here-strings skipped. Null when a quote or bracket never closes.
    /// </summary>
    private static SplitResult? Split(string text, bool bashMode)
    {
        var segments = new List<Segment>();
        var subs = new List<string>();
        var buffer = new StringBuilder();
        var invoked = false;
        var i = 0;

        void Flush()
        {
            var piece = buffer.ToString().Trim();
            if (piece.Length > 0)
            {
                segments.Add(new Segment(piece, invoked));
            }

            buffer.Clear();
            invoked = false;
        }

        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c is '\'' or '"')
            {
                var end = ReadQuoted(text, i, bashMode, subs);
                if (end < 0)
                {
                    return null;
                }

                buffer.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (c == '@' && next is '\'' or '"')
            {
                var j = i + 2;
                while (j < text.Length && text[j] is ' ' or '\t' or '\r')
                {
                    j++;
                }

                if (j < text.Length && text[j] == '\n')
                {
                    var closer = "\n" + next + "@";
                    var close = text.IndexOf(closer, j, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return null;
                    }

                    buffer.Append("''");
                    i = close + closer.Length;
                    continue;
                }
            }

            if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] is ';' or '|' or '&' or '(' or '{'))
            {
                var eol = text.IndexOf('\n', i);
                i = eol < 0 ? text.Length : eol;
                continue;
            }

            if (c == '<' && next == '#')
            {
                var close = text.IndexOf("#>", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return null;
                }

                i = close + 2;
                continue;
            }

            if (c == '<' && next == '<' && i + 2 < text.Length && text[i + 2] != '<')
            {
                var match = Regex.Match(text[(i + 2)..], @"^-?\s*(['""]?)([A-Za-z_][\w]*)\1");
                if (match.Success)
                {
                    var delimiter = match.Groups[2].Value;
                    var lineEnd = text.IndexOf('\n', i);
                    if (lineEnd < 0)
                    {
                        buffer.Append(text, i, text.Length - i);
                        i = text.Length;
                        continue;
                    }

                    buffer.Append(text, i, lineEnd - i);
                    var bodyEnd = Regex.Match(text[lineEnd..], @"\n[ \t]*" + Regex.Escape(delimiter) + @"[ \t]*(?=\r?\n|$)");
                    i = bodyEnd.Success ? lineEnd + bodyEnd.Index + bodyEnd.Length : text.Length;
                    continue;
                }
            }

            if (c == '$' && next == '(' || (c is '<' or '>') && next == '(' && bashMode)
            {
                var close = MatchingParen(text, i + 1);
                if (close < 0)
                {
                    return null;
                }

                subs.Add(text.Substring(i + 2, close - i - 2));
                buffer.Append(" _ ");
                i = close + 1;
                continue;
            }

            if (c == '`')
            {
                if (bashMode)
                {
                    var close = text.IndexOf('`', i + 1);
                    if (close < 0)
                    {
                        return null;
                    }

                    subs.Add(text.Substring(i + 1, close - i - 1));
                    buffer.Append(" _ ");
                    i = close + 1;
                    continue;
                }

                buffer.Append(c);
                if (i + 1 < text.Length)
                {
                    buffer.Append(text[i + 1]);
                }

                i += 2;
                continue;
            }

            if (bashMode && c == '\\' && i + 1 < text.Length)
            {
                if (next == '\n')
                {
                    i += 2;
                    continue;
                }

                buffer.Append(c).Append(next);
                i += 2;
                continue;
            }

            if (c == '(')
            {
                // name( is a call; a keyword( or a bare ( is grouping.
                var head = IdentifierThenParen.Match(buffer.ToString());
                if (head.Success && !Keywords.Contains(head.Groups["id"].Value) && !Jumps.Contains(head.Groups["id"].Value) &&
                    !head.Groups["id"].Value.StartsWith('-') && buffer.Length > 0 && !char.IsWhiteSpace(buffer[^1]))
                {
                    invoked = true;
                    buffer.Append(c);
                    i++;
                    continue;
                }

                Flush();
                i++;
                continue;
            }

            if (c is ')' or '{' or '}' or ';' or '\n' or '\r')
            {
                // ${name} and @{...} stay part of their word.
                if (c is '{' && buffer.Length > 0 && buffer[^1] is '$' or '@')
                {
                    buffer.Append(c);
                    i++;
                    continue;
                }

                Flush();
                i++;
                continue;
            }

            if (c == '|')
            {
                Flush();
                i += next == '|' ? 2 : 1;
                continue;
            }

            if (c == '&')
            {
                if (next == '&')
                {
                    Flush();
                    i += 2;
                    continue;
                }

                var previous = buffer.Length > 0 ? buffer[^1] : ' ';
                if (previous == '>' || next == '>')
                {
                    buffer.Append(c);
                    i++;
                    continue;
                }

                if (buffer.ToString().Trim().Length == 0)
                {
                    buffer.Append(c).Append(' ');
                    i++;
                    continue;
                }

                Flush();
                i++;
                continue;
            }

            buffer.Append(c);
            i++;
        }

        Flush();
        return new SplitResult(segments, subs);
    }

    /// <summary>Index just past the closing quote; substitutions inside double quotes are collected. -1 when unterminated.</summary>
    private static int ReadQuoted(string text, int start, bool bashMode, List<string> subs)
    {
        var quote = text[start];
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == quote)
            {
                if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'' && !bashMode)
                {
                    i += 2;
                    continue;
                }

                if (quote == '"' && i + 1 < text.Length && text[i + 1] == '"' && !bashMode)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (quote == '"')
            {
                if (bashMode && c == '\\' && i + 1 < text.Length)
                {
                    i += 2;
                    continue;
                }

                if (!bashMode && c == '`' && i + 1 < text.Length)
                {
                    i += 2;
                    continue;
                }

                if (c == '$' && i + 1 < text.Length && text[i + 1] == '(')
                {
                    var close = MatchingParen(text, i + 1);
                    if (close < 0)
                    {
                        return -1;
                    }

                    subs.Add(text.Substring(i + 2, close - i - 2));
                    i = close + 1;
                    continue;
                }

                if (bashMode && c == '`')
                {
                    var close = text.IndexOf('`', i + 1);
                    if (close < 0)
                    {
                        return -1;
                    }

                    subs.Add(text.Substring(i + 1, close - i - 1));
                    i = close + 1;
                    continue;
                }
            }

            i++;
        }

        return -1;
    }

    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\'' or '"')
            {
                var dummy = new List<string>();
                var end = ReadQuoted(text, i, true, dummy);
                if (end < 0)
                {
                    return -1;
                }

                i = end - 1;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record Lexed(List<string> Words, string? WritesFile, bool FirstQuoted);

    /// <summary>Words of one command with quotes removed, and the first file a redirect would write.</summary>
    private static Lexed Lex(string segment)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var hasWord = false;
        string? writes = null;
        var firstQuoted = false;
        var i = 0;

        void End()
        {
            if (hasWord)
            {
                words.Add(word.ToString());
            }

            word.Clear();
            hasWord = false;
        }

        while (i < segment.Length)
        {
            var c = segment[i];
            if (c is '\'' or '"')
            {
                firstQuoted |= words.Count == 0 && !hasWord;
                hasWord = true;
                var close = segment.IndexOf(c, i + 1);
                while (close >= 0 && close + 1 < segment.Length && segment[close + 1] == c)
                {
                    close = segment.IndexOf(c, close + 2);
                }

                var end = close < 0 ? segment.Length : close;
                word.Append(segment, i + 1, end - i - 1);
                i = Math.Min(segment.Length, end + 1);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                End();
                i++;
                continue;
            }

            if (c == '>')
            {
                // An attached fd number (2>) belongs to the redirect, not to a word.
                if (hasWord && word.ToString().All(char.IsDigit))
                {
                    word.Clear();
                    hasWord = false;
                }
                else if (hasWord && word.ToString() is "&")
                {
                    word.Clear();
                    hasWord = false;
                }
                else
                {
                    End();
                }

                i++;
                while (i < segment.Length && segment[i] is '>' or '|')
                {
                    i++;
                }

                var toFd = i < segment.Length && segment[i] == '&';
                if (toFd)
                {
                    i++;
                }

                while (i < segment.Length && char.IsWhiteSpace(segment[i]))
                {
                    i++;
                }

                var start = i;
                if (i < segment.Length && segment[i] is '\'' or '"')
                {
                    var quote = segment[i];
                    var close = segment.IndexOf(quote, i + 1);
                    i = close < 0 ? segment.Length : close + 1;
                }
                else
                {
                    while (i < segment.Length && !char.IsWhiteSpace(segment[i]))
                    {
                        i++;
                    }
                }

                var target = segment[start..i].Trim('\'', '"');
                var harmless = toFd || target.Equals("$null", StringComparison.OrdinalIgnoreCase) ||
                               target.Equals("nul", StringComparison.OrdinalIgnoreCase) || target == "/dev/null" ||
                               target is "&1" or "&2" or "-" || target.Length > 0 && target[0] == '&';
                if (!harmless)
                {
                    writes ??= target.Length == 0 ? "a file" : target;
                }

                continue;
            }

            if (c == '<')
            {
                End();
                i++;
                while (i < segment.Length && segment[i] is '<' or '=')
                {
                    i++;
                }

                while (i < segment.Length && char.IsWhiteSpace(segment[i]))
                {
                    i++;
                }

                while (i < segment.Length && !char.IsWhiteSpace(segment[i]))
                {
                    i++;
                }

                continue;
            }

            hasWord = true;
            word.Append(c);
            i++;
        }

        End();
        return new Lexed(words, writes, firstQuoted);
    }

    // ---- database ----

    private static Database Load()
    {
        using var stream = typeof(ShellCommandClassifier).Assembly.GetManifestResourceStream("shell_commands.json")
            ?? throw new InvalidOperationException("The shell command database is not embedded.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var db = new Database();

        foreach (var (verbs, set) in new[]
        {
            (root.GetProperty("verbs").GetProperty("readonly"), db.ReadVerbs),
            (root.GetProperty("verbs").GetProperty("write"), db.WriteVerbs),
            (root.GetProperty("verbs").GetProperty("delete"), db.DeleteVerbs),
            (root.GetProperty("verbs").GetProperty("danger"), db.DangerVerbs),
        })
        {
            foreach (var verb in verbs.EnumerateArray())
            {
                set.Add(verb.GetString()!);
            }
        }

        var wrappers = root.GetProperty("wrappers");
        foreach (var shell in wrappers.GetProperty("shells").EnumerateArray())
        {
            db.Shells.Add(shell.GetString()!);
        }

        foreach (var wrapper in wrappers.GetProperty("transparent").EnumerateArray())
        {
            db.Transparent.Add(wrapper.GetString()!);
        }

        var aliases = new List<(string Name, string Target)>();
        foreach (var program in root.GetProperty("programs").EnumerateObject())
        {
            if (program.Value.TryGetProperty("alias", out var alias))
            {
                aliases.Add((program.Name, alias.GetString()!));
                continue;
            }

            db.Programs[program.Name] = ReadEntry(program.Value);
        }

        foreach (var (name, target) in aliases)
        {
            db.Programs[name] = db.Programs[target];
        }

        return db;
    }

    private static Entry ReadEntry(JsonElement node)
    {
        var entry = new Entry { Class = ParseClass(node.GetProperty("c").GetString()!) };
        if (node.TryGetProperty("subDefault", out var subDefault))
        {
            entry.SubDefault = ParseClass(subDefault.GetString()!);
        }

        if (node.TryGetProperty("sub", out var sub))
        {
            foreach (var item in sub.EnumerateObject())
            {
                entry.Sub[item.Name] = ParseClass(item.Value.GetString()!);
            }
        }

        if (node.TryGetProperty("esc", out var esc))
        {
            entry.Escalations = ReadEscalations(esc);
        }

        if (node.TryGetProperty("subEsc", out var subEsc))
        {
            foreach (var item in subEsc.EnumerateObject())
            {
                entry.SubEscalations[item.Name] = ReadEscalations(item.Value);
            }
        }

        if (node.TryGetProperty("runScripts", out var scripts))
        {
            foreach (var item in scripts.EnumerateObject())
            {
                entry.RunScripts[item.Name] = ParseClass(item.Value.GetString()!);
            }
        }

        return entry;
    }

    private static List<Esc> ReadEscalations(JsonElement array) =>
        [.. array.EnumerateArray().Select(e => new Esc(
            e.GetProperty("f").GetString()!,
            ParseClass(e.GetProperty("c").GetString()!),
            e.TryGetProperty("b", out var bundled) && bundled.GetBoolean()))];

    private static ShellClass ParseClass(string name) => name.ToLowerInvariant() switch
    {
        "readonly" => ShellClass.ReadOnly,
        "build" => ShellClass.Build,
        "write" => ShellClass.Write,
        "delete" => ShellClass.Delete,
        "danger" => ShellClass.Danger,
        _ => ShellClass.Unknown,
    };
}
