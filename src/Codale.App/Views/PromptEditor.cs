using System.Text;

using Codale.Core.Text;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Windows.ApplicationModel.DataTransfer;

namespace Codale.App.Views;

/// <summary>
/// The composer's text, with attachments, file mentions and links held inline as pills.
/// A pill is one embedded image in the RichEditBox - a single character the caret steps over
/// and Backspace removes whole - while <see cref="Text"/> reads it back as the "@file" mention
/// or URL it stands for, so the prompt that goes out is plain text, as before.
///
/// Positions in this API (<see cref="SelectionStart"/>, the caret given to <see cref="SetText"/>)
/// count in that plain text; the document counts each pill as one character.
/// </summary>
internal sealed class PromptEditor
{
    /// <summary>What RichEdit reports in place of an embedded object.</summary>
    private const char ObjectChar = '￼';

    private readonly RichEditBox _box;

    /// <summary>The pills in document order: the n-th object character is the n-th pill.</summary>
    private readonly List<PromptPill> _pills = [];

    /// <summary>The document's text as RichEdit reports it, pills as <see cref="ObjectChar"/>.</summary>
    private string _raw = "";

    private string? _text;

    /// <summary>Set while this class edits the document, so its own changes are not re-read as typing.</summary>
    private bool _editing;

    public PromptEditor(RichEditBox box)
    {
        _box = box;

        // The composer keeps its own undo history (see ChatTab); RichEdit's would bring back
        // pills behind this class's back.
        _box.Document.UndoLimit = 0;
        _box.DisabledFormattingAccelerators = DisabledFormattingAccelerators.All;
        _box.TextChanged += OnBoxTextChanged;
        _box.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _box.CopyingToClipboard += OnCopying;
        _box.CuttingToClipboard += OnCutting;
        // Handled events too: RichEdit takes the tap for itself to place the caret.
        _box.AddHandler(UIElement.TappedEvent, new TappedEventHandler(OnTapped), handledEventsToo: true);
        _box.ActualThemeChanged += (_, _) => RedrawPills();
    }

    /// <summary>Raised after the text changed, by typing or by <see cref="SetText"/>.</summary>
    public event EventHandler? TextChanged;

    public event EventHandler? SelectionChanged;

    /// <summary>A pill was clicked: preview the attachment.</summary>
    public event EventHandler<PromptPill>? PillInvoked;

    public RichEditBox Box => _box;

    public bool IsEnabled => _box.IsEnabled;

    public FocusState FocusState => _box.FocusState;

    public bool Focus(FocusState state) => _box.Focus(state);

    /// <summary>The prompt as plain text, each pill read as its mention or URL; line breaks are \r.</summary>
    public string Text => _text ??= BuildText();

    /// <summary>The pills, in order. Their offsets are not maintained; use <see cref="Text"/> for positions.</summary>
    public IReadOnlyList<PromptPill> Pills => _pills;

    public int SelectionStart
    {
        get => ToText(_box.Document.Selection.StartPosition);
        set => Select(value, 0);
    }

    public int SelectionLength
    {
        get
        {
            var selection = _box.Document.Selection;
            return ToText(selection.EndPosition) - ToText(selection.StartPosition);
        }
        set => Select(SelectionStart, value);
    }

    /// <summary>Selects a span of <see cref="Text"/>; an end inside a pill takes the whole pill.</summary>
    public void Select(int start, int length)
    {
        var text = Text;
        start = Math.Clamp(start, 0, text.Length);
        var end = Math.Clamp(start + Math.Max(0, length), start, text.Length);
        _box.Document.Selection.SetRange(ToDocument(start, after: length > 0 ? false : true), ToDocument(end, after: true));
    }

    /// <summary>
    /// Makes <see cref="Text"/> read <paramref name="text"/> with the caret at <paramref name="caret"/>.
    /// Only the span that differs is rewritten, so pills outside it stay as they are; inside it,
    /// the runs <see cref="PromptPills.Find"/> reports and <paramref name="pill"/> accepts become pills.
    /// </summary>
    public void SetText(string text, int caret, Func<PromptPill, bool>? pill = null)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        caret = Normalize(text[..caret]).Length;
        text = Normalize(text);

        var old = Text;
        var prefix = 0;
        var limit = Math.Min(old.Length, text.Length);
        while (prefix < limit && old[prefix] == text[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < limit - prefix && old[old.Length - 1 - suffix] == text[text.Length - 1 - suffix])
        {
            suffix++;
        }

        // The rewritten span never splits a pill: one it touches is replaced whole.
        var start = prefix;
        var end = old.Length - suffix;
        foreach (var (pillStart, pillEnd) in PillSpans())
        {
            if (pillStart < start && start < pillEnd)
            {
                start = pillStart;
            }

            if (pillStart < end && end < pillEnd)
            {
                end = pillEnd;
            }
        }

        var replacement = text[start..(text.Length - (old.Length - end))];
        var documentStart = ToDocument(start, after: false);
        var documentEnd = ToDocument(end, after: true);
        var pillsBefore = CountObjects(_raw, 0, documentStart);
        var pillsRemoved = CountObjects(_raw, documentStart, documentEnd);

        var inserted = new List<PromptPill>();
        _editing = true;
        try
        {
            var document = _box.Document;
            document.GetRange(documentStart, documentEnd).SetText(TextSetOptions.None, "");

            var position = documentStart;
            var at = 0;
            var found = pill is null ? [] : PromptPills.Find(replacement).Where(pill);
            foreach (var next in found)
            {
                position = InsertText(position, replacement[at..next.Start]);
                InsertPill(position, next);
                inserted.Add(next);
                position++;
                at = next.End;
            }

            InsertText(position, replacement[at..]);
        }
        finally
        {
            _editing = false;
        }

        _pills.RemoveRange(pillsBefore, pillsRemoved);
        _pills.InsertRange(pillsBefore, inserted);
        _raw = ReadDocument();
        _text = null;
        if (CountObjects(_raw, 0, _raw.Length) != _pills.Count)
        {
            // RichEdit refused a pill or merged something: re-read rather than drift.
            Resync();
        }

        var caretInDocument = ToDocument(Math.Min(caret, Text.Length), after: true);
        _box.Document.Selection.SetRange(caretInDocument, caretInDocument);
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Line breaks as the composer holds them: a bare \r, the way RichEdit stores paragraphs.</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\r").Replace('\n', '\r');

    private int InsertText(int position, string text)
    {
        if (text.Length == 0)
        {
            return position;
        }

        _box.Document.GetRange(position, position).SetText(TextSetOptions.None, text);

        // Normalized text maps one to one onto document characters.
        return position + text.Length;
    }

    private void InsertPill(int position, PromptPill pill)
    {
        var scale = (float)(_box.XamlRoot?.RasterizationScale ?? 1.0);
        var rendered = PromptPillBitmap.Render(pill, _box.ActualTheme, scale);
        // Not disposed here: RichEdit may still be reading it when InsertImage returns.
        var stream = new MemoryStream(rendered.Png);
        _box.Document.GetRange(position, position).InsertImage(
            rendered.Width,
            rendered.Height,
            PromptPillBitmap.Ascent,
            VerticalCharacterAlignment.Baseline,
            pill.Text,
            stream.AsRandomAccessStream());
    }

    /// <summary>Turns mentions already in the text as plain words into pills, where <paramref name="pill"/> accepts them.</summary>
    public void MakePills(Func<PromptPill, bool> pill) => Rebuild(pill);

    /// <summary>Repaints every pill for the current theme, keeping text, pills and caret.</summary>
    private void RedrawPills()
    {
        if (_pills.Count > 0)
        {
            Rebuild(_ => false);
        }
    }

    /// <summary>Lays the document out again: the pills it has, plus the runs <paramref name="extra"/> accepts.</summary>
    private void Rebuild(Func<PromptPill, bool> extra)
    {
        var text = Text;
        var caret = SelectionStart;
        var keep = _pills.Select(p => p.Text).ToHashSet(StringComparer.Ordinal);
        _editing = true;
        try
        {
            _box.Document.SetText(TextSetOptions.None, "");
        }
        finally
        {
            _editing = false;
        }

        _pills.Clear();
        _raw = "";
        _text = null;
        SetText(text, caret, p => keep.Contains(p.Text) || extra(p));
    }

    private void OnBoxTextChanged(object sender, RoutedEventArgs e)
    {
        if (_editing)
        {
            return;
        }

        var previous = _raw;
        Resync();
        if (_raw != previous)
        {
            TextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Brings <see cref="_pills"/> in line with the document after an edit RichEdit made itself
    /// (typing, Backspace over a pill, a drag inside the box). The changed span is found by
    /// comparing ends; pills in the old span are gone, and an object that appears in the new one
    /// is recognised by its alt text - which is its mention - or else removed.
    /// </summary>
    private void Resync()
    {
        var current = ReadDocument();
        var old = _raw;

        // The caret ends an edit, so the unchanged start never runs past it: with two pills side
        // by side, Delete before the first must take the first, though the texts alone would
        // match either way.
        var limit = Math.Min(old.Length, current.Length);
        var prefixLimit = Math.Min(limit, Math.Max(0, _box.Document.Selection.StartPosition));
        var prefix = 0;
        while (prefix < prefixLimit && old[prefix] == current[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < limit - prefix && old[old.Length - 1 - suffix] == current[current.Length - 1 - suffix])
        {
            suffix++;
        }

        // Objects outside the changed span are unchanged; the ones counted up to it are the pills before it.
        var before = CountObjects(old, 0, prefix);
        var removed = CountObjects(old, prefix, old.Length - suffix);
        removed = Math.Min(removed, Math.Max(0, _pills.Count - before));

        var added = new List<PromptPill>();
        var strays = new List<int>();
        for (var i = prefix; i < current.Length - suffix; i++)
        {
            if (current[i] != ObjectChar)
            {
                continue;
            }

            _box.Document.GetRange(i, i + 1).GetText(TextGetOptions.UseObjectText, out var alt);
            if (PromptPills.Whole(alt ?? "") is { } pill)
            {
                added.Add(pill);
            }
            else
            {
                strays.Add(i);
            }
        }

        if (before <= _pills.Count)
        {
            _pills.RemoveRange(before, removed);
            _pills.InsertRange(before, added);
        }

        if (strays.Count > 0)
        {
            // An object that is not a pill (pasted from elsewhere) has no text to send: drop it.
            _editing = true;
            try
            {
                for (var i = strays.Count - 1; i >= 0; i--)
                {
                    _box.Document.GetRange(strays[i], strays[i] + 1).SetText(TextSetOptions.None, "");
                }
            }
            finally
            {
                _editing = false;
            }

            current = ReadDocument();
        }

        // Out of step after all (should not happen): keep the count right so Text never throws.
        var objects = CountObjects(current, 0, current.Length);
        while (_pills.Count > objects)
        {
            _pills.RemoveAt(_pills.Count - 1);
        }

        while (_pills.Count < objects)
        {
            _pills.Add(new PromptPill(0, 0, PromptPillKind.Text, "?", "") { Text = "" });
        }

        _raw = current;
        _text = null;
    }

    private string ReadDocument()
    {
        _box.Document.GetText(TextGetOptions.None, out var text);
        text ??= "";

        // RichEdit always ends the document with a paragraph mark of its own.
        return text.EndsWith('\r') ? text[..^1] : text;
    }

    private string BuildText()
    {
        var builder = new StringBuilder(_raw.Length + 32);
        var pill = 0;
        foreach (var c in _raw)
        {
            if (c == ObjectChar)
            {
                builder.Append(pill < _pills.Count ? _pills[pill].Text : "");
                pill++;
            }
            else
            {
                // Shift+Enter in RichEdit is a soft line break; to the prompt it is a newline.
                builder.Append(c == '\v' ? '\r' : c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Where each pill sits in <see cref="Text"/>.</summary>
    private IEnumerable<(int Start, int End)> PillSpans()
    {
        var position = 0;
        var pill = 0;
        foreach (var c in _raw)
        {
            if (c == ObjectChar)
            {
                var length = pill < _pills.Count ? _pills[pill].Text.Length : 0;
                yield return (position, position + length);
                position += length;
                pill++;
            }
            else
            {
                position++;
            }
        }
    }

    /// <summary>A document position as an offset into <see cref="Text"/>.</summary>
    private int ToText(int documentPosition)
    {
        documentPosition = Math.Clamp(documentPosition, 0, _raw.Length);
        var position = 0;
        var pill = 0;
        for (var i = 0; i < documentPosition; i++)
        {
            if (_raw[i] == ObjectChar)
            {
                position += pill < _pills.Count ? _pills[pill].Text.Length : 0;
                pill++;
            }
            else
            {
                position++;
            }
        }

        return position;
    }

    /// <summary>An offset into <see cref="Text"/> as a document position; one inside a pill goes to its start or end.</summary>
    private int ToDocument(int textPosition, bool after)
    {
        var position = 0;
        var pill = 0;
        for (var i = 0; i < _raw.Length; i++)
        {
            if (position >= textPosition)
            {
                return i;
            }

            if (_raw[i] == ObjectChar)
            {
                var length = pill < _pills.Count ? _pills[pill].Text.Length : 0;
                pill++;
                if (textPosition < position + length)
                {
                    return after ? i + 1 : i;
                }

                position += length;
            }
            else
            {
                position++;
            }
        }

        return _raw.Length;
    }

    private static int CountObjects(string text, int start, int end)
    {
        var count = 0;
        for (var i = Math.Max(0, start); i < Math.Min(end, text.Length); i++)
        {
            if (text[i] == ObjectChar)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The selection as prompt text, so a copied pill pastes back as its mention or link.</summary>
    private string SelectedText()
    {
        var selection = _box.Document.Selection;
        var start = ToText(selection.StartPosition);
        var end = ToText(selection.EndPosition);
        return Text[start..Math.Max(start, end)].Replace("\r", "\r\n");
    }

    private void OnCopying(RichEditBox sender, TextControlCopyingToClipboardEventArgs e)
    {
        e.Handled = true;
        CopySelection();
    }

    private void OnCutting(RichEditBox sender, TextControlCuttingToClipboardEventArgs e)
    {
        e.Handled = true;
        if (CopySelection())
        {
            // Through the selection, as typing would, so the change is read back by Resync.
            _box.Document.Selection.SetText(TextSetOptions.None, "");
        }
    }

    private bool CopySelection()
    {
        var text = SelectedText();
        if (text.Length == 0)
        {
            return false;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (PillAt(e.GetPosition(_box)) is { } pill)
        {
            e.Handled = true;
            PillInvoked?.Invoke(this, pill);
        }
    }

    /// <summary>The pill drawn under a point in the box, if any.</summary>
    private PromptPill? PillAt(Windows.Foundation.Point point)
    {
        var hit = _box.Document.GetRangeFromPoint(point, PointOptions.ClientCoordinates);
        foreach (var candidate in new[] { hit.StartPosition, hit.StartPosition - 1 })
        {
            if (candidate < 0 || candidate >= _raw.Length || _raw[candidate] != ObjectChar)
            {
                continue;
            }

            _box.Document.GetRange(candidate, candidate + 1).GetRect(PointOptions.ClientCoordinates, out var rect, out _);
            if (rect.Contains(point))
            {
                var index = CountObjects(_raw, 0, candidate);
                return index < _pills.Count ? _pills[index] : null;
            }
        }

        return null;
    }
}
