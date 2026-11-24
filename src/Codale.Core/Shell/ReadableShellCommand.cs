using System.Text;
using System.Text.RegularExpressions;

namespace Codale.Core.Shell;

/// <summary>Which shell's quoting and escaping rules a command line follows.</summary>
public enum ShellDialect
{
    /// <summary>Guess from the command: PowerShell when it uses cmdlets or <c>$env:</c>, else POSIX.</summary>
    Auto,
    Posix,
    PowerShell,
    Cmd,
}

/// <summary>What a piece of a readable command is; the UI picks its colour from this.</summary>
public enum CommandPartKind
{
    /// <summary>Spacing and anything without a role.</summary>
    Plain,

    /// <summary>The executable being run, as its bare name: <c>git</c>, <c>dotnet</c>, <c>pwsh</c>.</summary>
    Program,

    /// <summary>The action a developer CLI is asked for: <c>build</c> in <c>dotnet build</c>.</summary>
    Subcommand,

    /// <summary>Shell keywords (<c>if</c>, <c>for</c>, <c>foreach</c>) and wrappers such as <c>sudo</c>.</summary>
    Keyword,

    /// <summary>An option: <c>-c</c>, <c>--force</c>, <c>-Recurse</c>.</summary>
    Flag,

    /// <summary>A plain argument.</summary>
    Value,

    /// <summary>A file, folder, glob or URL argument.</summary>
    Path,

    /// <summary>The content of a quoted string, its quotes and escapes removed.</summary>
    Text,

    /// <summary>The typographic quotes put around a string that holds spaces.</summary>
    Quote,

    /// <summary><c>$name</c>, <c>$env:PATH</c>, <c>$(…)</c>, and <c>NAME=</c> assignments.</summary>
    Variable,

    /// <summary>Brackets and operators inside a block or subexpression.</summary>
    Punctuation,

    /// <summary><c>&gt;</c>, <c>2&gt;&amp;1</c>, <c>&lt;</c> and the like.</summary>
    Redirect,

    /// <summary>A <c>#</c> comment.</summary>
    Comment,

    /// <summary>A plain-English remark the formatter adds, such as "in the background".</summary>
    Note,
}

public readonly record struct CommandPart(string Text, CommandPartKind Kind);

/// <summary>One command of a readable command: how it joins the previous one, and its parts.</summary>
public sealed class ReadableCommandLine
{
    /// <summary>"then", "otherwise", "piped to" - or empty for a line that simply follows.</summary>
    public string Connector { get; internal set; } = "";

    /// <summary>Leading spaces kept from a multi-line script, or added for heredoc text.</summary>
    public string Indent { get; internal set; } = "";

    public List<CommandPart> Parts { get; } = [];

    public bool IsEmpty => Parts.Count == 0;

    public override string ToString()
    {
        var builder = new StringBuilder(Connector.Length > 0 ? Connector + " " : Indent);
        foreach (var part in Parts)
        {
            builder.Append(part.Text);
        }

        return builder.ToString();
    }
}

/// <summary>A command line laid out for reading: one command per line, each word tagged with its role.</summary>
public sealed class ReadableCommand
{
    /// <summary>The shell a wrapper such as <c>pwsh -Command '…'</c> ran the command in; empty when there was none.</summary>
    public string Shell { get; init; } = "";

    public ShellDialect Dialect { get; init; }

    public IReadOnlyList<ReadableCommandLine> Lines { get; init; } = [];

    public override string ToString() => string.Join('\n', Lines);
}

/// <summary>
/// Turns a raw command line into something a person reads at a glance. A transport wrapper
/// (<c>pwsh.exe -Command "…"</c>, <c>bash -lc '…'</c>, <c>cmd /c …</c>) is peeled off, quotes and
/// escapes are resolved so strings read as the text they are, chains are split one command per
/// line with "then" / "otherwise" / "piped to" in place of <c>&amp;&amp;</c> / <c>||</c> / <c>|</c>,
/// and each word is tagged: the program, its subcommand, flags, paths, strings and variables.
/// Like the highlighter it feeds, it is not a parser: it must be right often enough to read well
/// and never throw on input it does not understand.
/// </summary>
public static partial class ReadableShellCommand
{
    // Wrappers whose next word is the program that actually runs.
    private static readonly HashSet<string> Wrappers =
        [with(StringComparer.OrdinalIgnoreCase), "sudo", "nohup", "exec", "time", "nice", "env", "command", "builtin"];

    private static readonly HashSet<string> Keywords =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "if", "then", "elif", "else", "fi", "for", "while", "until", "do", "done", "case", "esac", "function",
        "elseif", "foreach", "switch", "try", "catch", "finally", "param", "return", "throw", "break", "continue",
    ];

    // Keywords followed by a name or list rather than by a command.
    private static readonly HashSet<string> NameAfter =
        [with(StringComparer.OrdinalIgnoreCase), "for", "foreach", "case", "function", "param", "return", "throw", "switch"];

    // Programs whose first word(s) name the action, and how many such words they take.
    private static readonly Dictionary<string, int> SubcommandPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["git"] = 1, ["dotnet"] = 1, ["npm"] = 1, ["pnpm"] = 1, ["yarn"] = 1, ["bun"] = 1, ["npx"] = 1, ["bunx"] = 1,
        ["pnpx"] = 1, ["cargo"] = 1, ["go"] = 1, ["docker"] = 1, ["kubectl"] = 1, ["uv"] = 1, ["pip"] = 1, ["pip3"] = 1,
        ["poetry"] = 1, ["rustup"] = 1, ["brew"] = 1, ["winget"] = 1, ["choco"] = 1, ["apt"] = 1, ["apt-get"] = 1,
        ["helm"] = 1, ["terraform"] = 1, ["make"] = 1, ["mvn"] = 1, ["gradle"] = 1, ["deno"] = 1, ["code"] = 0,
        ["gh"] = 2, ["az"] = 2, ["gcloud"] = 2, ["aws"] = 2, ["claude"] = 1, ["codex"] = 1,
    };

    // Subcommands that are followed by one more naming word: npm run build, docker compose up, git stash pop.
    private static readonly HashSet<string> NestedSubcommands =
        [with(StringComparer.OrdinalIgnoreCase), "run", "exec", "dlx", "x", "compose", "ef", "tool", "stash", "remote", "submodule", "worktree", "workspace"];

    [GeneratedRegex(
        """^(?<pre>["']?(?:[A-Za-z]:[\\/][^"'|&;\n]*[\\/]|[^\s"'|&;]*[\\/])?)(?<name>pwsh|powershell|bash|sh|zsh|cmd)(?:\.exe)?["']?\s+(?:-[\w-]+(?:\s+(?!-)[A-Za-z]\w*)?\s+)*?(?:-command|-c|-lc|-ic|-lic|/s\s+/c|/c)\s+(?<body>.+)$""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ShellWrapper();

    [GeneratedRegex(@"\$env:|\$_\b|\$null\b|\$true\b|\$false\b|(?:^|[\s|;({])[A-Z][a-z]+-[A-Z][A-Za-z]+\b|@'\n|@""\n|\s-(?:eq|ne|gt|lt|ge|le|like|match|and|or|not)\s")]
    private static partial Regex PowerShellHint();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^(?:\.{1,2}|~)(?:[\\/]|$)|[\\/]|^https?://|^[\w*?\[\]{},-]+\.[A-Za-z][A-Za-z0-9]{0,7}$|^[\w-]*\*[\w.*-]*$")]
    private static partial Regex PathLike();

    [GeneratedRegex(@"^[A-Za-z][\w:.+-]*$")]
    private static partial Regex SubcommandWord();

    [GeneratedRegex(@"^-?\d+(?:\.\d+)?$")]
    private static partial Regex NumberWord();

    [GeneratedRegex(@"^<<(-?)\s*(['""]?)([A-Za-z_][\w-]*)\2")]
    private static partial Regex HeredocStart();

    /// <summary>Lays <paramref name="command"/> out for reading; see <see cref="ReadableShellCommand"/>.</summary>
    public static ReadableCommand Parse(string? command, ShellDialect dialect = ShellDialect.Auto)
    {
        var text = (command ?? "").ReplaceLineEndings("\n").Trim();
        var shell = "";
        if (Unwrap(text) is { } unwrapped)
        {
            (text, shell, dialect) = unwrapped;
        }

        if (dialect == ShellDialect.Auto)
        {
            dialect = PowerShellHint().IsMatch(text) ? ShellDialect.PowerShell : ShellDialect.Posix;
        }

        var items = new Scanner(text, dialect).Run();
        return new ReadableCommand { Shell = shell, Dialect = dialect, Lines = Build(items, dialect) };
    }

    /// <summary>The command inside a <c>pwsh -Command</c> / <c>bash -c</c> / <c>cmd /c</c> wrapper, with the wrapper's own quoting undone.</summary>
    private static (string Body, string Shell, ShellDialect Dialect)? Unwrap(string command)
    {
        var match = ShellWrapper().Match(command);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value.ToLowerInvariant();
        var (shell, dialect) = name switch
        {
            "pwsh" or "powershell" => ("PowerShell", ShellDialect.PowerShell),
            "cmd" => ("cmd", ShellDialect.Cmd),
            _ => (name, ShellDialect.Posix),
        };

        var body = match.Groups["body"].Value.Trim();
        if (body.Length >= 2 && body[0] is '"' or '\'' && body[^1] == body[0] && ClosingQuote(body, dialect) == body.Length - 1)
        {
            body = Unquote(body[1..^1], body[0], dialect);
        }

        return body.Length == 0 ? null : (body.Trim(), shell, dialect);
    }

    /// <summary>Index of the quote that closes the one at index 0, honouring the dialect's escapes.</summary>
    private static int ClosingQuote(string text, ShellDialect dialect)
    {
        var quote = text[0];
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '"' && (c == '\\' || (c == '`' && dialect == ShellDialect.PowerShell)) && i + 1 < text.Length)
            {
                i++;
            }
            else if (c == quote)
            {
                // A doubled quote is an escaped quote in PowerShell and cmd; '\'' reopens a POSIX string.
                if (i + 1 < text.Length && text[i + 1] == quote && dialect != ShellDialect.Posix)
                {
                    i++;
                }
                else if (quote == '\'' && dialect == ShellDialect.Posix && text.AsSpan(i).StartsWith("'\\''"))
                {
                    i += 3;
                }
                else
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string Unquote(string inner, char quote, ShellDialect dialect)
    {
        if (quote == '\'')
        {
            return dialect == ShellDialect.Posix ? inner.Replace("'\\''", "'") : inner.Replace("''", "'");
        }

        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            var next = i + 1 < inner.Length ? inner[i + 1] : '\0';
            if (c == '\\' && (next == '"' || (dialect == ShellDialect.Posix && next is '\\' or '$' or '`')))
            {
                builder.Append(next);
                i++;
            }
            else if (c == '`' && dialect == ShellDialect.PowerShell && next is '"' or '`')
            {
                builder.Append(next);
                i++;
            }
            else if (c == '"' && next == '"' && dialect != ShellDialect.Posix)
            {
                builder.Append('"');
                i++;
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static List<ReadableCommandLine> Build(List<Item> items, ShellDialect dialect)
    {
        var lines = new List<ReadableCommandLine> { new() };
        var line = lines[0];
        var expectProgram = true;
        var program = "";
        var slots = 0;
        var lastKeyword = "";
        var redirectTarget = false;

        void NewLine(string connector, string indent)
        {
            if (line.IsEmpty)
            {
                line.Connector = connector.Length > 0 ? connector : line.Connector;
                line.Indent = indent;
            }
            else
            {
                line = new ReadableCommandLine { Connector = connector, Indent = indent };
                lines.Add(line);
            }

            expectProgram = true;
            program = "";
            slots = 0;
            redirectTarget = false;
        }

        void Add(string text, CommandPartKind kind)
        {
            if (text.Length > 0)
            {
                line.Parts.Add(new CommandPart(text, kind));
            }
        }

        void Space(Item item)
        {
            if (item.SpaceBefore && !line.IsEmpty)
            {
                Add(" ", CommandPartKind.Plain);
            }
        }

        void Render(List<Piece> pieces, CommandPartKind? bareKind)
        {
            for (var i = 0; i < pieces.Count;)
            {
                var piece = pieces[i];
                if (piece.Group == 0)
                {
                    Add(piece.Text, piece.Kind == PieceKind.Variable ? CommandPartKind.Variable : bareKind ?? Classify(piece.Text));
                    i++;
                    continue;
                }

                // A quoted string: its pieces share a group. Words with spaces get typographic quotes so
                // the string's extent still reads; a single word needs none.
                var end = i;
                var whole = new StringBuilder();
                while (end < pieces.Count && pieces[end].Group == piece.Group)
                {
                    whole.Append(pieces[end++].Text);
                }

                var quoted = whole.Length == 0 || whole.ToString().Any(char.IsWhiteSpace);
                if (quoted)
                {
                    Add("\u201C", CommandPartKind.Quote);
                }

                for (; i < end; i++)
                {
                    Add(pieces[i].Text, pieces[i].Kind == PieceKind.Variable ? CommandPartKind.Variable : CommandPartKind.Text);
                }

                if (quoted)
                {
                    Add("\u201D", CommandPartKind.Quote);
                }
            }
        }

        void Word(List<Piece> pieces)
        {
            var allBare = pieces.All(p => p is { Group: 0, Kind: PieceKind.Bare });
            var text = string.Concat(pieces.Select(p => p.Text));

            if (redirectTarget)
            {
                redirectTarget = false;
                Render(pieces, CommandPartKind.Path);
                return;
            }

            if (expectProgram)
            {
                if (pieces[0] is { Group: 0, Kind: PieceKind.Bare } first && Assignment().Match(first.Text) is { Success: true } assignment)
                {
                    Add(assignment.Value[..^1], CommandPartKind.Variable);
                    Add("=", CommandPartKind.Plain);
                    var rest = new List<Piece>(pieces);
                    rest[0] = first with { Text = first.Text[assignment.Length..] };
                    Render(rest, null);
                    return;
                }

                if (allBare && Keywords.Contains(text) && (dialect == ShellDialect.PowerShell || text == text.ToLowerInvariant()))
                {
                    Add(text, CommandPartKind.Keyword);
                    lastKeyword = text.ToLowerInvariant();
                    expectProgram = !NameAfter.Contains(text);
                    return;
                }

                if (pieces[0].Kind == PieceKind.Variable || (allBare && (NumberWord().IsMatch(text) || text.StartsWith('-'))))
                {
                    Render(pieces, null);
                    expectProgram = false;
                    return;
                }

                if (allBare && Wrappers.Contains(text))
                {
                    Add(text, CommandPartKind.Keyword);
                    return;
                }

                var name = ProgramName(text);
                Add(name, CommandPartKind.Program);
                program = name;
                slots = SubcommandPrograms.GetValueOrDefault(name);
                expectProgram = false;
                return;
            }

            // Flags, including --name=value where the value may be quoted.
            if (pieces[0] is { Group: 0, Kind: PieceKind.Bare } lead && lead.Text.Length > 1 && lead.Text[0] == '-' && !NumberWord().IsMatch(lead.Text))
            {
                var equals = lead.Text.IndexOf('=');
                if (equals < 0 && allBare)
                {
                    Add(text, CommandPartKind.Flag);
                    return;
                }

                if (equals > 0)
                {
                    Add(lead.Text[..equals], CommandPartKind.Flag);
                    Add("=", CommandPartKind.Plain);
                    var rest = new List<Piece>(pieces);
                    rest[0] = lead with { Text = lead.Text[(equals + 1)..] };
                    Render(rest, null);
                    return;
                }
            }

            if (allBare && text == "in" && lastKeyword is "for" or "foreach" or "case" or "select")
            {
                Add(text, CommandPartKind.Keyword);
                return;
            }

            // PowerShell's $x = Get-Thing: what follows the = is a command.
            if (allBare && text == "=" && program.Length == 0)
            {
                Add(text, CommandPartKind.Plain);
                expectProgram = true;
                return;
            }

            if (slots > 0 && allBare && SubcommandWord().IsMatch(text) && !PathLike().IsMatch(text))
            {
                Add(text, CommandPartKind.Subcommand);
                slots--;
                if (NestedSubcommands.Contains(text))
                {
                    slots++;
                }

                return;
            }

            slots = 0;
            Render(pieces, null);
        }

        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case ItemKind.Newline:
                    NewLine("", item.Text);
                    break;

                case ItemKind.Separator:
                    switch (item.Text)
                    {
                        case "&&":
                            NewLine("then", "");
                            break;
                        case "||":
                            NewLine("otherwise", "");
                            break;
                        case "|":
                            NewLine("piped to", "");
                            break;
                        case "&":
                            Add(" (in the background)", CommandPartKind.Note);
                            NewLine("", "");
                            break;
                        default:
                            NewLine("", "");
                            break;
                    }

                    break;

                case ItemKind.Punctuation:
                    Space(item);
                    Add(item.Text, CommandPartKind.Punctuation);
                    if (item.Text is ")" or "}")
                    {
                        expectProgram = false;
                    }
                    else
                    {
                        expectProgram = true;
                        program = "";
                        slots = 0;
                    }

                    break;

                case ItemKind.Redirect:
                    Space(item);
                    Add(item.Text, CommandPartKind.Redirect);
                    redirectTarget = !item.Text.Contains('&') && !item.Text.StartsWith("<<", StringComparison.Ordinal);
                    break;

                case ItemKind.Comment:
                    Space(item);
                    Add(item.Text, CommandPartKind.Comment);
                    break;

                case ItemKind.Heredoc:
                    foreach (var text in item.Text.Split('\n'))
                    {
                        NewLine("", "    ");
                        Add(text, CommandPartKind.Text);
                        if (line.IsEmpty)
                        {
                            Add(" ", CommandPartKind.Plain);
                        }
                    }

                    NewLine("", "");
                    break;

                case ItemKind.Word:
                    Space(item);
                    Word(item.Pieces);
                    break;
            }
        }

        lines.RemoveAll(l => l.IsEmpty);
        return lines;
    }

    private static CommandPartKind Classify(string text) =>
        text.StartsWith('$') ? CommandPartKind.Variable
        : PathLike().IsMatch(text) ? CommandPartKind.Path
        : CommandPartKind.Value;

    /// <summary><c>C:\Program Files\Git\bin\git.exe</c> reads as <c>git</c>; a relative script keeps its path.</summary>
    private static string ProgramName(string text)
    {
        var name = text;
        var absolute = name.Length > 2 && char.IsAsciiLetter(name[0]) && name[1] == ':' && name[2] is '\\' or '/'
            || name.StartsWith('/') || name.StartsWith(@"\\", StringComparison.Ordinal);
        if (absolute && name.LastIndexOfAny(['/', '\\']) is var slash and >= 0 && slash < name.Length - 1)
        {
            name = name[(slash + 1)..];
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Length > 4 ? name[..^4] : name;
    }

    private enum ItemKind
    {
        Word,
        Separator,
        Newline,
        Punctuation,
        Redirect,
        Comment,
        Heredoc,
    }

    private enum PieceKind
    {
        Bare,
        Variable,
    }

    /// <summary>A stretch of one word; pieces of the same quoted string share a non-zero <see cref="Group"/>.</summary>
    private readonly record struct Piece(string Text, PieceKind Kind, int Group);

    private sealed class Item
    {
        public ItemKind Kind { get; init; }

        public string Text { get; init; } = "";

        public List<Piece> Pieces { get; init; } = [];

        public bool SpaceBefore { get; set; }
    }

    /// <summary>Splits the text into words (with quotes resolved), separators, redirects and comments.</summary>
    private sealed class Scanner(string s, ShellDialect dialect)
    {
        private readonly List<Item> _items = [];
        private List<Piece>? _word;
        private bool _wordSpace;
        private bool _pendingSpace;
        private int _groups;
        private int _depth;
        private (string Delimiter, bool StripTabs)? _heredoc;

        private bool Posix => dialect == ShellDialect.Posix;

        private bool Pwsh => dialect == ShellDialect.PowerShell;

        private char At(int i) => i < s.Length ? s[i] : '\0';

        public List<Item> Run()
        {
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                var next = At(i + 1);

                if (((Posix && c == '\\') || (Pwsh && c == '`')) && next == '\n')
                {
                    EndWord();
                    _pendingSpace = true;
                    i += 2;
                }
                else if (c is ' ' or '\t')
                {
                    EndWord();
                    _pendingSpace = true;
                    i++;
                }
                else if (c == '\n')
                {
                    i = Newline(i);
                }
                else if (c == '#' && _word is null && dialect != ShellDialect.Cmd)
                {
                    var end = s.IndexOf('\n', i);
                    end = end < 0 ? s.Length : end;
                    Emit(ItemKind.Comment, s[i..end].TrimEnd());
                    i = end;
                }
                else if (Pwsh && c == '@' && next is '"' or '\'' && At(i + 2) == '\n')
                {
                    // A here-string runs to the line that starts with its closing "@ or '@.
                    var close = s.IndexOf("\n" + next + "@", i + 3, StringComparison.Ordinal);
                    var end = close < 0 ? s.Length : close;
                    AddPiece(s[(i + 3)..end], PieceKind.Bare, ++_groups);
                    i = close < 0 ? s.Length : close + 3;
                }
                else if (c == '\'' && dialect != ShellDialect.Cmd)
                {
                    i = ReadSingle(i);
                }
                else if (Posix && c == '$' && next == '\'')
                {
                    i = ReadSingle(i + 1);
                }
                else if (c == '"')
                {
                    i = ReadDouble(i);
                }
                else if (c == '$' && IsVariableStart(next))
                {
                    i = ReadVariable(i, out var variable);
                    AddPiece(variable, PieceKind.Variable, 0);
                }
                else if (Posix && c == '\\' && next != '\0' && !char.IsLetterOrDigit(next))
                {
                    AddPiece(next.ToString(), PieceKind.Bare, 0);
                    i += 2;
                }
                else if (Pwsh && c == '`' && next is '"' or '\'' or '`' or '$' or ' ' or '|' or '&' or ';')
                {
                    AddPiece(next.ToString(), PieceKind.Bare, 0);
                    i += 2;
                }
                else if (c is '&' or '|' or ';')
                {
                    i = Operator(i);
                }
                else if (c is '>' or '<')
                {
                    i = Redirect(i);
                }
                else if (c is '(' or ')' || (c == '@' && next == '(' && _word is null))
                {
                    var text = c == '@' ? "@(" : c.ToString();
                    Emit(ItemKind.Punctuation, text);
                    _depth = Math.Max(0, _depth + (c == ')' ? -1 : 1));
                    i += text.Length;
                }
                else if (c == '{' && _word is null && (Pwsh || next is ' ' or '\t' or '\n' or '\0'))
                {
                    Emit(ItemKind.Punctuation, "{");
                    _depth++;
                    i++;
                }
                else if (c == '}' && (Pwsh || _word is null))
                {
                    Emit(ItemKind.Punctuation, "}");
                    _depth = Math.Max(0, _depth - 1);
                    i++;
                }
                else
                {
                    AddPiece(c.ToString(), PieceKind.Bare, 0);
                    i++;
                }
            }

            EndWord();
            return _items;
        }

        private int Newline(int i)
        {
            EndWord();
            _pendingSpace = false;

            if (_heredoc is { } heredoc)
            {
                // The body runs from the next line to the line holding only the delimiter.
                _heredoc = null;
                var body = new List<string>();
                var at = i + 1;
                while (at <= s.Length)
                {
                    var end = s.IndexOf('\n', at);
                    end = end < 0 ? s.Length : end;
                    var text = s[at..end];
                    at = end + 1;
                    if ((heredoc.StripTabs ? text.TrimStart('\t') : text).Trim() == heredoc.Delimiter)
                    {
                        break;
                    }

                    body.Add(text);
                }

                _items.Add(new Item { Kind = ItemKind.Heredoc, Text = string.Join('\n', body) });
                return Math.Min(at - 1, s.Length);
            }

            var start = i + 1;
            var j = start;
            while (j < s.Length && s[j] is ' ' or '\t')
            {
                j++;
            }

            _items.Add(new Item { Kind = ItemKind.Newline, Text = s[start..j].Replace("\t", "    ") });
            return j;
        }

        private int Operator(int i)
        {
            var c = s[i];
            var next = At(i + 1);
            if (c == next && c is '&' or '|')
            {
                Separator(new string(c, 2));
                return i + 2;
            }

            if (c == '&' && Pwsh)
            {
                // PowerShell's call operator: & "C:\path\tool.exe" args.
                Emit(ItemKind.Punctuation, "&");
                return i + 1;
            }

            if (c == '&' && Posix && next == '>')
            {
                return Redirect(i);
            }

            Separator(c == '&' && dialect == ShellDialect.Cmd ? ";" : c.ToString());
            return i + 1;
        }

        private void Separator(string op)
        {
            if (_depth > 0)
            {
                Emit(ItemKind.Punctuation, op);
            }
            else
            {
                Emit(ItemKind.Separator, op);
            }
        }

        private int Redirect(int i)
        {
            // A bare file-descriptor number right before the arrow belongs to it: 2>, 2>&1.
            var prefix = "";
            if (_word is [{ Group: 0, Kind: PieceKind.Bare } only] && (only.Text.All(char.IsAsciiDigit) || (Pwsh && only.Text == "*")))
            {
                prefix = only.Text;
                _word = null;
                _pendingSpace = _wordSpace;
            }

            if (Posix && s[i] == '<' && HeredocStart().Match(s[i..]) is { Success: true } heredoc)
            {
                _heredoc = (heredoc.Groups[3].Value, heredoc.Groups[1].Length > 0);
                Emit(ItemKind.Redirect, "<<" + heredoc.Groups[3].Value);
                return i + heredoc.Length;
            }

            var start = i;
            if (s[i] == '&')
            {
                i++;
            }

            var arrow = s[i];
            i++;
            while (i < s.Length && s[i] == arrow && i - start < 3)
            {
                i++;
            }

            if (At(i) == '&' && (char.IsAsciiDigit(At(i + 1)) || At(i + 1) == '-'))
            {
                i += 2;
            }

            Emit(ItemKind.Redirect, prefix + s[start..i]);
            return i;
        }

        private int ReadSingle(int i)
        {
            var group = ++_groups;
            var builder = new StringBuilder();
            var j = i + 1;
            for (; j < s.Length; j++)
            {
                if (s[j] == '\'')
                {
                    if (Pwsh && At(j + 1) == '\'')
                    {
                        builder.Append('\'');
                        j++;
                        continue;
                    }

                    break;
                }

                builder.Append(s[j]);
            }

            AddPiece(builder.ToString(), PieceKind.Bare, group);
            return Math.Min(j + 1, s.Length);
        }

        private int ReadDouble(int i)
        {
            var group = ++_groups;
            var builder = new StringBuilder();
            var any = false;

            void Flush()
            {
                if (builder.Length > 0)
                {
                    AddPiece(builder.ToString(), PieceKind.Bare, group);
                    builder.Clear();
                    any = true;
                }
            }

            var j = i + 1;
            while (j < s.Length)
            {
                var c = s[j];
                var next = At(j + 1);
                if (c == '"')
                {
                    if (dialect != ShellDialect.Posix && next == '"')
                    {
                        builder.Append('"');
                        j += 2;
                        continue;
                    }

                    break;
                }

                if (Posix && c == '\\' && next is '"' or '\\' or '$' or '`' or '\n')
                {
                    if (next != '\n')
                    {
                        builder.Append(next);
                    }

                    j += 2;
                }
                else if (Pwsh && c == '`' && next is '"' or '`' or '$' or '\'')
                {
                    builder.Append(next);
                    j += 2;
                }
                else if (c == '$' && dialect != ShellDialect.Cmd && IsVariableStart(next))
                {
                    Flush();
                    j = ReadVariable(j, out var variable);
                    AddPiece(variable, PieceKind.Variable, group);
                    any = true;
                }
                else
                {
                    builder.Append(c);
                    j++;
                }
            }

            Flush();
            if (!any)
            {
                AddPiece("", PieceKind.Bare, group);
            }

            return Math.Min(j + 1, s.Length);
        }

        private bool IsVariableStart(char c) =>
            char.IsAsciiLetter(c) || c is '_' or '{' or '(' || (Posix && (char.IsAsciiDigit(c) || c is '?' or '!' or '@' or '#' or '*' or '$'));

        private int ReadVariable(int i, out string text)
        {
            var j = i + 1;
            var c = At(j);
            if (c == '{')
            {
                var close = s.IndexOf('}', j);
                j = close < 0 ? s.Length : close + 1;
            }
            else if (c == '(')
            {
                j = MatchingParen(j);
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '_'))
                {
                    j++;
                }

                // PowerShell scopes ride on a colon: $env:PATH, $script:x.
                if (Pwsh && At(j) == ':' && char.IsAsciiLetter(At(j + 1)))
                {
                    j++;
                    while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '_'))
                    {
                        j++;
                    }
                }
            }
            else
            {
                j++;
            }

            text = s[i..j];
            return j;
        }

        private int MatchingParen(int open)
        {
            var depth = 0;
            char? quote = null;
            for (var j = open; j < s.Length; j++)
            {
                var c = s[j];
                if (quote is not null)
                {
                    if (c == quote)
                    {
                        quote = null;
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '(')
                {
                    depth++;
                }
                else if (c == ')' && --depth == 0)
                {
                    return j + 1;
                }
            }

            return s.Length;
        }

        private void AddPiece(string text, PieceKind kind, int group)
        {
            if (_word is null)
            {
                _word = [];
                _wordSpace = _pendingSpace;
                _pendingSpace = false;
            }

            // Neighbouring unquoted characters make one piece.
            if (kind == PieceKind.Bare && group == 0 && _word.Count > 0 && _word[^1] is { Group: 0, Kind: PieceKind.Bare } last)
            {
                _word[^1] = last with { Text = last.Text + text };
            }
            else
            {
                _word.Add(new Piece(text, kind, group));
            }
        }

        private void EndWord()
        {
            if (_word is not null)
            {
                _items.Add(new Item { Kind = ItemKind.Word, Pieces = _word, SpaceBefore = _wordSpace });
                _word = null;
            }
        }

        private void Emit(ItemKind kind, string text)
        {
            EndWord();
            _items.Add(new Item { Kind = kind, Text = text, SpaceBefore = _pendingSpace });
            _pendingSpace = false;
        }
    }
}
