using System.Diagnostics;

using Codale.App.Services;
using Codale.Core.Syntax;

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.System;
using Windows.UI.Core;
using Windows.UI.Text;

namespace Codale.App.Controls;

/// <summary>
/// Codale's own native WinUI 3 code editor: a Win2D canvas that draws the buffer,
/// the gutter, the active line and the selection, driven by a line-list document and
/// a hand-written tokenizer (see <see cref="CodeTokenizer"/>).
///
/// The caret blinks by itself, the active line is one subtle full-width band, and colours
/// come from the theme resources so dark and light both read.
/// </summary>
public sealed partial class CodeEditor : UserControl
{
    /// <summary>How far zooming goes, in points of font size.</summary>
    private const float MinFontSize = 8;
    private const float MaxFontSize = 40;

    private const float DefaultFontSize = 13;
    private const string EditorFontFamily = "Consolas";
    private const string IconFontFamily = "Segoe MDL2 Assets";

    /// <summary>The caret is 2 dip wide, a hair wider than a stroke so it reads.</summary>
    private const float CaretWidth = 2;

    /// <summary>
    /// The active-line band, caret and selection sit this far below the top of the
    /// line box, so they land on the glyphs: the text baseline sits low in the box
    /// because the line spacing is taller than the font's natural height.
    /// </summary>
    private const double LineHighlightOffset = 1;

    /// <summary>Padding left of the text, and between gutter numbers and the text.</summary>
    private const double TextLeftPad = 8;

    private readonly TextDocument _doc = new();
    private DocumentPosition _caret;
    private DocumentPosition _anchor;

    private CodeSyntax _syntax = CodeSyntax.None;
    private string? _languageId;

    /// <summary>Whether the gutter draws line numbers (Settings).</summary>
    public bool ShowLineNumbers { get; set; } = true;

    /// <summary>Whether tokens colour the text, or everything draws plain (Settings).</summary>
    public bool EnableSyntaxHighlighting { get; set; } = true;

    /// <summary>Whether long lines fold at the viewport edge instead of scrolling sideways (Settings).</summary>
    public bool WordWrap { get; set; }

    /// <summary>
    /// The font size Settings last handed this editor, so a live change can tell a tab
    /// that still follows the setting apart from one the user zoomed.
    /// </summary>
    private double _settingsFontSize;

    private double _charWidth = 8;
    private double _lineHeight = 18;
    private double _maxLineWidth;

    private CanvasTextFormat? _textFormat;
    private CanvasTextFormat? _lineNumberFormat;

    private double _verticalOffset;
    private double _horizontalOffset;


    // Undo records edits as deltas (range, removed text, inserted text), so a step costs
    // what the edit touched rather than a copy of the whole file. Continuous typing
    // coalesces into one step; the step remembers the caret from before it started.
    private const int UndoLimit = 500;

    private readonly record struct EditRecord(
        DocumentPosition Start, DocumentPosition RemovedEnd, string Removed, string Inserted, DocumentPosition InsertedEnd);

    private sealed class UndoStep(DocumentPosition caretBefore)
    {
        public DocumentPosition CaretBefore { get; } = caretBefore;
        public List<EditRecord> Edits { get; } = [];
    }

    private readonly List<UndoStep> _undo = [];
    private readonly List<UndoStep> _redo = [];
    private DateTime _lastEditUtc = DateTime.MinValue;

    private bool _caretVisible = true;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _caretTimer;

    private Windows.UI.Color _baseText = Microsoft.UI.Colors.Black;
    private Windows.UI.Color _lineNumberText = Microsoft.UI.Colors.Gray;
    private Windows.UI.Color _selectionColor = Microsoft.UI.Colors.LightBlue;
    private Windows.UI.Color _activeLineColor = Microsoft.UI.Colors.LightGray;
    private Windows.UI.Color _caretColor = Microsoft.UI.Colors.Black;

    /// <summary>Solid brushes are made against the draw device and reused across frames.</summary>
    private readonly Dictionary<Windows.UI.Color, CanvasSolidColorBrush> _brushes = [];

    /// <summary>The caret layer's brush, owned by CaretCanvas rather than the brush cache.</summary>
    private CanvasSolidColorBrush? _caretBrush;

    private CanvasSolidColorBrush Brush(CanvasDrawingSession session, Windows.UI.Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new CanvasSolidColorBrush(session, color);
            _brushes[color] = brush;
        }

        return brush;
    }

    /// <summary>
    /// Per-line render cache: tokens plus the state chain that makes incremental
    /// window tokenization correct, and the laid-out text once it has been drawn.
    /// </summary>
    private sealed class LineCache
    {
        public string? Source;
        public string Display = "";
        public CodeTokenState StateIn;
        public CodeTokenState StateOut;
        public readonly List<CodeToken> Tokens = [];

        /// <summary>One laid-out text per visual row; a non-wrapped line has exactly one.</summary>
        public readonly List<CanvasTextLayout> RowLayouts = [];
    }

    private readonly List<LineCache> _lineCache = [];

    // Visual rows: a logical line folds into one or more rows of _wrapCols columns each
    // (the sentinel NoWrapCols makes every line a single row when wrap is off). The row
    // map is the prefix sum of rows-per-line and everything that turns a y into a line
    // or a line into a y goes through it.
    private const int NoWrapCols = int.MaxValue;

    private int _wrapCols = NoWrapCols;
    private bool _rowMapDirty = true;
    private readonly List<int> _rowOffsets = [];
    private int _totalRows = 1;

    /// <summary>How many visual rows an expanded column count needs (never zero).</summary>
    private int RowCountOf(int expandedLength) => expandedLength == 0 ? 1 : (expandedLength - 1) / _wrapCols + 1;

    /// <summary>The first visual row of a logical line.</summary>
    private int RowOffsetOf(int line) => _rowOffsets[line];

    /// <summary>Splits an expanded column into (row within the line, column within that row).</summary>
    private (int RowInLine, int ColInRow) Fold(int expandedCol) => (expandedCol / _wrapCols, expandedCol % _wrapCols);

    /// <summary>
    /// Rebuilds the row map when dirty. The wrap width follows the viewport, so a
    /// resize that changes it also drops the layout cache; only the layouts are
    /// affected, tokens are per logical line and stay.
    /// </summary>
    private void EnsureRowMap()
    {
        if (!_rowMapDirty)
        {
            return;
        }

        _rowMapDirty = false;
        var wrapCols = WordWrap
            ? Math.Max(8, (int)((Canvas.ActualWidth - TextX - 40) / Math.Max(1, _charWidth)))
            : NoWrapCols;
        if (wrapCols != _wrapCols)
        {
            ClearLayouts();
            _maxLineWidth = 0;
        }

        _wrapCols = wrapCols;
        _rowOffsets.Clear();
        var total = 0;
        for (var i = 0; i < _doc.LineCount; i++)
        {
            _rowOffsets.Add(total);
            var raw = _doc.Lines[i];
            total += RowCountOf(TextDocument.ExpandedColFromRaw(raw, raw.Length));
        }

        _totalRows = Math.Max(1, total);
    }

    /// <summary>The logical line and row-within-line a visual row belongs to (binary search).</summary>
    private (int Line, int RowInLine) RowAt(int visualRow)
    {
        var lo = 0;
        var hi = _rowOffsets.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_rowOffsets[mid] <= visualRow)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (lo, visualRow - _rowOffsets[lo]);
    }

    /// <summary>The buffer-space visual row a position starts in, for overlays mapping lines to y.</summary>
    public int VisualRowOfPosition(int line, int col)
    {
        EnsureRowMap();
        line = Math.Clamp(line, 0, _doc.LineCount - 1);
        var rawLine = _doc.GetLine(line);
        var (rowInLine, _) = Fold(TextDocument.ExpandedColFromRaw(rawLine, Math.Clamp(col, 0, rawLine.Length)));
        return _rowOffsets[line] + rowInLine;
    }

    /// <summary>
    /// The row box a position is drawn in, in dips relative to this control at the current
    /// scroll - for overlays that float next to the text, like the Ask panel.
    /// </summary>
    public Windows.Foundation.Rect PositionBounds(int line, int col)
    {
        EnsureMetrics();
        EnsureRowMap();
        line = Math.Clamp(line, 0, _doc.LineCount - 1);
        var rawLine = _doc.GetLine(line);
        var (rowInLine, colInRow) = Fold(TextDocument.ExpandedColFromRaw(rawLine, Math.Clamp(col, 0, rawLine.Length)));
        var x = TextX + colInRow * _charWidth - _horizontalOffset;
        var y = (_rowOffsets[line] + rowInLine) * _lineHeight - _verticalOffset;
        return new Windows.Foundation.Rect(x, y, CaretWidth, _lineHeight);
    }

    /// <summary>How many visual rows a logical line folds into.</summary>
    public int VisualRowsOf(int line)
    {
        EnsureRowMap();
        line = Math.Clamp(line, 0, _doc.LineCount - 1);
        var raw = _doc.Lines[line];
        return RowCountOf(TextDocument.ExpandedColFromRaw(raw, raw.Length));
    }

    public CodeEditor()
    {
        InitializeComponent();

        RefreshThemeColors();
        ActualThemeChanged += (_, _) =>
        {
            RefreshThemeColors();
            if (_caretBrush is { } caret)
            {
                caret.Color = _caretColor;
            }

            ClearLayouts();
            InvalidateAll();
        };

        _caretTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _caretTimer.Interval = TimeSpan.FromMilliseconds(530);
        _caretTimer.Tick += (_, _) =>
        {
            // The blink redraws the caret layer only: a full viewport repaint twice a
            // second while the editor sits idle was pure burn.
            _caretVisible = !_caretVisible;
            CaretCanvas.Invalidate();
        };

        GotFocus += (_, _) =>
        {
            _caretVisible = true;
            InvalidateAll();
            _caretTimer.Start();
        };
        LostFocus += (_, _) =>
        {
            _caretTimer.Stop();
            _caretVisible = false;
            InvalidateAll();
        };

        // Tunnelling: the focus system takes Tab before a bubbling KeyDown fires.
        PreviewKeyDown += OnEditorKeyDown;
        CharacterReceived += OnEditorCharacterReceived;

        // FontSize changes (zoom, or XAML styling) re-measure everything.
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => OnFontMetricsChanged());

        // Settings own the defaults; zoom still overrides per tab until the next
        // ApplyAppSettings. EditorTab must not set FontSize in XAML, or it would
        // overwrite this.
        _settingsFontSize = Math.Clamp(AppSettings.EditorFontSize, MinFontSize, MaxFontSize);
        FontSize = _settingsFontSize;
        ShowLineNumbers = AppSettings.EditorLineNumbers;
        EnableSyntaxHighlighting = AppSettings.EditorHighlighting;
        WordWrap = AppSettings.EditorWordWrap;
    }

    // ------------------------------------------------------------------ public surface


    /// <summary>The whole buffer text, joined with CRLF.</summary>
    public string GetText() => _doc.GetText();

    public int NumberOfLines => _doc.LineCount;

    /// <summary>The height of one rendered line in dips; overlays key off it.</summary>
    public double ActualLineHeight => _lineHeight;

    /// <summary>Vertical scroll offset in dips from the top of the buffer.</summary>
    public double VerticalScroll => _verticalOffset;

    /// <summary>Scrolls so <paramref name="offset"/> dips of the buffer are at the top.</summary>
    public void SetVerticalScroll(double offset) =>
        Scroller.ChangeView(null, Math.Max(0, offset), null, disableAnimation: true);

    /// <summary>The caret position: line 0-based, column 1-based.</summary>
    public EditorCursorPosition CursorPosition =>
        new(_caret.Line, _caret.Col + 1);

    public bool HasSelection => _anchor != _caret;

    /// <summary>
    /// The selection's character count, computed arithmetically. Building the whole
    /// SelectedText string just to read its length used to allocate the selection
    /// afresh on every pointer move during a drag.
    /// </summary>
    public int SelectedTextLength
    {
        get
        {
            var (start, end) = OrderedSelection();
            if (start == end)
            {
                return 0;
            }

            if (start.Line == end.Line)
            {
                return end.Col - start.Col;
            }

            var count = _doc.GetLine(start.Line).Length - start.Col;
            for (var line = start.Line + 1; line < end.Line; line++)
            {
                count += _doc.GetLine(line).Length + Environment.NewLine.Length;
            }

            return count + Environment.NewLine.Length + end.Col;
        }
    }

    /// <summary>The selected text, or an empty string when nothing is selected.</summary>
    public string SelectedText
    {
        get
        {
            var (start, end) = OrderedSelection();
            if (start == end)
            {
                return string.Empty;
            }

            if (start.Line == end.Line)
            {
                return _doc.GetLine(start.Line)[start.Col..end.Col];
            }

            var parts = new List<string> { _doc.GetLine(start.Line)[start.Col..] };
            for (var line = start.Line + 1; line < end.Line; line++)
            {
                parts.Add(_doc.GetLine(line));
            }

            parts.Add(_doc.GetLine(end.Line)[..end.Col]);
            return string.Join(Environment.NewLine, parts);
        }
    }

    /// <summary>The selection as character offsets into <see cref="GetText"/>; both equal the caret when nothing is selected.</summary>
    public (int Start, int End) SelectionOffsets
    {
        get
        {
            var (start, end) = OrderedSelection();
            return (OffsetOf(start), OffsetOf(end));
        }
    }

    private int OffsetOf(DocumentPosition position)
    {
        var offset = position.Col;
        for (var line = 0; line < position.Line; line++)
        {
            offset += _doc.GetLine(line).Length + 2;
        }

        return offset;
    }

    // Menu-facing wrappers over the keyboard paths.
    public void CopyToClipboard() => CopySelection();

    public void CutToClipboard() => CutSelection();

    public void PasteFromClipboard() => _ = PasteAsync();

    public void SelectEverything() => SelectAll();

    public void DeleteSelection() => Delete();

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Replaces the selection (or inserts at the caret) as one undo step.</summary>
    public void ReplaceSelection(string text) => InsertText(text.Replace("\r\n", "\n").Replace('\r', '\n'));

    /// <summary>
    /// Swaps the whole buffer for new text as one undo step, keeping the caret near its
    /// old line. Unlike <see cref="LoadText"/> this stays undoable.
    /// </summary>
    public void ReplaceAllText(string text)
    {
        var line = _caret.Line;
        BeginUndoStep(canCoalesce: false);
        var last = _doc.LineCount - 1;
        Edit(new DocumentPosition(0, 0), new DocumentPosition(last, _doc.GetLine(last).Length), text);
        _caret = _anchor = new DocumentPosition(Math.Min(line, _doc.LineCount - 1), 0);
        AfterEdit();
    }

    /// <summary>True when the position (line 0-based, column 0-based) lies inside the selection.</summary>
    private bool SelectionContains(DocumentPosition position)
    {
        var (start, end) = OrderedSelection();
        return start != end && !(position < start) && !(position > end);
    }

    /// <summary>Replaces the buffer. Undo history restarts; the caret goes to the top.</summary>
    public void LoadText(string text)
    {
        _doc.Load(text);
        _undo.Clear();
        _redo.Clear();
        _caret = _anchor = default;
        ResetAfterDocChange();
        RaiseTextChanged();
        RaiseSelectionChanged();
    }

    /// <summary>Puts the caret (and optionally the selection anchor) at a position.</summary>
    public void SetCursorPosition(int line, int col, bool select = false, bool scrollIntoView = true)
    {
        _caret = ClampPosition(new(Math.Max(0, line), Math.Max(0, col)));
        if (!select)
        {
            _anchor = _caret;
        }

        if (scrollIntoView)
        {
            ScrollIntoView();
        }

        InvalidateAll();
        RaiseSelectionChanged();
    }

    /// <summary>Scrolls a line into the vertical middle of the view.</summary>
    public void ScrollLineToCenter(int line)
    {
        EnsureRowMap();
        var target = (RowOffsetOf(Math.Clamp(line, 0, _doc.LineCount - 1)) + 0.5) * _lineHeight - Canvas.ActualHeight / 2;
        Scroller.ChangeView(null, Math.Max(0, target), null);
    }

    /// <summary>Moves the view so the caret's column is visible.</summary>
    public void ScrollIntoViewHorizontally() => ScrollIntoView();

    /// <summary>Sets the language the buffer is coloured with.</summary>
    public void SelectSyntaxHighlightingById(string? languageId)
    {
        _languageId = languageId;
        _syntax = LegacySyntax.For(languageId);
        ClearTokens();
        InvalidateAll();
    }

    /// <summary>The id of the language the buffer is coloured with, or null for plain text.</summary>
    public string? LanguageId => _languageId;

    // ------------------------------------------------------------------ events

    public event EditorTextEventHandler? TextChanged;
    public event EditorSelectionChangedEventHandler? SelectionChanged;
    public event EventHandler? ZoomChanged;

    /// <summary>Raised whenever the scroll offsets changed, so overlays can move with the text.</summary>
    public event EventHandler? ScrollChanged;

    private void RaiseTextChanged() => TextChanged?.Invoke(this);

    private void RaiseSelectionChanged() =>
        SelectionChanged?.Invoke(this, new EditorSelectionEventArgs
        {
            LineNumber = _caret.Line,
            CharacterPositionInLine = _caret.Col + 1,
        });

    // ------------------------------------------------------------------ theme colours

    private void RefreshThemeColors()
    {
        var resources = Application.Current.Resources;
        Windows.UI.Color FromBrush(string key) => ((SolidColorBrush)resources[key]).Color;

        _baseText = FromBrush("TextFillColorPrimaryBrush");
        _lineNumberText = FromBrush("TextFillColorTertiaryBrush");
        _caretColor = _baseText;

        var accent = FromBrush("AccentFillColorDefaultBrush");
        _selectionColor = Windows.UI.Color.FromArgb(0x42, accent.R, accent.G, accent.B);

        // The active line: a whisper of the text colour, so it works on any theme
        // without competing with the selection.
        var a = (byte)(ActualTheme == ElementTheme.Dark ? 0x0E : 0x0C);
        _activeLineColor = ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(a, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(a, 0x00, 0x00, 0x00);
    }

    // ------------------------------------------------------------------ metrics

    private double GutterWidth
    {
        get
        {
            if (!ShowLineNumbers)
            {
                return 0;
            }

            var digits = Math.Max(2, _doc.LineCount.ToString().Length);
            return TextLeftPad * 2 + digits * _charWidth;
        }
    }

    private double TextX => GutterWidth + TextLeftPad;

    /// <summary>Makes sure the fonts and monospace metrics match the current FontSize.</summary>
    private void EnsureMetrics()
    {
        if (_textFormat is not null)
        {
            return;
        }

        var fontSize = (float)(double.IsFinite(FontSize) && FontSize > 0 ? FontSize : DefaultFontSize);
        _lineHeight = Math.Round(fontSize * 1.38);

        _textFormat = new CanvasTextFormat
        {
            FontFamily = EditorFontFamily,
            FontSize = fontSize,
            FontWeight = new FontWeight { Weight = 400 },
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            LineSpacing = (float)_lineHeight,
            LineSpacingBaseline = (float)Math.Round(_lineHeight * 0.8),
        };
        _lineNumberFormat = new CanvasTextFormat
        {
            FontFamily = EditorFontFamily,
            FontSize = fontSize,
            WordWrapping = CanvasWordWrapping.NoWrap,
            HorizontalAlignment = CanvasHorizontalAlignment.Right,
            LineSpacing = (float)_lineHeight,
            LineSpacingBaseline = (float)Math.Round(_lineHeight * 0.8),
        };

        using var measure = new CanvasTextLayout(Canvas, new string('M', 100), _textFormat, 65536f, (float)_lineHeight * 2);

        // LayoutBounds is advance-based; DrawBounds is ink-only and under-measures, which
        // drifts the selection and caret further from the glyphs the longer the line is.
        _charWidth = measure.LayoutBounds.Width / 100;
        if (_charWidth <= 0)
        {
            _charWidth = fontSize * 0.55;
        }
    }

    private void OnFontMetricsChanged()
    {
        ClearLayouts();
        _maxLineWidth = 0;
        _textFormat?.Dispose();
        _lineNumberFormat?.Dispose();
        _textFormat = null;
        _lineNumberFormat = null;
        _rowMapDirty = true;
        UpdateExtent();
        InvalidateAll();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies the Settings-window values to this editor: the configured font size
    /// (unless the user zoomed this tab away from the last setting), tab width, line
    /// numbers and highlighting. Called for open editors when a setting changes.
    /// </summary>
    public void ApplyAppSettings()
    {
        var tabWidthChanged = TextDocument.TabWidth != AppSettings.EditorTabWidth;
        TextDocument.TabWidth = AppSettings.EditorTabWidth;

        var size = Math.Clamp(AppSettings.EditorFontSize, MinFontSize, MaxFontSize);
        if (double.IsFinite(FontSize) && Math.Abs(FontSize - _settingsFontSize) < 0.1)
        {
            // The tab still follows the setting, so it moves with it; a zoomed tab
            // keeps the size the user picked.
            FontSize = size;
        }

        _settingsFontSize = size;

        var wrapChanged = WordWrap != AppSettings.EditorWordWrap;
        WordWrap = AppSettings.EditorWordWrap;
        if (wrapChanged)
        {
            _rowMapDirty = true;
        }

        var highlightingChanged = EnableSyntaxHighlighting != AppSettings.EditorHighlighting;
        ShowLineNumbers = AppSettings.EditorLineNumbers;
        EnableSyntaxHighlighting = AppSettings.EditorHighlighting;

        if (tabWidthChanged || highlightingChanged)
        {
            // Tab expansion and token colours live in the render cache; drop it so
            // the visible lines rebuild from the new values.
            ClearTokens();
            _maxLineWidth = 0;
        }

        UpdateExtent();
        InvalidateAll();
    }

    // ------------------------------------------------------------------ token cache

    /// <summary>
    /// Number of lines at the top of the document known to be token-cache-valid, i.e.
    /// every line below this index may need re-lexing. Skips the per-frame walk from
    /// line 0, which at the bottom of a large file was O(document) on every draw.
    /// </summary>
    private int _stableLine;

    private void ClearTokens()
    {
        ClearLayouts();
        _lineCache.Clear();
        _stableLine = 0;
    }

    /// <summary>
    /// Marks everything from <paramref name="line"/> down as needing a re-lex, keeping
    /// the cache and layouts above it. A localized edit (typing, undo, redo) only
    /// invalidates the lines it touched, so the next draw rebuilds a handful of lines
    /// instead of re-shaping the whole viewport.
    /// </summary>
    private void InvalidateTokensFrom(int line)
    {
        if (line < _stableLine)
        {
            _stableLine = Math.Clamp(line, 0, _doc.LineCount);
        }
    }

    private void ClearLayouts()
    {
        foreach (var entry in _lineCache)
        {
            foreach (var layout in entry.RowLayouts)
            {
                layout.Dispose();
            }

            entry.RowLayouts.Clear();
        }
    }

    /// <summary>
    /// Walks the token cache from the top, reusing every line whose cached entry was
    /// built from the same line string (lines are immutable, so reference equality
    /// means unchanged) with the same incoming state, and re-tokenizing the rest.
    /// An edit therefore re-lexes only the touched line, plus following lines only
    /// while a multi-line construct such as a block comment keeps changing their state.
    /// </summary>
    private void EnsureTokens(int lastLine)
    {
        var docLines = _doc.LineCount;
        if (lastLine >= docLines)
        {
            lastLine = docLines - 1;
        }

        if (_stableLine > docLines)
        {
            _stableLine = docLines;
        }

        var state = new CodeTokenState();
        var line = 0;

        if (_stableLine > 0)
        {
            if (lastLine < _stableLine)
            {
                // Everything the viewport asks for is already verified: no walk at all.
                line = lastLine + 1;
            }
            else
            {
                // Jump to the watermark. The state entering it is exactly what the
                // previous verified line produced; the reference checks below still
                // hold it honest.
                line = _stableLine;
                state = line > 0 && line - 1 < _lineCache.Count
                    ? _lineCache[line - 1].StateOut
                    : state;
            }
        }

        for (; line <= lastLine && line < docLines; line++)
        {
            var source = _doc.Lines[line];
            if (line < _lineCache.Count
                && ReferenceEquals(_lineCache[line].Source, source)
                && _lineCache[line].StateIn.SameAs(state))
            {
                state = _lineCache[line].StateOut;
                continue;
            }

            while (_lineCache.Count <= line)
            {
                _lineCache.Add(new LineCache());
            }

            var entry = _lineCache[line];
            foreach (var layout in entry.RowLayouts)
            {
                layout.Dispose();
            }

            entry.RowLayouts.Clear();
            entry.Source = source;
            entry.StateIn = state;
            entry.Display = TextDocument.ExpandTabs(source);
            entry.StateOut = CodeTokenizer.TokenizeLine(entry.Display, _languageId, _syntax, state, entry.Tokens);
            state = entry.StateOut;
        }

        if (lastLine + 1 > _stableLine)
        {
            _stableLine = lastLine + 1;
        }

        if (_lineCache.Count > _doc.LineCount)
        {
            for (var i = _doc.LineCount; i < _lineCache.Count; i++)
            {
                foreach (var layout in _lineCache[i].RowLayouts)
                {
                    layout.Dispose();
                }
            }

            _lineCache.RemoveRange(_doc.LineCount, _lineCache.Count - _doc.LineCount);
        }
    }

    /// <summary>
    /// Builds (or reuses) a line's per-row text layouts. With wrap off a line has one
    /// row covering the whole expanded text; with wrap on, row r is the substring
    /// starting at column r * _wrapCols. Token colours index the expanded line, so
    /// they are shifted back by the row's start column.
    /// </summary>
    private void EnsureRowLayouts(LineCache entry)
    {
        var display = entry.Display;
        var rows = RowCountOf(display.Length);
        if (entry.RowLayouts.Count == rows)
        {
            return;
        }

        foreach (var layout in entry.RowLayouts)
        {
            layout.Dispose();
        }

        entry.RowLayouts.Clear();

        for (var r = 0; r < rows; r++)
        {
            var start = r * _wrapCols;
            var len = Math.Min(_wrapCols, display.Length - start);
            var text = len == display.Length ? display
                : len > 0 ? display.Substring(start, len)
                : " ";
            var layout = new CanvasTextLayout(Canvas, text, _textFormat!, 65536f, (float)_lineHeight * 2);

            if (EnableSyntaxHighlighting)
            {
                foreach (var token in entry.Tokens)
                {
                    if (token.Kind == CodeTokenKind.Plain)
                    {
                        continue;
                    }

                    var from = Math.Max(token.Start, start);
                    var to = Math.Min(token.Start + token.Length, start + len);
                    if (to > from)
                    {
                        layout.SetColor(from - start, to - from, CodePalette.Get(token.Kind));
                    }
                }
            }

            // Consolas has no glyphs for private-use icons (Segoe MDL2 Assets), which would render as boxes.
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] is >= '' and <= '')
                {
                    var runStart = i;
                    while (i + 1 < text.Length && text[i + 1] is >= '' and <= '')
                    {
                        i++;
                    }

                    layout.SetFontFamily(runStart, i - runStart + 1, IconFontFamily);
                }
            }

            if (r == 0 && !WordWrap)
            {
                _maxLineWidth = Math.Max(_maxLineWidth, layout.DrawBounds.Width * (display.Length == 0 ? 0 : 1));
            }

            entry.RowLayouts.Add(layout);
        }
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Invalidates both layers. Everything but the caret lives on Canvas; the caret alone on CaretCanvas.</summary>
    private void InvalidateAll()
    {
        Canvas.Invalidate();
        CaretCanvas.Invalidate();
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // A device rebuild invalidates every cached layout and brush; they are rebuilt
        // lazily on the next draw.
        ClearLayouts();
        foreach (var brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _brushes.Clear();
        _textFormat = null;
        _lineNumberFormat = null;
    }

    private void OnCaretCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _caretBrush?.Dispose();
        _caretBrush = new CanvasSolidColorBrush(sender, _caretColor);
    }

    private void OnCaretCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (FocusState == FocusState.Unfocused || !_caretVisible)
        {
            return;
        }

        EnsureMetrics();
        var caretLine = _caret.Line;
        var firstLine = Math.Max(0, (int)(_verticalOffset / _lineHeight));
        var lastLine = (int)((_verticalOffset + sender.ActualHeight) / _lineHeight);
        if (caretLine < firstLine || caretLine > lastLine)
        {
            return;
        }

        var rawLine = _doc.GetLine(caretLine);
        var (caretRowInLine, caretColInRow) = Fold(TextDocument.ExpandedColFromRaw(rawLine, _caret.Col));
        var x = TextX + caretColInRow * _charWidth - _horizontalOffset;
        var y = (_rowOffsets[caretLine] + caretRowInLine) * _lineHeight - _verticalOffset + LineHighlightOffset;
        args.DrawingSession.FillRectangle((float)x, (float)y, CaretWidth, (float)_lineHeight, _caretBrush!);
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        EnsureMetrics();
        EnsureRowMap();
        var session = args.DrawingSession;

        var viewportWidth = Canvas.ActualWidth;
        var viewportHeight = Canvas.ActualHeight;
        var firstRow = Math.Max(0, (int)(_verticalOffset / _lineHeight));
        var lastRow = Math.Min(_totalRows - 1, (int)((_verticalOffset + viewportHeight) / _lineHeight));
        var (firstLine, _) = RowAt(firstRow);
        var (lastLine, _) = RowAt(lastRow);
        var gutter = GutterWidth;

        // Token validity first: the selection spans reuse the expanded line text below.
        EnsureTokens(lastLine);

        // The active line: a full-width band under the caret's visual row. Nudged down
        // a couple of pixels so it sits on the glyphs rather than above them.
        var focused = FocusState != FocusState.Unfocused;
        if (focused)
        {
            var caretLine = _doc.GetLine(_caret.Line);
            var (caretRowInLine, _) = Fold(TextDocument.ExpandedColFromRaw(caretLine, _caret.Col));
            var caretRow = _rowOffsets[_caret.Line] + caretRowInLine;
            if (caretRow >= firstRow && caretRow <= lastRow)
            {
                var y = caretRow * _lineHeight - _verticalOffset + LineHighlightOffset;
                session.FillRectangle(0, (float)y, (float)viewportWidth, (float)_lineHeight, Brush(session, _activeLineColor));
            }
        }

        // Selection spans, one rectangle per folded row.
        if (HasSelection)
        {
            var (start, end) = OrderedSelection();
            for (var line = Math.Max(start.Line, firstLine); line <= Math.Min(end.Line, lastLine); line++)
            {
                var rawLine = _doc.GetLine(line);
                var expanded = _lineCache[line].Display;
                var fromCol = line == start.Line ? TextDocument.ExpandedColFromRaw(rawLine, start.Col) : 0;
                var toCol = line == end.Line ? TextDocument.ExpandedColFromRaw(rawLine, end.Col) : expanded.Length;
                var offset = _rowOffsets[line];
                var firstR = fromCol / _wrapCols;
                var lastR = toCol == 0 ? 0 : (toCol - 1) / _wrapCols;
                for (var r = firstR; r <= lastR; r++)
                {
                    var rowStart = r * _wrapCols;
                    var s = Math.Max(fromCol, rowStart);
                    var e = Math.Min(toCol, rowStart + _wrapCols);
                    var x1 = TextX + (s - rowStart) * _charWidth - _horizontalOffset;
                    var x2 = TextX + (e - rowStart) * _charWidth - _horizontalOffset;
                    var y = (offset + r) * _lineHeight - _verticalOffset + LineHighlightOffset;
                    session.FillRectangle((float)x1, (float)y, (float)Math.Max(2, x2 - x1), (float)_lineHeight, Brush(session, _selectionColor));
                }
            }
        }

        // The buffer, one laid-out visual row at a time.
        for (var line = firstLine; line <= lastLine; line++)
        {
            var entry = _lineCache[line];
            EnsureRowLayouts(entry);
            var offset = _rowOffsets[line];
            var rows = RowCountOf(entry.Display.Length);
            for (var r = 0; r < rows; r++)
            {
                var vrow = offset + r;
                if (vrow < firstRow || vrow > lastRow)
                {
                    continue;
                }

                var y = vrow * _lineHeight - _verticalOffset;
                session.DrawTextLayout(entry.RowLayouts[r], (float)(TextX - _horizontalOffset), (float)y, Brush(session, _baseText));

                if (r == 0 && ShowLineNumbers)
                {
                    session.DrawText(
                        (line + 1).ToString(),
                        (float)(gutter - TextLeftPad),
                        (float)y,
                        Brush(session, _lineNumberText),
                        _lineNumberFormat!);
                }
            }
        }

        UpdateExtent();
    }

    /// <summary>Sizes the scroll extent to the content, never smaller than the viewport.</summary>
    private void UpdateExtent()
    {
        EnsureRowMap();
        var width = WordWrap ? 0 : TextX + _maxLineWidth + 60;
        var height = _totalRows * _lineHeight + 40;
        Extent.Width = Math.Max(Canvas.ActualWidth, width);
        Extent.Height = Math.Max(Canvas.ActualHeight, height);
    }

    // ------------------------------------------------------------------ scroll

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        var changed = _verticalOffset != Scroller.VerticalOffset || _horizontalOffset != Scroller.HorizontalOffset;
        _verticalOffset = Scroller.VerticalOffset;
        _horizontalOffset = Scroller.HorizontalOffset;
        if (changed)
        {
            InvalidateAll();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateExtent();
        InvalidateAll();
    }

    private void ScrollIntoView()
    {
        EnsureMetrics();
        EnsureRowMap();
        var rawLine = _doc.GetLine(_caret.Line);
        var (rowInLine, colInRow) = Fold(TextDocument.ExpandedColFromRaw(rawLine, _caret.Col));
        var x = TextX + colInRow * _charWidth;
        var y = (_rowOffsets[_caret.Line] + rowInLine) * _lineHeight;

        var targetVertical = _verticalOffset;
        if (y < _verticalOffset)
        {
            targetVertical = y;
        }
        else if (y + _lineHeight > _verticalOffset + Canvas.ActualHeight)
        {
            targetVertical = y + _lineHeight - Canvas.ActualHeight;
        }

        var targetHorizontal = _horizontalOffset;
        if (WordWrap)
        {
            // Folded lines never scroll sideways; a change of setting may have left
            // the view scrolled right, so bring it home.
            targetHorizontal = 0;
        }
        else
        {
            var viewX = x - _horizontalOffset;
            if (viewX < GutterWidth)
            {
                targetHorizontal = Math.Max(0, x - TextLeftPad * 3);
            }
            else if (viewX > Canvas.ActualWidth - 40)
            {
                targetHorizontal = Math.Max(0, x - (Canvas.ActualWidth - TextX) / 3);
            }
        }

        if (targetVertical != _verticalOffset || targetHorizontal != _horizontalOffset)
        {
            Scroller.ChangeView(targetHorizontal, targetVertical, null);
        }
    }

    // ------------------------------------------------------------------ pointer input

    private readonly Stopwatch _clickClock = new();
    private Windows.Foundation.Point _lastClickPoint;
    private int _clickCount;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(Canvas);

        // A right click opens the context menu: it moves the caret unless it lands in the
        // selection, but never starts a drag.
        if (point.Properties.IsRightButtonPressed)
        {
            var clicked = PointToDocumentPosition(point.Position);
            if (!SelectionContains(clicked))
            {
                _caret = _anchor = clicked;
                InvalidateAll();
                RaiseSelectionChanged();
            }

            return;
        }

        Canvas.CapturePointer(e.Pointer);

        // A double click selects the word under it.
        _clickClock.Stop();
        var isRepeat = _clickClock.ElapsedMilliseconds < 500
            && Math.Abs(point.Position.X - _lastClickPoint.X) < _charWidth
            && Math.Abs(point.Position.Y - _lastClickPoint.Y) < _lineHeight;
        _clickCount = isRepeat ? _clickCount + 1 : 1;
        _clickClock.Restart();
        _lastClickPoint = point.Position;

        if (_clickCount >= 2)
        {
            SelectWordAt(point.Position);
        }
        else
        {
            var position = PointToDocumentPosition(point.Position);
            if (IsShiftDown())
            {
                // Shift+click extends the selection from the existing anchor.
                _caret = position;
            }
            else
            {
                _caret = _anchor = position;
            }
        }

        _dragPoint = point.Position;
        InvalidateAll();
        RaiseSelectionChanged();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPointerCaptured(e))
        {
            return;
        }

        var point = e.GetCurrentPoint(Canvas);
        _dragPoint = point.Position;
        if (point.Properties.IsLeftButtonPressed && _clickCount < 2)
        {
            _caret = PointToDocumentPosition(point.Position);
            InvalidateAll();
            RaiseSelectionChanged();
            UpdateAutoScroll();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _autoScrollTimer?.Stop();
        Canvas.ReleasePointerCapture(e.Pointer);
    }

    // Dragging a selection past the top or bottom edge keeps scrolling that way, even
    // while the pointer is still, so a range can reach text that is out of view.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoScrollTimer;
    private Windows.Foundation.Point _dragPoint;

    private void UpdateAutoScroll()
    {
        var outside = _dragPoint.Y < 0 || _dragPoint.Y > Canvas.ActualHeight
            || (!WordWrap && (_dragPoint.X < GutterWidth || _dragPoint.X > Canvas.ActualWidth));
        if (!outside)
        {
            _autoScrollTimer?.Stop();
            return;
        }

        if (_autoScrollTimer is null)
        {
            _autoScrollTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(30);
            _autoScrollTimer.Tick += (_, _) => OnAutoScrollTick();
        }

        if (!_autoScrollTimer.IsRunning)
        {
            _autoScrollTimer.Start();
        }
    }

    private void OnAutoScrollTick()
    {
        if (Canvas.PointerCaptures is not { Count: > 0 })
        {
            _autoScrollTimer?.Stop();
            return;
        }

        // Speed grows with the distance past the edge, capped at a few lines per tick.
        double Step(double over) => Math.Clamp(over / 4, 4, _lineHeight * 3) * Math.Sign(over);

        double? targetV = null;
        double? targetH = null;
        if (_dragPoint.Y < 0)
        {
            targetV = Math.Max(0, _verticalOffset + Step(_dragPoint.Y));
        }
        else if (_dragPoint.Y > Canvas.ActualHeight)
        {
            targetV = _verticalOffset + Step(_dragPoint.Y - Canvas.ActualHeight);
        }

        if (!WordWrap)
        {
            if (_dragPoint.X < GutterWidth)
            {
                targetH = Math.Max(0, _horizontalOffset + Step(_dragPoint.X - GutterWidth));
            }
            else if (_dragPoint.X > Canvas.ActualWidth)
            {
                targetH = _horizontalOffset + Step(_dragPoint.X - Canvas.ActualWidth);
            }
        }

        if (targetV is null && targetH is null)
        {
            _autoScrollTimer?.Stop();
            return;
        }

        Scroller.ChangeView(targetH, targetV, null, disableAnimation: true);

        // ChangeView applies asynchronously; extrapolate the offsets we just asked for so
        // the caret follows without waiting a tick.
        var savedV = _verticalOffset;
        var savedH = _horizontalOffset;
        _verticalOffset = Math.Min(targetV ?? savedV, Math.Max(0, Scroller.ScrollableHeight));
        _horizontalOffset = Math.Min(targetH ?? savedH, Math.Max(0, Scroller.ScrollableWidth));
        _caret = PointToDocumentPosition(_dragPoint);
        InvalidateAll();
        RaiseSelectionChanged();
    }

    private bool IsPointerCaptured(PointerRoutedEventArgs e) =>
        Canvas.PointerCaptures is { Count: > 0 };

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas);
        var delta = point.Properties.MouseWheelDelta;

        if (IsCtrlDown())
        {
            // One notch is one size step.
            var steps = Math.Sign(delta);
            var size = (float)(FontSize is double.NaN or <= 0 ? DefaultFontSize : FontSize) + steps;
            if (size >= MinFontSize && size <= MaxFontSize && size != FontSize)
            {
                FontSize = size;
            }

            e.Handled = true;
            return;
        }

        if (IsShiftDown())
        {
            Scroller.ChangeView(Math.Max(0, _horizontalOffset - delta), null, null);
        }
        else
        {
            // Three lines per notch, the classic amount.
            Scroller.ChangeView(null, Math.Max(0, _verticalOffset - delta * 3 * _lineHeight / 120), null);
        }

        e.Handled = true;
    }

    private DocumentPosition PointToDocumentPosition(Windows.Foundation.Point point)
    {
        EnsureMetrics();
        EnsureRowMap();
        var visualRow = Math.Clamp(
            (int)Math.Floor((point.Y + _verticalOffset) / _lineHeight), 0, _totalRows - 1);
        var (line, rowInLine) = RowAt(visualRow);
        var rowStart = rowInLine * _wrapCols;

        var rawLine = _doc.GetLine(line);
        var lineLen = TextDocument.ExpandedColFromRaw(rawLine, rawLine.Length);
        var rowLen = Math.Max(0, Math.Min(_wrapCols, lineLen - rowStart));
        var colInRow = Math.Clamp(
            (int)Math.Round((point.X - TextX + _horizontalOffset) / _charWidth), 0, rowLen);

        var col = TextDocument.RawColFromExpanded(rawLine, rowStart + colInRow);
        return new DocumentPosition(line, Math.Min(col, rawLine.Length));
    }

    private void SelectWordAt(Windows.Foundation.Point point)
    {
        var position = PointToDocumentPosition(point);
        var line = _doc.GetLine(position.Line);
        if (line.Length == 0)
        {
            _caret = _anchor = position;
            return;
        }

        var isWordChar = Char.IsLetterOrDigit(line[Math.Min(position.Col, line.Length - 1)]) || line[Math.Min(position.Col, line.Length - 1)] == '_';
        var start = position.Col;
        while (start > 0 && IsWordChar(line[start - 1]) == isWordChar && (isWordChar || line[start - 1] != ' '))
        {
            start--;
        }

        var end = position.Col;
        while (end < line.Length && IsWordChar(line[end]) == isWordChar && (isWordChar || line[end] != ' '))
        {
            end++;
        }

        _anchor = new DocumentPosition(position.Line, start);
        _caret = new DocumentPosition(position.Line, end);
        InvalidateAll();
        RaiseSelectionChanged();
    }

    private bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    // ------------------------------------------------------------------ keyboard input

    private (DocumentPosition Start, DocumentPosition End) OrderedSelection() =>
        _anchor < _caret ? (_anchor, _caret) : (_caret, _anchor);

    private DocumentPosition ClampPosition(DocumentPosition position)
    {
        // The line is clamped first: the column limit comes from the clamped line,
        // so a position past the buffer cannot survive with a stale column.
        var line = Math.Clamp(position.Line, 0, _doc.LineCount - 1);
        return new(line, Math.Clamp(position.Col, 0, _doc.GetLine(line).Length));
    }

    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsCtrlDown();
        var shift = IsShiftDown();

        switch (e.Key)
        {
            case VirtualKey.Left: MoveCaretHorizontal(ctrl ? Jump.Word : Jump.Character, back: true, select: shift); break;
            case VirtualKey.Right: MoveCaretHorizontal(ctrl ? Jump.Word : Jump.Character, back: false, select: shift); break;
            case VirtualKey.Up: MoveCaretVertical(-1, select: shift); break;
            case VirtualKey.Down: MoveCaretVertical(1, select: shift); break;
            case VirtualKey.Home: MoveCaretHome(select: shift); break;
            case VirtualKey.End: MoveCaretLineEnd(select: shift); break;
            case VirtualKey.PageUp: MoveCaretVertical(-VisibleLineCount(), select: shift); break;
            case VirtualKey.PageDown: MoveCaretVertical(VisibleLineCount(), select: shift); break;
            case VirtualKey.Back: Backspace(); break;
            case VirtualKey.Delete: Delete(); break;
            case VirtualKey.Enter: InsertNewLine(); break;
            case VirtualKey.Tab: InsertText(new string(' ', TextDocument.TabWidth)); break;
            case VirtualKey.A when ctrl: SelectAll(); break;
            case VirtualKey.C when ctrl: CopySelection(); break;
            case VirtualKey.X when ctrl: CutSelection(); break;
            case VirtualKey.V when ctrl: _ = PasteAsync(); break;
            case VirtualKey.Z when ctrl: Undo(); break;
            case VirtualKey.Y when ctrl: Redo(); break;
            default: return;
        }

        e.Handled = true;
    }

    private void OnEditorCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        // Control combos arrive here as control characters and are already handled in KeyDown.
        if (IsCtrlDown() || IsAltDown())
        {
            return;
        }

        var ch = args.Character;
        if (ch is '\r' or '\n' or '\t' || char.IsControl(ch))
        {
            return;
        }

        args.Handled = true;
        InsertText(ch.ToString());
    }

    private int VisibleLineCount() => Math.Max(1, (int)(Canvas.ActualHeight / _lineHeight) - 1);

    private enum Jump
    {
        Character,
        Word,
    }

    private void MoveCaretHorizontal(Jump jump, bool back, bool select)
    {
        if (jump == Jump.Word)
        {
            MoveCaretWord(back, select);
            return;
        }

        var caret = _caret;
        if (!select && HasSelection)
        {
            // Collapse onto the end the movement is heading towards.
            var (start, end) = OrderedSelection();
            _caret = _anchor = back ? start : end;
        }
        else if (back)
        {
            if (caret.Col > 0)
            {
                _caret = new DocumentPosition(caret.Line, caret.Col - 1);
            }
            else if (caret.Line > 0)
            {
                _caret = new DocumentPosition(caret.Line - 1, _doc.GetLine(caret.Line - 1).Length);
            }
        }
        else
        {
            var line = _doc.GetLine(caret.Line);
            if (caret.Col < line.Length)
            {
                _caret = new DocumentPosition(caret.Line, caret.Col + 1);
            }
            else if (caret.Line < _doc.LineCount - 1)
            {
                _caret = new DocumentPosition(caret.Line + 1, 0);
            }
        }

        if (!select)
        {
            _anchor = _caret;
        }

        AfterCaretMove();
    }

    private void MoveCaretWord(bool back, bool select)
    {
        var line = _doc.GetLine(_caret.Line);
        var col = _caret.Col;

        static bool Word(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

        if (back)
        {
            while (col > 0 && !Word(line[col - 1]))
            {
                col--;
            }

            while (col > 0 && Word(line[col - 1]))
            {
                col--;
            }
        }
        else
        {
            while (col < line.Length && !Word(line[col]))
            {
                col++;
            }

            while (col < line.Length && Word(line[col]))
            {
                col++;
            }
        }

        _caret = new DocumentPosition(_caret.Line, col);
        if (!select)
        {
            _anchor = _caret;
        }

        AfterCaretMove();
    }

    /// <summary>
    /// Moves the caret one visual row: with wrap on, a logical line spans several rows
    /// and Up/Down walk them, keeping the visual column the way plain editors keep the
    /// plain column. Landing past a shorter row clamps to its end.
    /// </summary>
    private void MoveCaretVertical(int deltaLines, bool select)
    {
        EnsureRowMap();
        var caretLine = _doc.GetLine(_caret.Line);
        var (rowInLine, colInRow) = Fold(TextDocument.ExpandedColFromRaw(caretLine, _caret.Col));
        var targetRow = Math.Clamp(_rowOffsets[_caret.Line] + rowInLine + deltaLines, 0, _totalRows - 1);
        var (line, targetRowInLine) = RowAt(targetRow);

        var rawLine = _doc.GetLine(line);
        var lineLen = TextDocument.ExpandedColFromRaw(rawLine, rawLine.Length);
        var rowStart = targetRowInLine * _wrapCols;
        var rowLen = Math.Max(0, Math.Min(_wrapCols, lineLen - rowStart));
        var expandedCol = Math.Min(rowStart + Math.Min(colInRow, rowLen), lineLen);

        var col = TextDocument.RawColFromExpanded(rawLine, expandedCol);
        _caret = new DocumentPosition(line, Math.Min(col, rawLine.Length));
        if (!select)
        {
            _anchor = _caret;
        }

        AfterCaretMove();
    }

    private void MoveCaretHome(bool select)
    {
        var line = _doc.GetLine(_caret.Line);
        var indent = 0;
        while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
        {
            indent++;
        }

        // Smart home: first press lands on the text, second on the column start.
        _caret = new DocumentPosition(_caret.Line, _caret.Col == indent ? 0 : indent);
        if (!select)
        {
            _anchor = _caret;
        }

        AfterCaretMove();
    }

    private void MoveCaretLineEnd(bool select)
    {
        _caret = new DocumentPosition(_caret.Line, _doc.GetLine(_caret.Line).Length);
        if (!select)
        {
            _anchor = _caret;
        }

        AfterCaretMove();
    }

    private void AfterCaretMove()
    {
        ScrollIntoView();
        InvalidateAll();
        RaiseSelectionChanged();
    }

    private void SelectAll()
    {
        _anchor = new DocumentPosition(0, 0);
        _caret = new DocumentPosition(_doc.LineCount - 1, _doc.GetLine(_doc.LineCount - 1).Length);
        InvalidateAll();
        RaiseSelectionChanged();
    }

    // ------------------------------------------------------------------ editing

    /// <summary>Inserts text at the caret, replacing any selection.</summary>
    private void InsertText(string text)
    {
        BeginUndoStep(canCoalesce: text.Length == 1);
        var (start, end) = OrderedSelection();
        _caret = Edit(start, end, text);
        _anchor = _caret;
        AfterEdit();
    }

    private void InsertNewLine()
    {
        BeginUndoStep(canCoalesce: false);
        var (start, end) = OrderedSelection();
        Edit(start, end, "\n");

        // Auto-indent: the new line inherits its predecessor's whitespace, plus one
        // more level when the line opens a brace.
        var indent = _doc.IndentOf(start.Line);
        var opened = start.Col > 0 && "{[(".Contains(_doc.GetLine(start.Line)[Math.Max(0, start.Col - 1)]);
        if (opened)
        {
            indent += new string(' ', TextDocument.TabWidth);
        }

        var caretLine = start.Line + 1;
        if (indent.Length > 0)
        {
            Edit(new DocumentPosition(caretLine, 0), new DocumentPosition(caretLine, 0), indent);
        }

        _caret = _anchor = new DocumentPosition(caretLine, indent.Length);
        AfterEdit();
    }

    private void Backspace()
    {
        var (start, end) = OrderedSelection();
        if (start != end)
        {
            BeginUndoStep(canCoalesce: true);
            _caret = Edit(start, end, string.Empty);
            _anchor = _caret;
            AfterEdit();
            return;
        }

        if (start is { Line: 0, Col: 0 })
        {
            return;
        }

        BeginUndoStep(canCoalesce: true);

        // At the start of a line, backspace removes the line break and joins it to
        // the end of the line above.
        if (start.Col == 0)
        {
            var above = new DocumentPosition(start.Line - 1, _doc.GetLine(start.Line - 1).Length);
            _caret = Edit(above, start, string.Empty);
            _anchor = _caret;
            AfterEdit();
            return;
        }

        // Backspace over leading whitespace eats a whole indent level, so de-indenting
        // does not take four presses.
        var line = _doc.GetLine(start.Line);
        int deleteTo;
        if (start.Col >= TextDocument.TabWidth && line[..start.Col].AsSpan().IndexOfAnyExcept(' ') < 0)
        {
            deleteTo = start.Col - TextDocument.TabWidth + (start.Col % TextDocument.TabWidth);
        }
        else
        {
            deleteTo = start.Col - 1;
        }

        _caret = Edit(new DocumentPosition(start.Line, Math.Max(0, deleteTo)), start, string.Empty);
        _anchor = _caret;
        AfterEdit();
    }

    private void Delete()
    {
        var (start, end) = OrderedSelection();
        if (start != end)
        {
            BeginUndoStep(canCoalesce: true);
            _caret = Edit(start, end, string.Empty);
            _anchor = _caret;
            AfterEdit();
            return;
        }

        var line = _doc.GetLine(start.Line);
        if (start.Col >= line.Length && start.Line >= _doc.LineCount - 1)
        {
            return;
        }

        BeginUndoStep(canCoalesce: true);
        var next = start.Col < line.Length
            ? new DocumentPosition(start.Line, start.Col + 1)
            : new DocumentPosition(start.Line + 1, 0);
        _caret = Edit(start, next, string.Empty);
        _anchor = _caret;
        AfterEdit();
    }

    private void AfterEdit()
    {
        ClampCaretState();

        // Editing moves the caret: typing past the right edge, Enter on the bottom
        // row and a big paste all have to bring it back into view.
        ScrollIntoView();
        UpdateExtent();
        InvalidateAll();
        RaiseTextChanged();
        RaiseSelectionChanged();
    }

    private void ClampCaretState()
    {
        _caret = ClampPosition(_caret);
        _anchor = ClampPosition(_anchor);
    }

    // ------------------------------------------------------------------ undo / redo

    /// <summary>Opens a new undo step, or - for continuous typing - keeps appending to the current one.</summary>
    private void BeginUndoStep(bool canCoalesce)
    {
        var now = DateTime.UtcNow;
        var continuous = canCoalesce && _undo.Count > 0 && (now - _lastEditUtc).TotalMilliseconds < 600;
        if (!continuous)
        {
            _undo.Add(new UndoStep(_caret));
            if (_undo.Count > UndoLimit)
            {
                _undo.RemoveAt(0);
            }
        }

        _redo.Clear();
        _lastEditUtc = now;
    }

    /// <summary>Applies an edit to the document and records it, as a delta, in the open undo step.</summary>
    private DocumentPosition Edit(DocumentPosition start, DocumentPosition end, string text)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var removed = _doc.GetRange(start, end);
        var after = _doc.Replace(start, end, text);
        InvalidateTokensFrom(start.Line);
        _rowMapDirty = true;
        _undo[^1].Edits.Add(new EditRecord(start, end, removed, text, after));
        return after;
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (var i = step.Edits.Count - 1; i >= 0; i--)
        {
            var edit = step.Edits[i];
            _doc.Replace(edit.Start, edit.InsertedEnd, edit.Removed);
        }

        _redo.Add(step);
        FinishUndoRedo(step.CaretBefore, FirstEditedLine(step, step.CaretBefore.Line));
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var edit in step.Edits)
        {
            _doc.Replace(edit.Start, edit.RemovedEnd, edit.Inserted);
        }

        _undo.Add(step);
        FinishUndoRedo(
            step.Edits.Count > 0 ? step.Edits[^1].InsertedEnd : step.CaretBefore,
            FirstEditedLine(step, step.CaretBefore.Line));
    }

    private static int FirstEditedLine(UndoStep step, int fallback) =>
        step.Edits.Count == 0 ? fallback : step.Edits.Min(e => e.Start.Line);

    private void FinishUndoRedo(DocumentPosition caret, int changedFrom)
    {
        _caret = _anchor = caret;
        _lastEditUtc = DateTime.MinValue;
        // An undo touches a handful of lines: re-lex from the earliest one instead of
        // dropping the whole token and layout cache, which re-shaped the viewport on
        // every undo step in large files.
        InvalidateTokensFrom(changedFrom);
        ClampCaretState();
        _maxLineWidth = 0;
        _rowMapDirty = true;
        UpdateExtent();
        InvalidateAll();
        ScrollIntoView();
        RaiseTextChanged();
        RaiseSelectionChanged();
    }

    /// <summary>Everything that has to follow the buffer being swapped out wholesale.</summary>
    private void ResetAfterDocChange()
    {
        ClampCaretState();
        ClearTokens();
        _maxLineWidth = 0;
        _rowMapDirty = true;
        UpdateExtent();
        InvalidateAll();
    }

    // ------------------------------------------------------------------ clipboard

    private void CopySelection()
    {
        if (!HasSelection)
        {
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy };
        package.SetText(SelectedText);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void CutSelection()
    {
        if (!HasSelection)
        {
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move };
        package.SetText(SelectedText);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        Delete();
    }

    private async Task PasteAsync()
    {
        var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
        {
            return;
        }

        var text = await content.GetTextAsync();
        InsertText(text.Replace("\r\n", "\n").Replace('\r', '\n'));
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsCtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsAltDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
}
