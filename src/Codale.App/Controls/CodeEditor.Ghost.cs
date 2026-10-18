using System.Diagnostics;
using System.Text;

using Codale.App.Services;

using Microsoft.UI.Xaml;

namespace Codale.App.Controls;

/// <summary>
/// Ghost text: Ctrl+Space asks the helper model for the rest of the line, drawn dimmed after the caret.
/// Tab accepts it; Ctrl+Space again asks for a different one; any other key or a click drops it.
/// Only runs when Settings runs background jobs on an API model.
/// </summary>
public sealed partial class CodeEditor
{
    private const int GhostPrefixChars = 4500;

    private string _ghostText = "";
    private DocumentPosition _ghostAt;
    private CancellationTokenSource? _ghostCts;
    private readonly List<string> _ghostSeen = [];
    private DocumentPosition _ghostSeenAt;

    /// <summary>Where the buffer's file lives, for the model's prompt; null for an unsaved buffer.</summary>
    public Func<string?>? FilePathProvider { get; set; }

    private bool GhostVisible => _ghostText.Length > 0 && _ghostAt == _caret && _anchor == _caret;

    private void ClearGhost()
    {
        _ghostCts?.Cancel();
        _ghostSeen.Clear();
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

    /// <summary>Asks for a one-line proposal at the caret; asking again at the same spot asks for a different one.</summary>
    private void RequestGhost()
    {
        _ghostCts?.Cancel();
        if (_languageId is null || BackgroundHelper.Completions is not { } model || _anchor != _caret)
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

        var path = FilePathProvider?.Invoke();
        var name = string.IsNullOrEmpty(path) ? "untitled" : Path.GetFileName(path);
        var language = _languageId;
        var cts = _ghostCts = new CancellationTokenSource();
        _ = RunGhostAsync(model, name, language, prefix, at, cts);
    }

    private async Task RunGhostAsync(
        Codale.Agents.Completion.InlineCompleter model, string name, string language, string prefix, DocumentPosition at, CancellationTokenSource cts)
    {
        try
        {
            var text = await model.CompleteCodeAsync(name, language, prefix, _ghostSeen.ToList(), cts.Token);
            if (text.Length == 0 || cts.IsCancellationRequested || _caret != at)
            {
                return;
            }

            _ghostText = text;
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
