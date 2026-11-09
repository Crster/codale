using System.Diagnostics;
using System.Text;

using Codale.App.Services;

using Microsoft.UI.Xaml;

namespace Codale.App.Controls;

/// <summary>
/// Ghost text: Ctrl+Space asks the fill-in-the-middle model for the rest of the line, drawn dimmed after
/// the caret. The model sees the code on both sides of the caret. Tab accepts it; Ctrl+Space again asks
/// for a different one; any other key or a click drops it. Only runs when Settings sets a completion model.
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

    /// <summary>Where the buffer's file lives, for the model's prompt; null for an unsaved buffer.</summary>
    public Func<string?>? FilePathProvider { get; set; }

    /// <summary>
    /// Comment header for the model's prompt, from the code just before the caret and the language id:
    /// the file's path and language and what the project index knows about the names used nearby.
    /// </summary>
    public Func<string, string?, string>? GhostContext { get; set; }

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
        if (_languageId is null || BackgroundProviders.Completions is not { } model || _anchor != _caret)
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

        // The project's context (file, language, declarations used nearby) leads the prompt as comments.
        var nearby = prefix.Length > 1500 ? prefix[^1500..] : prefix;
        string header;
        try
        {
            header = GhostContext?.Invoke(nearby, _languageId) ?? "";
        }
        catch (Exception)
        {
            header = "";
        }

        prefix = header + prefix;
        var suffix = SuffixAfter(at);
        var cts = _ghostCts = new CancellationTokenSource();
        _ = RunGhostAsync(model, prefix, suffix, at, cts);
    }

    private async Task RunGhostAsync(
        Codale.Core.Background.IFimCompleter model, string prefix, string suffix, DocumentPosition at, CancellationTokenSource cts)
    {
        try
        {
            var text = await model.CompleteAsync(prefix, suffix, _ghostSeen.ToList(), cts.Token);
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
        catch (Exception)
        {
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
