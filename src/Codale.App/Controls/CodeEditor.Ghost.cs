using System.Diagnostics;
using System.Text;

using Codale.App.Services;

using Microsoft.UI.Xaml;

namespace Codale.App.Controls;

/// <summary>
/// Ghost text: after a typing pause the local model proposes the next few tokens, drawn dimmed after the
/// caret. Tab accepts them; any other key, a click or a newer keystroke drops or replaces them.
/// Only runs when Settings runs background jobs on the local model.
/// </summary>
public sealed partial class CodeEditor
{
    private const int GhostDelayMs = 150;
    private const int GhostPrefixChars = 4500;

    private string _ghostText = "";
    private string _ghostFull = "";
    private List<int> _ghostStops = [];
    private int _ghostStop;
    private DocumentPosition _ghostAt;
    private CancellationTokenSource? _ghostCts;

    /// <summary>Where the buffer's file lives, for the model's prompt; null for an unsaved buffer.</summary>
    public Func<string?>? FilePathProvider { get; set; }

    private bool GhostVisible => _ghostText.Length > 0 && _ghostAt == _caret && _anchor == _caret;

    private void ClearGhost()
    {
        _ghostCts?.Cancel();
        _ghostFull = "";
        _ghostStops = [];
        if (_ghostText.Length > 0)
        {
            _ghostText = "";
            CaretCanvas.Invalidate();
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
        BeginUndoStep(canCoalesce: false);
        _caret = Edit(_caret, _caret, text);
        _anchor = _caret;
        AfterEdit();
        return true;
    }

    /// <summary>Starts a fresh request for the caret's position; call after the user typed.</summary>
    private void ScheduleGhost()
    {
        ClearGhost();
        if (_languageId is null || BackgroundHelper.Completions is not { } model || _anchor != _caret)
        {
            return;
        }

        var at = _caret;
        var line = _doc.GetLine(at.Line);

        // Mid-word suggestions fight the keyword popup; wait for the end of the line or a break.
        if (at.Col < line.Length && !char.IsWhiteSpace(line[at.Col]) && !")]};,".Contains(line[at.Col]))
        {
            return;
        }

        var prefix = PrefixBefore(at);
        if (prefix.Trim().Length == 0)
        {
            return;
        }

        var path = FilePathProvider?.Invoke();
        var name = string.IsNullOrEmpty(path) ? "untitled" : Path.GetFileName(path);
        var language = _languageId;
        var cts = _ghostCts = new CancellationTokenSource();
        _ = RunGhostAsync(model, name, language, prefix, at, cts);
    }

    private async Task RunGhostAsync(
        Codale.Agents.Local.LocalHelperModel model, string name, string language, string prefix, DocumentPosition at, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(GhostDelayMs, cts.Token);
            var text = await model.CompleteCodeAsync(name, language, prefix, cts.Token);
            if (text.Length == 0 || cts.IsCancellationRequested || _caret != at)
            {
                return;
            }

            _ghostFull = text;
            _ghostStops = FindGhostStops(text);
            _ghostStop = 0;
            _ghostText = text[.._ghostStops[0]];
            _ghostAt = at;
            CaretCanvas.Invalidate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Ghost text failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Offsets where the proposal should pause for the user: just after an opening quote or after a
    /// bracket/comma that still has arguments to fill in. The last stop is the whole text.
    /// </summary>
    private static List<int> FindGhostStops(string text)
    {
        var stops = new List<int>();
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (ch == '\\')
                {
                    i++;
                }
                else if (ch == quote)
                {
                    quote = '\0';
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
                stops.Add(i + 1);
            }
            else if ((ch is '(' or '[' or ',') && i + 1 < text.Length && text[i + 1] is not (')' or ']'))
            {
                stops.Add(i + 1);
            }
        }

        if (stops.Count == 0 || stops[^1] != text.Length)
        {
            stops.Add(text.Length);
        }

        return stops;
    }

    /// <summary>Ctrl+Shift+[ / ]: shows a proposal, or steps it back / forward through its pause points.</summary>
    private void CycleGhost(int step)
    {
        if (!GhostVisible)
        {
            ScheduleGhost();
            return;
        }

        _ghostStop = Math.Clamp(_ghostStop + step, 0, _ghostStops.Count - 1);
        _ghostText = _ghostFull[.._ghostStops[_ghostStop]];
        CaretCanvas.Invalidate();
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
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(i == 0 ? "" : "\n").Append(lines[i]);
        }

        return builder.ToString();
    }

    /// <summary>Draws the proposal after the caret on the caret layer, in the base text colour at low opacity.</summary>
    private void DrawGhost(Microsoft.Graphics.Canvas.CanvasDrawingSession session, double x, double y)
    {
        if (!GhostVisible || _textFormat is null)
        {
            return;
        }

        var color = _baseText;
        color.A = (byte)(color.A * 0.45);
        session.DrawText(_ghostText, (float)x, (float)y, color, _textFormat);
    }
}
