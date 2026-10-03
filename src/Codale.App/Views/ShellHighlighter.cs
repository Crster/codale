namespace Codale.App.Views;

/// <summary>What kind of shell word a span is; drives the colour it renders in.</summary>
internal enum ShellTokenKind
{
    Plain,
    Command,
    Keyword,
    String,
    Variable,
    Flag,
    Number,
    Operator,
    Comment,
}

/// <summary>
/// A small shell tokenizer for preview-sized command lines. Not a parser: it only
/// has to be right often enough that the colouring reads, and it must never throw
/// on input it does not understand. Handles bash-style syntax plus the keywords
/// PowerShell adds; quoted spans keep their literal text, with $variables inside
/// double quotes still picked out.
/// </summary>
internal static class ShellHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "then", "elif", "else", "fi", "for", "while", "until", "do", "done",
        "case", "esac", "in", "function", "select", "time", "return",
        "foreach", "switch", "try", "catch", "finally", "param", "begin", "end",
    };

    private const string WordBreaks = " \t|&;<>()#\"'`$";

    public static List<(string Text, ShellTokenKind Kind)> Tokenize(string code)
    {
        var tokens = new List<(string, ShellTokenKind)>();
        var lines = code.ReplaceLineEndings("\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            // A trailing backslash continues the command, so the next line opens in
            // argument position, not command position.
            var continuation = index > 0 && lines[index - 1].TrimEnd().EndsWith('\\');
            ScanLine(lines[index], continuation, tokens);
        }

        return tokens;
    }

    private static void ScanLine(string line, bool continuation, List<(string Text, ShellTokenKind Kind)> tokens)
    {
        var i = 0;
        var commandNext = !continuation;
        var wordStart = true;

        while (i < line.Length)
        {
            var ch = line[i];

            if (ch is ' ' or '\t')
            {
                var text = SpanWhile(line, ref i, c => c is ' ' or '\t');
                tokens.Add((text, ShellTokenKind.Plain));
                wordStart = true;
                continue;
            }

            // A comment runs from a word-start '#' to the end of the line; a#b is a word.
            if (ch == '#' && wordStart)
            {
                tokens.Add((line[i..], ShellTokenKind.Comment));
                return;
            }

            if (ch is '"' or '\'' or '`')
            {
                i = ScanQuoted(line, i, ch, tokens);
                commandNext = false;
                wordStart = false;
                continue;
            }

            if (ch == '$')
            {
                i = SpanVariable(line, i, tokens);
                commandNext = false;
                wordStart = false;
                continue;
            }

            if (ch is '|' or '&' or ';' or '<' or '>')
            {
                var text = SpanWhile(line, ref i, c => c is '|' or '&' or ';' or '<' or '>');
                tokens.Add((text, ShellTokenKind.Operator));

                // A separator opens a new command; a redirect opens a filename.
                commandNext = text.Any(c => c is '|' or ';' or '&');
                wordStart = true;
                continue;
            }

            if (ch is '(' or ')')
            {
                tokens.Add((line[i..(i + 1)], ShellTokenKind.Operator));
                i++;
                continue;
            }

            if (ch == '-' && wordStart)
            {
                var text = SpanWhile(line, ref i, c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '=');
                tokens.Add((text, ShellTokenKind.Flag));
                commandNext = false;
                wordStart = false;
                continue;
            }

            var start = i;
            i = SpanWord(line, i);
            var word = line[start..i];

            if (wordStart && char.IsAsciiDigit(word[0]) && word.All(char.IsAsciiDigit))
            {
                tokens.Add((word, ShellTokenKind.Number));
            }
            else if (commandNext && !word.Contains('/') && !word.Contains('\\'))
            {
                tokens.Add((word, Keywords.Contains(word) ? ShellTokenKind.Keyword : ShellTokenKind.Command));
            }
            else if (Keywords.Contains(word))
            {
                tokens.Add((word, ShellTokenKind.Keyword));
            }
            else if (IsAssignment(word))
            {
                tokens.Add((word, ShellTokenKind.Variable));
            }
            else
            {
                tokens.Add((word, ShellTokenKind.Plain));
            }

            commandNext = false;
            wordStart = false;
        }
    }

    private static int SpanWord(string line, int start)
    {
        var i = start;
        while (i < line.Length && !WordBreaks.Contains(line[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary>Quoted text keeps its literal reading; inside double quotes a $var still colours.</summary>
    private static int ScanQuoted(
        string line, int start, char quote, List<(string Text, ShellTokenKind Kind)> tokens)
    {
        var chunkStart = start;
        var i = start + 1;
        tokens.Add((line[start..i], ShellTokenKind.String));

        while (i < line.Length)
        {
            var ch = line[i];

            if (ch == '\\' && quote != '\'' && i + 1 < line.Length)
            {
                i += 2;
                continue;
            }

            if (ch == quote)
            {
                if (i > chunkStart)
                {
                    tokens.Add((line[chunkStart..i], ShellTokenKind.String));
                }

                tokens.Add((line[i..++i], ShellTokenKind.String));
                return i;
            }

            if (ch == '$' && quote == '"')
            {
                if (i > chunkStart)
                {
                    tokens.Add((line[chunkStart..i], ShellTokenKind.String));
                }

                i = SpanVariable(line, i, tokens);
                chunkStart = i;
                continue;
            }

            i++;
        }

        if (line.Length > chunkStart)
        {
            tokens.Add((line[chunkStart..], ShellTokenKind.String));
        }

        return line.Length;
    }

    private static int SpanVariable(string line, int start, List<(string Text, ShellTokenKind Kind)> tokens)
    {
        var i = start + 1;

        if (i < line.Length && line[i] == '{')
        {
            var close = line.IndexOf('}', i + 1);
            i = close < 0 ? line.Length : close + 1;
        }
        else if (i < line.Length && (char.IsAsciiLetter(line[i]) || line[i] == '_'))
        {
            i++;
            SpanWhile(line, ref i, c => char.IsAsciiLetterOrDigit(c) || c == '_');

            // PowerShell scopes ride on a colon: $env:PATH, $global:x.
            if (i + 1 < line.Length && line[i] == ':' && char.IsAsciiLetter(line[i + 1]))
            {
                i++;
                SpanWhile(line, ref i, c => char.IsAsciiLetterOrDigit(c) || c == '_');
            }
        }
        else if (i < line.Length && (char.IsAsciiDigit(line[i]) || line[i] is '?' or '!' or '@' or '#'))
        {
            i++; // positional and shell variables: $1, $?, $!...
        }

        tokens.Add((line[start..i], ShellTokenKind.Variable));
        return i;
    }

    private static string SpanWhile(string line, ref int i, Func<char, bool> keep)
    {
        var start = i;
        while (i < line.Length && keep(line[i]))
        {
            i++;
        }

        return line[start..i];
    }

    /// <summary>VAR=value before a command is an assignment; it colours like a variable.</summary>
    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }

        var name = word[..equals];
        return (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(char.IsAsciiLetterOrDigit);
    }
}
