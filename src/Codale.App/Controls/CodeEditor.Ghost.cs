using System.Text;

using Codale.Editor;

using Microsoft.UI.Xaml;

namespace Codale.App.Controls;

/// <summary>
/// Ghost text: the local Smart model proposes the rest of the line by fill-in-the-middle, drawn dimmed after the caret.
/// It is asked on demand only, with Ctrl+Space (again for a different proposal); nothing is requested while typing.
/// Tab accepts it as one undo step; Escape, any other key, a click or a caret move drops it.
/// </summary>
public sealed partial class CodeEditor
{
    private const int GhostPrefixChars = 3000;
    private const int GhostSuffixChars = 1500;

    private string _ghostText = "";
    private DocumentPosition _ghostAt;
    private CancellationTokenSource? _ghostCts;
    private readonly List<string> _ghostSeen = [];
    private DocumentPosition _ghostSeenAt;
    private bool _ghostPending;
    private DocumentPosition _ghostPendingAt;

    /// <summary>
    /// The one line to insert at the caret, from the code before and after it and the proposals already passed over
    /// there; null or empty for none. Set by the owner when a completion model is available; null disables ghost text.
    /// </summary>
    public Func<string, string, IReadOnlyList<string>, CancellationToken, Task<string?>>? InlineCompleter { get; set; }

    /// <summary>Whether the editor hints at Ctrl+Space after the code at the end of the caret's line. Set by the owner from the settings.</summary>
    public bool ShowInlineHint { get; set; }

    private bool GhostVisible => _ghostText.Length > 0 && !_model.HasMultipleSelections && _ghostAt == caret && _model.Primary.IsEmpty;

    /// <summary>The hint shows only where Ctrl+Space would act: focused, caret after code at the end of its line, nothing else shown.</summary>
    private bool GhostHintVisible => ShowInlineHint && InlineCompleter is not null && _ghostText.Length == 0 && !CompletionOpen
        && FocusState != FocusState.Unfocused && !HasSelection && !_model.HasMultipleSelections && _model.Primary.IsEmpty
        && AtEndOfCodeLine();

    /// <summary>True when code precedes the caret on its line and only whitespace follows it.</summary>
    private bool AtEndOfCodeLine()
    {
        var line = doc.GetLine(caret.Line);
        return line[..caret.Col].Trim().Length > 0 && line[caret.Col..].Trim().Length == 0;
    }

    private void ClearGhost()
    {
        _ghostCts?.Cancel();
        _ghostSeen.Clear();
        var wasPending = _ghostPending;
        _ghostPending = false;
        if (_ghostText.Length > 0 || wasPending)
        {
            _ghostText = "";
            Invalidate();
        }
    }

    /// <summary>Inserts the proposed text at the caret as one undo step. True when there was any.</summary>
    private bool AcceptGhost()
    {
        if (!GhostVisible)
        {
            return false;
        }

        var text = _ghostText;
        ClearGhost();
        _model.ReplaceRange(caret, caret, text);
        return true;
    }

    /// <summary>Asks for a one-line proposal at the caret (Ctrl+Space); asking again at the same spot asks for a different one.</summary>
    private void RequestGhost()
    {
        _ghostCts?.Cancel();
        if (InlineCompleter is not { } complete || !_model.Primary.IsEmpty || _model.HasMultipleSelections)
        {
            return;
        }

        var at = caret;
        if (_ghostSeenAt != at)
        {
            _ghostSeen.Clear();
            _ghostSeenAt = at;
        }

        if (GhostVisible)
        {
            _ghostSeen.Add(_ghostText);
        }

        var prefix = PrefixBefore(at);
        if (prefix.Trim().Length == 0)
        {
            return;
        }

        var suffix = SuffixAfter(at);
        var cts = _ghostCts = new CancellationTokenSource();
        _ghostPending = true;
        _ghostPendingAt = at;
        Invalidate();
        _ = RunGhostAsync(complete, prefix, suffix, at, cts);
    }

    private async Task RunGhostAsync(Func<string, string, IReadOnlyList<string>, CancellationToken, Task<string?>> complete,
        string prefix, string suffix, DocumentPosition at, CancellationTokenSource cts)
    {
        try
        {
            var text = await complete(prefix, suffix, [.. _ghostSeen], cts.Token);
            if (string.IsNullOrEmpty(text) || cts.IsCancellationRequested || caret != at || !_model.Primary.IsEmpty)
            {
                return;
            }

            _ghostText = text;
            _ghostAt = at;
            Invalidate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A completion is optional: a failure shows nothing.
        }
        finally
        {
            // A superseded request must not clear the status of the one that replaced it.
            if (ReferenceEquals(_ghostCts, cts))
            {
                _ghostPending = false;
                Invalidate();
            }
        }
    }

    /// <summary>True while a proposal is being asked for at the caret.</summary>
    private bool GhostPendingVisible => _ghostPending && _ghostPendingAt == caret && _model.Primary.IsEmpty;

    /// <summary>The buffer's text before <paramref name="at"/>, at most <see cref="GhostPrefixChars"/> characters, whole lines where it can.</summary>
    private string PrefixBefore(DocumentPosition at)
    {
        var lines = new List<string> { doc.GetLine(at.Line)[..at.Col] };
        var size = lines[0].Length;
        for (var i = at.Line - 1; i >= 0 && size < GhostPrefixChars; i--)
        {
            var text = doc.GetLine(i);
            lines.Add(text);
            size += text.Length + 1;
        }

        lines.Reverse();
        return string.Join("\n", lines);
    }

    /// <summary>The buffer's text from <paramref name="at"/> on, at most <see cref="GhostSuffixChars"/> characters, whole lines where it can.</summary>
    private string SuffixAfter(DocumentPosition at)
    {
        var builder = new StringBuilder(doc.GetLine(at.Line)[at.Col..]);
        for (var i = at.Line + 1; i < doc.LineCount && builder.Length < GhostSuffixChars; i++)
        {
            builder.Append('\n').Append(doc.GetLine(i));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Draws, after the caret's line end, either the "Completing…" status while a proposal is being asked for or the
    /// Ctrl+Space hint otherwise. Both are dimmer than a proposal and never accepted by Tab.
    /// </summary>
    private void DrawGhostHint(Microsoft.Graphics.Canvas.CanvasDrawingSession session)
    {
        if (_textFormat is null)
        {
            return;
        }

        string text;
        double alpha;
        if (GhostPendingVisible)
        {
            text = "Completing…";
            alpha = 0.5;
        }
        else if (GhostHintVisible)
        {
            text = "Ctrl+Space for a suggestion";
            alpha = 0.3;
        }
        else
        {
            return;
        }

        var (row, x) = Locate(caret);
        var color = _baseText;
        color.A = (byte)(color.A * alpha);
        using var layout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(Device, text, _textFormat, 0f, (float)_lineHeight);
        session.DrawTextLayout(layout, (float)(TextLeft + x - _scrollX + 16), RowY(row), color);
    }

    /// <summary>Draws the proposal after the caret, in the base text colour at low opacity.</summary>
    private void DrawGhost(Microsoft.Graphics.Canvas.CanvasDrawingSession session)
    {
        if (!GhostVisible || _textFormat is null)
        {
            return;
        }

        var (row, x) = Locate(caret);
        var color = _baseText;
        color.A = (byte)(color.A * 0.45);
        var lines = _ghostText.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            using var layout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(Device, lines[i], _textFormat, 0f, (float)_lineHeight);
            layout.Options = Microsoft.Graphics.Canvas.Text.CanvasDrawTextOptions.EnableColorFont;
            session.DrawTextLayout(layout, (float)(TextLeft + (i == 0 ? x : 0) - _scrollX), RowY(row + i), color);
        }
    }
}