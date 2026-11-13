using System.Text;

using Codale.Editor;

using Microsoft.UI.Xaml;

namespace Codale.App.Controls;

/// <summary>
/// Ghost text: the local Smart model proposes the rest of the line by fill-in-the-middle, drawn dimmed after the caret.
/// It is asked after a short pause in typing when the caret ends its line's code, or on demand with Ctrl+Space (again
/// for a different proposal). Tab accepts it as one undo step; Escape, any other key, a click or a caret move drops it.
/// </summary>
public sealed partial class CodeEditor
{
    private const int GhostPrefixChars = 3000;
    private const int GhostSuffixChars = 1500;
    private static readonly TimeSpan GhostDelay = TimeSpan.FromMilliseconds(300);

    private string _ghostText = "";
    private DocumentPosition _ghostAt;
    private CancellationTokenSource? _ghostCts;
    private readonly List<string> _ghostSeen = [];
    private DocumentPosition _ghostSeenAt;
    private DispatcherTimer? _ghostTimer;

    /// <summary>
    /// The one line to insert at the caret, from the code before and after it and the proposals already passed over
    /// there; null or empty for none. Set by the owner when a completion model is available; null disables ghost text.
    /// </summary>
    public Func<string, string, IReadOnlyList<string>, CancellationToken, Task<string?>>? InlineCompleter { get; set; }

    /// <summary>Whether ghost text is asked for after a pause in typing (Ctrl+Space always asks).</summary>
    public bool AutomaticInlineCompletions { get; set; } = true;

    private bool GhostVisible => _ghostText.Length > 0 && !_model.HasMultipleSelections && _ghostAt == _caret && _model.Primary.IsEmpty;

    private void ClearGhost()
    {
        _ghostTimer?.Stop();
        _ghostCts?.Cancel();
        _ghostSeen.Clear();
        if (_ghostText.Length > 0)
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
        _model.ReplaceRange(_caret, _caret, text);
        return true;
    }

    /// <summary>After a typed character: ask once typing pauses, when the caret ends the code on its line.</summary>
    private void ScheduleGhost()
    {
        if (InlineCompleter is null || !AutomaticInlineCompletions || HasSelection || _model.HasMultipleSelections)
        {
            return;
        }

        if (_ghostTimer is null)
        {
            _ghostTimer = new DispatcherTimer { Interval = GhostDelay };
            _ghostTimer.Tick += (_, _) =>
            {
                _ghostTimer.Stop();
                if (!CompletionOpen && FocusState != FocusState.Unfocused && AtLineEndOfCode())
                {
                    RequestGhost();
                }
            };
        }

        _ghostTimer.Stop();
        _ghostTimer.Start();
    }

    /// <summary>True when only whitespace or closing punctuation follows the caret on its line.</summary>
    private bool AtLineEndOfCode()
    {
        var rest = _doc.GetLine(_caret.Line)[_caret.Col..];
        return rest.All(c => char.IsWhiteSpace(c) || c is ')' or ']' or '}' or ';' or ',' or '"' or '\'' or '>');
    }

    /// <summary>Asks for a one-line proposal at the caret; asking again at the same spot asks for a different one.</summary>
    private void RequestGhost()
    {
        _ghostTimer?.Stop();
        _ghostCts?.Cancel();
        if (InlineCompleter is not { } complete || !_model.Primary.IsEmpty || _model.HasMultipleSelections)
        {
            return;
        }

        var at = _caret;
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
        _ = RunGhostAsync(complete, prefix, suffix, at, cts);
    }

    private async Task RunGhostAsync(Func<string, string, IReadOnlyList<string>, CancellationToken, Task<string?>> complete,
        string prefix, string suffix, DocumentPosition at, CancellationTokenSource cts)
    {
        try
        {
            var text = await complete(prefix, suffix, _ghostSeen.ToList(), cts.Token);
            if (string.IsNullOrEmpty(text) || cts.IsCancellationRequested || _caret != at || !_model.Primary.IsEmpty)
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
    }

    /// <summary>The buffer's text before <paramref name="at"/>, at most <see cref="GhostPrefixChars"/> characters, whole lines where it can.</summary>
    private string PrefixBefore(DocumentPosition at)
    {
        var lines = new List<string> { _doc.GetLine(at.Line)[..at.Col] };
        var size = lines[0].Length;
        for (var i = at.Line - 1; i >= 0 && size < GhostPrefixChars; i--)
        {
            var text = _doc.GetLine(i);
            lines.Add(text);
            size += text.Length + 1;
        }

        lines.Reverse();
        return string.Join("\n", lines);
    }

    /// <summary>The buffer's text from <paramref name="at"/> on, at most <see cref="GhostSuffixChars"/> characters, whole lines where it can.</summary>
    private string SuffixAfter(DocumentPosition at)
    {
        var builder = new StringBuilder(_doc.GetLine(at.Line)[at.Col..]);
        for (var i = at.Line + 1; i < _doc.LineCount && builder.Length < GhostSuffixChars; i++)
        {
            builder.Append('\n').Append(_doc.GetLine(i));
        }

        return builder.ToString();
    }

    /// <summary>Draws the proposal after the caret, in the base text colour at low opacity.</summary>
    private void DrawGhost(Microsoft.Graphics.Canvas.CanvasDrawingSession session)
    {
        if (!GhostVisible || _textFormat is null)
        {
            return;
        }

        var (row, x) = Locate(_caret);
        var color = _baseText;
        color.A = (byte)(color.A * 0.45);
        using var layout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(Device, _ghostText, _textFormat, 0f, (float)_lineHeight);
        layout.Options = Microsoft.Graphics.Canvas.Text.CanvasDrawTextOptions.EnableColorFont;
        session.DrawTextLayout(layout, (float)(TextLeft + x - _scrollX), RowY(row), color);
    }
}
