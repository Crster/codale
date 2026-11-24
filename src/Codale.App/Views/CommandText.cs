using Codale.Core.Shell;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using Windows.UI;
using Windows.UI.Text;

namespace Codale.App.Views;

/// <summary>
/// Attached properties that show a shell command the way a person reads it (see
/// <see cref="ReadableShellCommand"/>): wrapper peeled, quotes and escapes resolved, one
/// command per line joined by "then" / "otherwise" / "piped to", and the program, its
/// subcommand, flags, paths, strings and variables each in their own colour. Used where a
/// command is looked at rather than copied: the transcript's Run tooltip and the peek pane.
/// Both sit on dark surfaces (the window is pinned dark), so one palette serves.
/// </summary>
public static class CommandText
{
    private static readonly Dictionary<CommandPartKind, Brush> Palette = new()
    {
        [CommandPartKind.Program] = Solid(0x61, 0xAF, 0xEF),
        [CommandPartKind.Subcommand] = Solid(0x56, 0xB6, 0xC2),
        [CommandPartKind.Keyword] = Solid(0xC6, 0x78, 0xDD),
        [CommandPartKind.Flag] = Solid(0xD1, 0x9A, 0x66),
        [CommandPartKind.Path] = Solid(0xE5, 0xC0, 0x7B),
        [CommandPartKind.Text] = Solid(0x98, 0xC3, 0x79),
        [CommandPartKind.Quote] = Solid(0x5F, 0x7D, 0x4E),
        [CommandPartKind.Variable] = Solid(0xE0, 0x6C, 0x75),
        [CommandPartKind.Punctuation] = Solid(0x8B, 0x95, 0xA1),
        [CommandPartKind.Redirect] = Solid(0x8B, 0x95, 0xA1),
        [CommandPartKind.Comment] = Solid(0x7F, 0x8C, 0x9B),
        [CommandPartKind.Note] = Solid(0x7F, 0x8C, 0x9B),
    };

    private static readonly Brush ConnectorBrush = Solid(0x8B, 0x95, 0xA1);

    private static Brush Solid(byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(255, r, g, b));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(string), typeof(CommandText), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty IsCommandProperty = DependencyProperty.RegisterAttached(
        "IsCommand", typeof(bool), typeof(CommandText), new PropertyMetadata(true, OnChanged));

    public static readonly DependencyProperty DialectProperty = DependencyProperty.RegisterAttached(
        "Dialect", typeof(string), typeof(CommandText), new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.RegisterAttached(
        "MaxLines", typeof(int), typeof(CommandText), new PropertyMetadata(0, OnChanged));

    /// <summary>The command line to show.</summary>
    public static string? GetCommand(TextBlock text) => (string?)text.GetValue(CommandProperty);

    public static void SetCommand(TextBlock text, string? value) => text.SetValue(CommandProperty, value);

    /// <summary>False shows <see cref="GetCommand"/> as plain text: the same slot can hold a file name or a pattern.</summary>
    public static bool GetIsCommand(TextBlock text) => (bool)text.GetValue(IsCommandProperty);

    public static void SetIsCommand(TextBlock text, bool value) => text.SetValue(IsCommandProperty, value);

    /// <summary>"powershell", "bash" or "cmd" when the tool says which shell runs it; empty guesses.</summary>
    public static string GetDialect(TextBlock text) => (string)text.GetValue(DialectProperty);

    public static void SetDialect(TextBlock text, string value) => text.SetValue(DialectProperty, value);

    /// <summary>Lines shown before the rest folds into "… N more lines"; 0 shows all.</summary>
    public static int GetMaxLines(TextBlock text) => (int)text.GetValue(MaxLinesProperty);

    public static void SetMaxLines(TextBlock text, int value) => text.SetValue(MaxLinesProperty, value);

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBlock text)
        {
            return;
        }

        text.Inlines.Clear();
        if (GetCommand(text) is not { Length: > 0 } command)
        {
            return;
        }

        if (!GetIsCommand(text))
        {
            text.Inlines.Add(new Run { Text = command });
            return;
        }

        var dialect = GetDialect(text).ToLowerInvariant() switch
        {
            "powershell" or "pwsh" => ShellDialect.PowerShell,
            "bash" or "sh" or "posix" => ShellDialect.Posix,
            "cmd" => ShellDialect.Cmd,
            _ => ShellDialect.Auto,
        };

        var readable = ReadableShellCommand.Parse(command, dialect);
        if (readable.Lines.Count == 0)
        {
            text.Inlines.Add(new Run { Text = command.Trim() });
            return;
        }

        var maxLines = GetMaxLines(text);
        var shown = maxLines > 0 && readable.Lines.Count > maxLines ? maxLines - 1 : readable.Lines.Count;

        if (readable.Shell.Length > 0)
        {
            text.Inlines.Add(new Run { Text = "in " + readable.Shell, Foreground = ConnectorBrush, FontStyle = FontStyle.Italic });
            text.Inlines.Add(new LineBreak());
        }

        for (var i = 0; i < shown; i++)
        {
            var line = readable.Lines[i];
            if (i > 0)
            {
                text.Inlines.Add(new LineBreak());
            }

            if (line.Connector.Length > 0)
            {
                text.Inlines.Add(new Run { Text = line.Connector + "  ", Foreground = ConnectorBrush, FontStyle = FontStyle.Italic });
            }
            else if (line.Indent.Length > 0)
            {
                text.Inlines.Add(new Run { Text = line.Indent });
            }

            foreach (var part in line.Parts)
            {
                AddPart(text, part);
            }
        }

        if (shown < readable.Lines.Count)
        {
            text.Inlines.Add(new LineBreak());
            var more = readable.Lines.Count - shown;
            text.Inlines.Add(new Run { Text = $"… {more} more line{(more == 1 ? "" : "s")}", Foreground = ConnectorBrush, FontStyle = FontStyle.Italic });
        }
    }

    private static void AddPart(TextBlock text, CommandPart part)
    {
        // A string spanning lines keeps its line breaks: a Run does not break on '\n' by itself.
        var pieces = part.Text.Split('\n');
        for (var i = 0; i < pieces.Length; i++)
        {
            if (i > 0)
            {
                text.Inlines.Add(new LineBreak());
            }

            if (pieces[i].Length == 0)
            {
                continue;
            }

            var run = new Run { Text = pieces[i] };
            if (Palette.TryGetValue(part.Kind, out var brush))
            {
                run.Foreground = brush;
            }

            if (part.Kind is CommandPartKind.Program)
            {
                run.FontWeight = FontWeights.SemiBold;
            }
            else if (part.Kind is CommandPartKind.Comment or CommandPartKind.Note)
            {
                run.FontStyle = FontStyle.Italic;
            }

            text.Inlines.Add(run);
        }
    }
}
