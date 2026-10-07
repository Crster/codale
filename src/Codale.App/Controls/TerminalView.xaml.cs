using System.Diagnostics;

using Codale.App.Services;
using Codale.Terminal;

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Windows.System;
using Windows.UI.Core;

namespace Codale.App.Controls;

/// <summary>
/// Codale's own native WinUI 3 terminal: a Win2D canvas that draws the emulator's
/// cell grid, with selection, scrollback, mouse reporting and VT key encoding -
/// everything a terminal frontend does, natively.
///
/// The rendering follows the same patterns as the CodeEditor control (canvas plus
/// scrollbar-only ScrollViewer, brush cache, viewport-only drawing); the difference
/// is a fixed cell grid instead of free-form lines, and that input is forwarded to
/// the shell rather than applied to a local buffer.
/// </summary>
public sealed partial class TerminalView : UserControl
{
    /// <summary>Font size from Settings; an instance value so a live change can refit the grid.</summary>
    private double _fontSize = 13;

    /// <summary>Font family from Settings, validated against the installed fonts; the fallback when nothing matches.</summary>
    private const string DefaultFontFamily = "Cascadia Mono";

    /// <summary>The resolved family this view draws with.</summary>
    private string _fontFamily = DefaultFontFamily;

    /// <summary>Padding around the cell grid, (4 top, 8 left).</summary>
    private const double PadLeft = 8;
    private const double PadTop = 4;
    private const double PadRight = 8;
    private const double PadBottom = 4;

    // The Campbell palette (Windows Terminal default).
    private static readonly Windows.UI.Color BackgroundColor = FromHex("#0C0C0C");
    private static readonly Windows.UI.Color ForegroundColor = FromHex("#CCCCCC");
    private static readonly Windows.UI.Color SelectionColor = FromHex("#264F78");
    private static readonly Windows.UI.Color CursorColor = FromHex("#CCCCCC");
    private static readonly Windows.UI.Color[] Palette =
    [
        FromHex("#0C0C0C"), FromHex("#C50F1F"), FromHex("#13A10E"), FromHex("#C19C00"),
        FromHex("#0037DA"), FromHex("#881798"), FromHex("#3A96DD"), FromHex("#CCCCCC"),
        FromHex("#767676"), FromHex("#E74856"), FromHex("#16C60C"), FromHex("#F9F1A5"),
        FromHex("#3B78FF"), FromHex("#B4009E"), FromHex("#61D6D6"), FromHex("#F2F2F2"),
    ];

    /// <summary>Channel levels of the 256-colour cube; static so per-cell colour resolution does not allocate.</summary>
    private static readonly byte[] CubeLevels = [0, 95, 135, 175, 215, 255];

    private static Windows.UI.Color FromHex(string hex) => ColorFrom(
        0xFF,
        Convert.ToByte(hex[1..3], 16),
        Convert.ToByte(hex[3..5], 16),
        Convert.ToByte(hex[5..7], 16));

    /// <summary>WinUI3 projects do not see Windows.UI.ColorHelper; construct the struct directly.</summary>
    private static Windows.UI.Color ColorFrom(byte a, byte r, byte g, byte b) => new()
    {
        A = a,
        R = r,
        G = g,
        B = b,
    };

    private readonly TerminalEmulator _emulator = new();

    private double _charWidth = 8;
    private double _lineHeight = 18;

    private CanvasTextFormat? _textFormat;
    private CanvasTextFormat? _boldFormat;
    private CanvasTextFormat? _italicFormat;
    private CanvasTextFormat? _boldItalicFormat;

    private readonly Dictionary<Windows.UI.Color, CanvasSolidColorBrush> _brushes = [];

    /// <summary>The selection anchor and head, in absolute lines (0 = oldest scrollback line) and columns.</summary>
    private (int Line, int Col) _selectionAnchor;
    private (int Line, int Col) _selectionHead;
    private bool _selecting;

    private bool _caretVisible = true;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _caretTimer;

    private readonly Stopwatch _clickClock = new();
    private Windows.Foundation.Point _lastClickPoint;
    private int _clickCount;

    /// <summary>
    /// The viewport is one integer: the absolute line at its top. It is the single
    /// source of truth for drawing, hit-testing and the scrollbar, so the three can
    /// never disagree by a line. While pinned, it follows the live screen.
    /// </summary>
    private bool _pinned = true;
    private int _topLine;

    /// <summary>The scroller offset we last asked for, so ViewChanged can tell our own scrolls from the user's.</summary>
    private double _expectedOffset = double.NaN;

    /// <summary>Fractional wheel notches (precision touchpads send small deltas).</summary>
    private double _wheelLines;

    /// <summary>The emulator's trim counter at the last sync; see <see cref="TerminalEmulator.TrimmedLines"/>.</summary>
    private long _trimmedSeen;

    public TerminalView()
    {
        CrashLog.Trace("terminal: view ctor begin");
        InitializeComponent();

        // Settings drive the defaults; ApplyAppSettings follows a live change.
        _fontSize = Math.Clamp(AppSettings.TerminalFontSize, AppSettings.MinFontSize, AppSettings.MaxFontSize);
        _fontFamily = ResolveFontFamily(AppSettings.TerminalFontFamily);
        _emulator.ScrollbackLimit = AppSettings.TerminalScrollback;

        _emulator.TitleChanged += title => TitleChanged?.Invoke(this, title);

        // Every fed chunk repaints - without this, output (including the echo of each
        // keystroke) only showed up on the next caret blink, up to 530ms late.
        // CanvasControl.Invalidate coalesces, so a burst of chunks is one frame.
        _emulator.Invalidated += OnEmulatorInvalidated;

        // DSR/DA replies go straight back to the shell; they are not user input, so
        // they must not clear the selection the way WriteToShellAsync does.
        _emulator.Response += reply => Response?.Invoke(this, reply);

        _caretTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _caretTimer.Interval = TimeSpan.FromMilliseconds(530);
        _caretTimer.Tick += (_, _) =>
        {
            // The blink redraws the one-cell cursor layer, not the whole grid: a full
            // repaint twice a second while idle was pure burn on every open terminal.
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
            InvalidateAll();
        };

        // A ChangeView issued before the extent grew is clamped to the old extent;
        // once layout catches up, re-issue it so the scrollbar lands where we meant.
        Extent.SizeChanged += (_, _) => SyncScrollbar();

        // Preview (tunnelling): the focus system consumes Tab and the arrows before a
        // bubbling KeyDown ever fires, which is what broke shell tab-completion.
        PreviewKeyDown += OnTerminalKeyDown;
        CharacterReceived += OnTerminalCharacterReceived;

        CrashLog.Trace("terminal: view ctor end");
    }

    // ------------------------------------------------------------------ public surface

    /// <summary>A reply the emulator wants sent to the shell (DSR, DA) - route it to the PTY input.</summary>
    public event EventHandler<string>? Response;

    /// <summary>The grid size changed, so the pseudoconsole must follow.</summary>
    public event EventHandler<(int Cols, int Rows)>? Resized;

    /// <summary>Raw output from the pseudoconsole, fed through the emulator.</summary>
    public void Write(string text) => _emulator.Feed(text);

    /// <summary>
    /// Applies the Settings-window values to this terminal: a new font size or family
    /// rebuilds the text formats and refits the grid (the shell's columns and rows
    /// follow), and a new scrollback limit takes effect as lines push. Called for open
    /// terminals when a setting changes.
    /// </summary>
    public void ApplyAppSettings()
    {
        _emulator.ScrollbackLimit = AppSettings.TerminalScrollback;

        var family = ResolveFontFamily(AppSettings.TerminalFontFamily);
        var size = Math.Clamp(AppSettings.TerminalFontSize, AppSettings.MinFontSize, AppSettings.MaxFontSize);
        if (Math.Abs(_fontSize - size) < 0.01 && string.Equals(_fontFamily, family, StringComparison.Ordinal))
        {
            return;
        }

        _fontSize = size;
        _fontFamily = family;
        DisposeTextFormats();
        RefitGrid();
    }

    private void DisposeTextFormats()
    {
        _textFormat?.Dispose();
        _boldFormat?.Dispose();
        _italicFormat?.Dispose();
        _boldItalicFormat?.Dispose();
        _textFormat = _boldFormat = _italicFormat = _boldItalicFormat = null;
    }

    /// <summary>
    /// Guards the cell grid: DirectWrite silently substitutes a proportional fallback
    /// for a family it does not have, which would shear every column off its cell. An
    /// unknown family therefore falls back to Cascadia Mono.
    /// </summary>
    private static string ResolveFontFamily(string requested) =>
        InstalledFonts.Resolve(requested, DefaultFontFamily);

    private void OnEmulatorInvalidated()
    {
        // Output moves the cursor; keep it solid while things change, like a real
        // terminal, instead of letting it blink out mid-typing.
        ResetCaretBlink();
        ApplyTrim();
        SyncScrollbar();
        InvalidateAll();
    }

    /// <summary>Invalidates both layers: the grid lives on Canvas, the cursor alone on CaretCanvas.</summary>
    private void InvalidateAll()
    {
        Canvas.Invalidate();
        CaretCanvas.Invalidate();
    }

    /// <summary>
    /// A full scrollback drops its oldest lines, shifting every absolute index down.
    /// Shift the scrolled-up viewport and the selection with it so they stay on their text.
    /// </summary>
    private void ApplyTrim()
    {
        var trimmed = _emulator.TrimmedLines;
        var delta = (int)(trimmed - _trimmedSeen);
        _trimmedSeen = trimmed;
        if (delta <= 0)
        {
            return;
        }

        if (!_pinned)
        {
            _topLine = Math.Max(0, _topLine - delta);
        }

        if (HasSelection)
        {
            _selectionAnchor.Line -= delta;
            _selectionHead.Line -= delta;
            if (_selectionAnchor.Line < 0 || _selectionHead.Line < 0)
            {
                _selectionAnchor = _selectionHead = default;
            }
        }
    }

    /// <summary>Shows the caret and restarts the blink cycle (focused only).</summary>
    private void ResetCaretBlink()
    {
        _caretVisible = true;
        if (FocusState != FocusState.Unfocused)
        {
            _caretTimer.Stop();
            _caretTimer.Start();
        }
    }

    /// <summary>The emulator's title event (OSC 0/2), re-raised for anything that wants it.</summary>
    public event EventHandler<string>? TitleChanged;

    public bool HasSelection => _selectionAnchor != _selectionHead;

    /// <summary>Clears the screen and scrollback, and the selection with it (the menu's Clear).</summary>
    public void Clear()
    {
        _emulator.Clear();
        ClearSelection();
        _pinned = true;
        SyncScrollbar();
        InvalidateAll();
    }

    public void SelectAll()
    {
        _selectionAnchor = (0, 0);
        _selectionHead = (_emulator.TotalLines - 1, _emulator.Cols - 1);
        InvalidateAll();
    }

    public string SelectedText => GetSelectionText();

    public void CopySelection() => SetClipboard(SelectedText);

    /// <summary>Pastes clipboard text into the shell, wrapped in bracketed-paste markers when the app asked for them.</summary>
    public async Task PasteAsync()
    {
        var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
        {
            return;
        }

        var text = await content.GetTextAsync();
        if (text.Length == 0)
        {
            return;
        }

        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (_emulator.BracketedPaste)
        {
            text = "\x1b[200~" + text + "\x1b[201~";
        }

        await WriteToShellAsync(text);
    }

    /// <summary>Sends text to the shell as if typed (menu paste, mouse reports, emulator replies).</summary>
    private Task WriteToShellAsync(string text)
    {
        // Anything the user sends clears the selection, like every real terminal -
        // a stray drag otherwise leaves its highlight floating over fresh output.
        ClearSelection();
        ResetCaretBlink();

        // Typing while scrolled up jumps back to the prompt, like every real terminal.
        if (!_pinned)
        {
            _pinned = true;
            SyncScrollbar();
            InvalidateAll();
        }

        Response?.Invoke(this, text);
        return Task.CompletedTask;
    }

    private void ClearSelection()
    {
        if (!HasSelection)
        {
            return;
        }

        _selectionAnchor = _selectionHead = default;
        InvalidateAll();
    }

    /// <summary>Sets the clipboard, retrying: another process briefly holding it open makes SetContent throw (CLIPBRD_E_CANT_OPEN), which is what made copy hit or miss.</summary>
    private static bool SetClipboard(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                try
                {
                    Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                }
                catch (Exception)
                {
                    // Flush is best effort; the content is already set.
                }

                return true;
            }
            catch (Exception)
            {
                System.Threading.Thread.Sleep(30);
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ metrics

    private void EnsureMetrics()
    {
        if (_textFormat is not null)
        {
            return;
        }

        var fontSize = (float)_fontSize;
        // Tight leading: the glyphs occupy about fontSize pixels, so 1.2x keeps the
        // cells close to a real terminal's rhythm without clipping descenders.
        _lineHeight = Math.Round(fontSize * 1.2);

        _textFormat = MakeFormat(fontSize, false, false);
        _boldFormat = MakeFormat(fontSize, true, false);
        _italicFormat = MakeFormat(fontSize, false, true);
        _boldItalicFormat = MakeFormat(fontSize, true, true);

        // Measured against the shared device, not the control: FitGrid runs from
        // SizeChanged, which fires before the control's first CreateResources, and
        // the control throws "no CanvasDevice" if used for resource creation there.
        //
        // LayoutBounds, not DrawBounds: DrawBounds is the ink, which leaves out the
        // side bearings of the first and last glyph - a hair under the real advance,
        // and that error multiplied by the column made the caret drift left of the
        // text on long lines. A long sample averages out any rounding.
        const int sample = 100;
        using var measure = new CanvasTextLayout(
            CanvasDevice.GetSharedDevice(), new string('M', sample), _textFormat, 65536f, (float)_lineHeight * 2);
        _charWidth = measure.LayoutBounds.Width / sample;
        if (_charWidth <= 0)
        {
            _charWidth = fontSize * 0.55;
        }
    }

    private CanvasTextFormat MakeFormat(float size, bool bold, bool italic) => new()
    {
        // Single family: CanvasTextFormat goes straight to DirectWrite, which takes
        // one family name - a CSS-style fallback list silently degrades to a
        // proportional default. The setting is validated for exactly that reason.
        FontFamily = _fontFamily,
        FontSize = size,
        FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)(bold ? 700 : 400) },
        FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
        WordWrapping = CanvasWordWrapping.NoWrap,
        HorizontalAlignment = CanvasHorizontalAlignment.Left,
        LineSpacing = size,
        LineSpacingBaseline = (float)Math.Round(size * 0.8),
    };

    /// <summary>The cell grid that fits the viewport, at least 1x1 so the shell always has a sane size.</summary>
    private (int Cols, int Rows) FitGrid()
    {
        EnsureMetrics();
        var cols = (int)((Canvas.ActualWidth - PadLeft - PadRight) / _charWidth);
        var rows = (int)((Canvas.ActualHeight - PadTop - PadBottom) / _lineHeight);
        return (Math.Max(1, cols), Math.Max(1, rows));
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => RefitGrid();

    /// <summary>Fits the cell grid to the viewport, resizing the shell when it changed.</summary>
    private void RefitGrid()
    {
        // While the tab is hidden the control reports zero; resizing the shell to
        // nothing would be wrong, so wait for a real size.
        if (Canvas.ActualWidth < _charWidth || Canvas.ActualHeight < _lineHeight)
        {
            return;
        }

        var (cols, rows) = FitGrid();
        if (cols != _emulator.Cols || rows != _emulator.Rows)
        {
            _emulator.Resize(cols, rows);
            ApplyTrim();
            Resized?.Invoke(this, (cols, rows));
        }

        SyncScrollbar();
        InvalidateAll();
    }

    // ------------------------------------------------------------------ colours

    private CanvasSolidColorBrush Brush(CanvasDrawingSession session, Windows.UI.Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new CanvasSolidColorBrush(session, color);
            _brushes[color] = brush;
        }

        return brush;
    }

    /// <summary>Resolves a cell colour to a pixel colour: theme defaults, the 16-entry palette, the 256 cube, or exact RGB.</summary>
    private static Windows.UI.Color ResolveColor(TerminalColor color, bool isForeground)
    {
        switch (color.Kind)
        {
            case TerminalColor.Kinds.Rgb:
                return ColorFrom(0xFF, color.R, color.G, color.B);
            case TerminalColor.Kinds.Indexed:
                var index = color.Index;
                if (index < 16)
                {
                    return Palette[index];
                }

                if (index < 232)
                {
                    // The classic 6x6x6 colour cube.
                    var i = index - 16;
                    return ColorFrom(0xFF,
                        CubeLevels[(i / 36) % 6], CubeLevels[(i / 6) % 6], CubeLevels[i % 6]);
                }

                // 24 steps of grayscale.
                var gray = (byte)(8 + (index - 232) * 10);
                return ColorFrom(0xFF, gray, gray, gray);
            default:
                return isForeground ? ForegroundColor : BackgroundColor;
        }
    }

    // ------------------------------------------------------------------ drawing

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        CrashLog.Trace("terminal: CreateResources begin");
        try
        {
            // A device rebuild invalidates every cached brush and format; they are rebuilt lazily.
            foreach (var brush in _brushes.Values)
            {
                brush.Dispose();
            }

            _brushes.Clear();
            _textFormat = null;
            _boldFormat = null;
            _italicFormat = null;
            _boldItalicFormat = null;
        }
        finally
        {
            CrashLog.Trace("terminal: CreateResources end");
        }
    }

    private CanvasSolidColorBrush? _caretBrush;
    private CanvasSolidColorBrush? _caretGlyphBrush;

    private void OnCaretCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _caretBrush?.Dispose();
        _caretGlyphBrush?.Dispose();
        _caretBrush = new CanvasSolidColorBrush(sender, CursorColor);
        _caretGlyphBrush = new CanvasSolidColorBrush(sender, BackgroundColor);
    }

    private void OnCaretCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (Canvas.ActualWidth <= 0 || Canvas.ActualHeight <= 0)
        {
            return;
        }

        try
        {
            EnsureMetrics();
            var topLine = TopLine;
            var lastLine = Math.Min(_emulator.TotalLines - 1, topLine + _emulator.Rows - 1);
            DrawCursor(args.DrawingSession, topLine, lastLine);
        }
        catch
        {
            // The grid layer must never depend on the cursor layer drawing.
        }
    }

    private bool _drawFaulted;

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try
        {
            DrawFrame(args);
        }
        catch (Exception ex)
        {
            // Log the first fault only, but keep drawing: a single bad frame (say, mid
            // resize) must not freeze the terminal for the rest of the session.
            if (!_drawFaulted)
            {
                _drawFaulted = true;
                CrashLog.Write("Terminal", "canvas draw threw; later faults not logged", ex);
            }
        }
    }

    private void DrawFrame(CanvasDrawEventArgs args)
    {
        EnsureMetrics();
        var session = args.DrawingSession;
        var viewportHeight = Canvas.ActualWidth > 0 ? Canvas.ActualHeight : 0;
        if (viewportHeight <= 0 || Canvas.ActualWidth <= 0)
        {
            return;
        }

        session.FillRectangle(0, 0, (float)Canvas.ActualWidth, (float)viewportHeight, Brush(session, BackgroundColor));

        // Exactly one screen's worth of rows from the top line: the grid was sized to
        // fit the viewport, so pinned, the live screen's last row is the last one drawn.
        var topLine = TopLine;
        var lastLine = Math.Min(_emulator.TotalLines - 1, topLine + _emulator.Rows - 1);

        for (var line = topLine; line <= lastLine; line++)
        {
            var y = PadTop + (line - topLine) * _lineHeight;
            DrawRow(session, line, (float)y);
        }

        // The cursor draws on its own layer (OnCaretCanvasDraw), so blinking it never
        // repaints the grid.
    }

    private void DrawRow(CanvasDrawingSession session, int absoluteLine, float y)
    {
        var row = _emulator.GetLine(absoluteLine);

        // Scrollback lines keep the width they were written at, so after the window
        // widens they are shorter than the grid - never index past their end.
        var cols = Math.Min(_emulator.Cols, row.Length);

        // Background fills first, then selection, then text - the same layering xterm uses.
        var col = 0;
        while (col < cols)
        {
            var cell = row[col];
            var bg = ResolveBackground(cell.Style);
            var start = col;
            while (col < cols && ResolveBackground(row[col].Style) == bg)
            {
                col++;
            }

            if (bg != BackgroundColor)
            {
                session.FillRectangle(
                    (float)(PadLeft + start * _charWidth), y,
                    (float)((col - start) * _charWidth), (float)_lineHeight,
                    Brush(session, bg));
            }
        }

        // Selection highlight for this row - the full cell height, matching the
        // background fills above: glyph-hugging strips leave the 20% leading of each
        // row unhighlighted, so consecutive selected rows do not touch.
        var selection = SelectionOnRow(absoluteLine);
        if (selection is { } span)
        {
            session.FillRectangle(
                (float)(PadLeft + span.from * _charWidth), y,
                (float)((span.to - span.from) * _charWidth), (float)_lineHeight,
                Brush(session, SelectionColor));
        }

        if (_hoverLink is { } hover && hover.Line == absoluteLine)
        {
            var underline = (float)(y + (_lineHeight - _fontSize) / 2 + _fontSize + 1);
            session.DrawLine(
                (float)(PadLeft + hover.From * _charWidth), underline,
                (float)(PadLeft + hover.To * _charWidth), underline,
                Brush(session, LinkColor), 1f);
        }

        // Text runs: consecutive cells sharing fg/flags are drawn as one DrawText.
        col = 0;
        while (col < cols)
        {
            var cell = row[col];
            if (cell.WideTail)
            {
                col++;
                continue;
            }

            var fg = ResolveForeground(cell.Style);

            // Block elements (progress bars: █ ▒ ░ ▌ ▄ ...) are drawn as rectangles that
            // fill the whole cell. The font's glyphs neither fill the cell height nor
            // join up, and its shade glyphs are diagonal hatching that reads as "////".
            if (cell.Code is >= '▀' and <= '▟')
            {
                if ((cell.Style.Flags & CellFlags.Hidden) == 0)
                {
                    DrawBlockElement(session, cell.Code, (float)(PadLeft + col * _charWidth), y, fg);
                }

                col++;
                continue;
            }

            var start = col;
            while (col < cols)
            {
                var next = row[col];
                if (next.WideTail || next.Code == '\0' || ResolveForeground(next.Style) != fg)
                {
                    break;
                }

                // Run breaks also on flags that need their own format or decoration.
                if ((next.Style.Flags & (CellFlags.Bold | CellFlags.Italic | CellFlags.Underline | CellFlags.Strikeout | CellFlags.Faint | CellFlags.Hidden))
                    != (cell.Style.Flags & (CellFlags.Bold | CellFlags.Italic | CellFlags.Underline | CellFlags.Strikeout | CellFlags.Faint | CellFlags.Hidden)))
                {
                    break;
                }

                // Non-ASCII glyphs may come from a fallback font with a different
                // advance; drawn inside a run they would shift every glyph after them
                // off the grid. Each one is its own run, pinned to its cell.
                if (next.Code > '~')
                {
                    if (col == start)
                    {
                        col++;
                    }

                    break;
                }

                col++;
            }

            if (col == start)
            {
                col++;
                continue;
            }

            var flags = cell.Style.Flags;
            if ((flags & CellFlags.Hidden) != 0)
            {
                continue;
            }

            var text = RunText(row, start, col);
            if (text.Length == 0)
            {
                continue;
            }

            var format = (flags & CellFlags.Bold) != 0
                ? (flags & CellFlags.Italic) != 0 ? _boldItalicFormat! : _boldFormat!
                : (flags & CellFlags.Italic) != 0 ? _italicFormat! : _textFormat!;

            var brushColor = fg;
            if ((flags & CellFlags.Faint) != 0)
            {
                brushColor = ColorFrom(0x80, brushColor.R, brushColor.G, brushColor.B);
            }

            // The text line box is fontSize tall inside a taller cell; centre it so it
            // sits in the middle of backgrounds and the selection highlight.
            var textY = y + (float)((_lineHeight - _fontSize) / 2);
            session.DrawText(text, (float)(PadLeft + start * _charWidth), textY, Brush(session, brushColor), format);

            if ((flags & CellFlags.Underline) != 0)
            {
                session.FillRectangle(
                    (float)(PadLeft + start * _charWidth), (float)(textY + _fontSize - 1),
                    (float)(CountColumns(row, start, col) * _charWidth), 1,
                    Brush(session, fg));
            }

            if ((flags & CellFlags.Strikeout) != 0)
            {
                session.FillRectangle(
                    (float)(PadLeft + start * _charWidth), (float)(textY + _fontSize * 0.55),
                    (float)(CountColumns(row, start, col) * _charWidth), 1,
                    Brush(session, fg));
            }
        }
    }

    /// <summary>Draws one Unicode block element (U+2580-U+259F) as filled rectangles in the cell at (x, y).</summary>
    private void DrawBlockElement(CanvasDrawingSession session, char code, float x, float y, Windows.UI.Color color)
    {
        var w = (float)_charWidth;
        var h = (float)_lineHeight;

        void Fill(float left, float top, float width, float height, Windows.UI.Color fill) =>
            session.FillRectangle(x + left * w, y + top * h, width * w, height * h, Brush(session, fill));

        switch (code)
        {
            case '▀': Fill(0, 0, 1, 0.5f, color); return;                        // upper half
            case >= '▁' and <= '█':                                          // lower 1/8 .. full
                var rows = (code - '▀') / 8f;
                Fill(0, 1 - rows, 1, rows, color);
                return;
            case >= '▉' and <= '▏':                                          // left 7/8 .. 1/8
                Fill(0, 0, (8 - (code - '█')) / 8f, 1, color);
                return;
            case '▐': Fill(0.5f, 0, 0.5f, 1, color); return;                      // right half
            case >= '░' and <= '▓':                                          // light, medium, dark shade
                var alpha = (byte)((code - '▐') * 0x40 - (code == '▓' ? 1 : 0));
                Fill(0, 0, 1, 1, ColorFrom(alpha, color.R, color.G, color.B));
                return;
            case '▔': Fill(0, 0, 1, 0.125f, color); return;                       // upper 1/8
            case '▕': Fill(0.875f, 0, 0.125f, 1, color); return;                  // right 1/8
        }

        // U+2596-U+259F: quadrants, as a bit set of upper-left, upper-right, lower-left, lower-right.
        const int UL = 1, UR = 2, LL = 4, LR = 8;
        var quadrants = code switch
        {
            '▖' => LL,
            '▗' => LR,
            '▘' => UL,
            '▙' => UL | LL | LR,
            '▚' => UL | LR,
            '▛' => UL | UR | LL,
            '▜' => UL | UR | LR,
            '▝' => UR,
            '▞' => UR | LL,
            _ => UR | LL | LR,
        };

        if ((quadrants & UL) != 0) Fill(0, 0, 0.5f, 0.5f, color);
        if ((quadrants & UR) != 0) Fill(0.5f, 0, 0.5f, 0.5f, color);
        if ((quadrants & LL) != 0) Fill(0, 0.5f, 0.5f, 0.5f, color);
        if ((quadrants & LR) != 0) Fill(0.5f, 0.5f, 0.5f, 0.5f, color);
    }

    /// <summary>Concatenates the run's characters, dropping blank cell padding at the end.</summary>
    private static string RunText(ReadOnlySpan<TerminalCell> row, int start, int end)
    {
        while (end > start && row[end - 1].Code is '\0' or ' ')
        {
            end--;
        }

        if (end <= start)
        {
            return "";
        }

        var length = end - start;
        Span<char> buffer = length <= 512 ? stackalloc char[length] : new char[length];
        for (var i = 0; i < length; i++)
        {
            var code = row[start + i].Code;
            buffer[i] = code == '\0' ? ' ' : code;
        }

        return new string(buffer);
    }

    private static int CountColumns(ReadOnlySpan<TerminalCell> row, int start, int end)
    {
        var count = 0;
        for (var i = start; i < end; i++)
        {
            count += row[i].WideTail ? 0 : 1;
        }

        return Math.Max(1, count);
    }

    private static Windows.UI.Color ResolveForeground(CellStyle style)
    {
        var fg = ResolveColor(style.Fg, isForeground: true);
        var bg = ResolveColor(style.Bg, isForeground: false);
        return (style.Flags & CellFlags.Reverse) != 0 ? bg : fg;
    }

    private static Windows.UI.Color ResolveBackground(CellStyle style)
    {
        var fg = ResolveColor(style.Fg, isForeground: true);
        var bg = ResolveColor(style.Bg, isForeground: false);
        return (style.Flags & CellFlags.Reverse) != 0 ? fg : bg;
    }

    /// <summary>The cursor, drawn only when its screen row is on the visible part of the buffer.</summary>
    private void DrawCursor(CanvasDrawingSession session, int topLine, int lastLine)
    {
        if (!_emulator.CursorVisible || !_caretVisible)
        {
            return;
        }

        var absolute = _emulator.ScrollbackCount + _emulator.CursorRow;
        if (absolute < topLine || absolute > lastLine)
        {
            return;
        }

        var y = PadTop + (absolute - topLine) * _lineHeight;
        var x = PadLeft + _emulator.CursorCol * _charWidth;
        var focused = FocusState != FocusState.Unfocused;

        // The caret hugs the glyphs, not the whole cell: the text occupies roughly
        // fontSize pixels from the top of the cell (line spacing 13 in a 16 cell),
        // so a full-height block hangs below the character line. Nudged up 2px from
        // the geometric position - that is where the eye reads the glyph centre.
        var cursorTop = (float)(y - 1);
        var cursorHeight = (float)(_fontSize + 1);

        if (_emulator.Shape is TerminalEmulator.CursorShape.BlinkBlock or TerminalEmulator.CursorShape.Block)
        {
            if (focused)
            {
                // A filled block in the cursor colour; the glyph under it flips to the
                // background colour so it stays readable.
                session.FillRectangle((float)x, cursorTop, (float)_charWidth, cursorHeight, _caretBrush!);
                var row = _emulator.GetLine(absolute);
                var cell = row[_emulator.CursorCol];
                if (cell.Code is not ' ' and not '\0')
                {
                    var text = cell.Code.ToString();
                    var style = cell.Style;
                    var format = (style.Flags & CellFlags.Bold) != 0 ? _boldFormat! : _textFormat!;
                    session.DrawText(text, (float)x, (float)y, _caretGlyphBrush!, format);
                }
            }
            else
            {
                session.DrawRectangle((float)x, cursorTop, (float)_charWidth, cursorHeight, _caretBrush!);
            }
        }
        else if (_emulator.Shape is TerminalEmulator.CursorShape.BlinkUnderline or TerminalEmulator.CursorShape.Underline)
        {
            session.FillRectangle((float)x, (float)(y + _fontSize - 1), (float)_charWidth, 2, _caretBrush!);
        }
        else
        {
            session.FillRectangle((float)x, cursorTop, 2, cursorHeight - 2, _caretBrush!);
        }
    }

    // ------------------------------------------------------------------ scroll

    /// <summary>The highest top line: the one that puts the live screen's last row at the bottom.</summary>
    private int MaxTopLine => Math.Max(0, _emulator.TotalLines - _emulator.Rows);

    /// <summary>The absolute line at the top of the viewport - shared by drawing and hit-testing.</summary>
    private int TopLine => _pinned ? MaxTopLine : Math.Clamp(_topLine, 0, MaxTopLine);

    /// <summary>Moves the viewport; reaching the bottom pins it to the live screen again.</summary>
    private void SetTopLine(int line)
    {
        _topLine = Math.Clamp(line, 0, MaxTopLine);
        _pinned = _topLine >= MaxTopLine;
    }

    /// <summary>
    /// Mirrors the viewport onto the scrollbar. One scroll step is one line, so the
    /// extent is exactly the scrollable lines plus one viewport - offset/lineHeight
    /// is the top line, with no padding or remainder to round away.
    /// </summary>
    private void SyncScrollbar()
    {
        EnsureMetrics();
        var viewport = Canvas.ActualHeight;
        if (viewport <= 0)
        {
            return;
        }

        // Every output chunk used to rewrite both extent properties unconditionally,
        // invalidating ScrollViewer measure per chunk during bursts; during ordinary
        // output neither value moves.
        var extentWidth = Canvas.ActualWidth;
        var extentHeight = MaxTopLine * _lineHeight + viewport;
        if (Extent.Width != extentWidth)
        {
            Extent.Width = extentWidth;
        }

        if (Extent.Height != extentHeight)
        {
            Extent.Height = extentHeight;
        }

        var target = TopLine * _lineHeight;
        if (Math.Abs(Scroller.VerticalOffset - target) > 0.5)
        {
            _expectedOffset = target;
            Scroller.ChangeView(null, target, null, disableAnimation: true);
        }
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        var offset = Scroller.VerticalOffset;

        // Our own ChangeView landing - the viewport is already where it should be.
        if (Math.Abs(offset - _expectedOffset) < 0.5)
        {
            return;
        }

        // Our own ChangeView, clamped because layout has not grown the extent yet.
        // Reading it as a user scroll would unpin the view one line short of the
        // prompt; Extent.SizeChanged re-issues it once layout catches up.
        if (_expectedOffset > Scroller.ScrollableHeight + 0.5)
        {
            return;
        }

        // Otherwise the user dragged the scrollbar.
        _expectedOffset = double.NaN;
        var previous = TopLine;
        SetTopLine((int)Math.Round(offset / _lineHeight));
        if (TopLine != previous)
        {
            InvalidateAll();
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Canvas).Properties.MouseWheelDelta;

        // When the app tracks the mouse, the wheel belongs to it - fzf, less and
        // friends scroll their own way. Wheel reports are button 64 (up) / 65 (down).
        if (_emulator.MouseMode != TerminalEmulator.MouseTracking.None)
        {
            var button = delta > 0 ? 64 : 65;
            var cell = PointToCell(e.GetCurrentPoint(Canvas).Position);
            _ = WriteToShellAsync(EncodeMouseReport(button, cell.Col, ViewportRow(cell.Line), pressed: true));
            e.Handled = true;
            return;
        }

        // Three lines per notch; touchpad fractions accumulate until they make a line.
        _wheelLines += delta * 3 / 120.0;
        var lines = (int)_wheelLines;
        _wheelLines -= lines;
        if (lines != 0)
        {
            SetTopLine(TopLine - lines);
            SyncScrollbar();
            InvalidateAll();
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------------ pointer

    /// <summary>Ctrl+click on a URL or file path in the output; the host decides how to open it.</summary>
    public event EventHandler<Codale.App.Services.LinkMatch>? LinkActivated;

    /// <summary>The link under the pointer while Ctrl is held: underlined, and clickable.</summary>
    private (int Line, int From, int To, Codale.App.Services.LinkMatch Match)? _hoverLink;

    /// <summary>
    /// What the hover computation was last built from. With Ctrl held, every pointer
    /// move used to rebuild the row text and re-run both link regexes over it, even
    /// though the cell (and the buffer) had not moved.
    /// </summary>
    private (int Line, int Col, long Revision)? _linkCacheKey;
    private (int Line, int From, int To, Codale.App.Services.LinkMatch Match)? _linkCache;

    private static readonly Windows.UI.Color LinkColor = FromHex("#61AFEF");

    /// <summary>One buffer row as text, a character per cell, so a match's index is its column.</summary>
    private string LineText(int line)
    {
        var row = _emulator.GetLine(line);
        var chars = new char[Math.Min(row.Length, _emulator.Cols)];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = row[i].Code is '\0' ? ' ' : row[i].Code;
        }

        return new string(chars);
    }

    private (int Line, int From, int To, Codale.App.Services.LinkMatch Match)? LinkAt(Windows.Foundation.Point point)
    {
        // An app tracking the mouse owns clicks; Shift is the usual way to take them back.
        if (_emulator.MouseMode != TerminalEmulator.MouseTracking.None && !IsShiftDown())
        {
            return null;
        }

        return ComputeLinkAt(point);
    }

    /// <summary>The link for the cell under the point, cached against the cell and the buffer revision.</summary>
    private (int Line, int From, int To, Codale.App.Services.LinkMatch Match)? ComputeLinkAt(Windows.Foundation.Point point)
    {
        var (line, col) = PointToCell(point);
        if (_linkCacheKey == (line, col, _emulator.Revision))
        {
            return _linkCache;
        }

        var link = Codale.App.Services.LinkFinder.At(LineText(line), col) is { } match
            ? (line, match.Index, match.Index + match.Length, match)
            : default((int, int, int, Codale.App.Services.LinkMatch)?);

        _linkCacheKey = (line, col, _emulator.Revision);
        _linkCache = link;
        return link;
    }

    private void UpdateHover(PointerRoutedEventArgs e)
    {
        var link = IsCtrlDown() ? ComputeLinkAt(e.GetCurrentPoint(Canvas).Position) : default((int, int, int, Codale.App.Services.LinkMatch)?);
        if (Equals(link, _hoverLink))
        {
            return;
        }

        _hoverLink = link;
        ProtectedCursor = link is null ? null : InputSystemCursor.Create(InputSystemCursorShape.Hand);
        InvalidateAll();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverLink is not null)
        {
            _hoverLink = null;
            ProtectedCursor = null;
            InvalidateAll();
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);

        if (IsCtrlDown() && LinkAt(e.GetCurrentPoint(Canvas).Position) is { } link)
        {
            LinkActivated?.Invoke(this, link.Match);
            e.Handled = true;
            return;
        }

        Canvas.CapturePointer(e.Pointer);
        var point = e.GetCurrentPoint(Canvas);

        // An app tracking the mouse owns clicks and drags; Shift takes them back for selection.
        if (AppOwnsMouse)
        {
            _reportedButton = MouseButtonCode(point);
            _lastReportedCell = PointToCell(point.Position);
            _ = WriteToShellAsync(EncodeMouseReport(_reportedButton, _lastReportedCell.Col, ViewportRow(_lastReportedCell.Line), pressed: true));
            e.Handled = true;
            return;
        }

        // Double click selects the word under it.
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
            e.Handled = true;
            return;
        }

        var position = PointToCell(point.Position);
        if (IsShiftDown() && HasSelection)
        {
            _selectionHead = position;
        }
        else
        {
            _selectionAnchor = _selectionHead = position;
            _selecting = true;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
        }

        InvalidateAll();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas);

        // Motion reports: 1003 sends every move, 1002 only moves with a button held.
        // Button codes get +32 for motion; 35 is "no button".
        if (AppOwnsMouse && _emulator.MouseMode is TerminalEmulator.MouseTracking.Drag or TerminalEmulator.MouseTracking.Any)
        {
            var held = Canvas.PointerCaptures is { Count: > 0 };
            if (held || _emulator.MouseMode == TerminalEmulator.MouseTracking.Any)
            {
                var cell = PointToCell(point.Position);
                if (cell != _lastReportedCell)
                {
                    _lastReportedCell = cell;
                    var button = (held ? _reportedButton : 3) + 32;
                    _ = WriteToShellAsync(EncodeMouseReport(button, cell.Col, ViewportRow(cell.Line), pressed: true));
                }
            }

            e.Handled = true;
            return;
        }

        if (Canvas.PointerCaptures is not { Count: > 0 })
        {
            UpdateHover(e);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_selecting)
        {
            _selectionHead = PointToCell(point.Position);
            InvalidateAll();
        }
        else if (_clickCount >= 2)
        {
            // Dragging after a double click extends the word selection.
            SelectWordAt(point.Position);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Canvas.ReleasePointerCapture(e.Pointer);

        // The app only hears about clicks it asked for; wheel reports carry no
        // coordinates, clicks do.
        var point = e.GetCurrentPoint(Canvas);
        if (_emulator.MouseMode != TerminalEmulator.MouseTracking.None && _reportedButton >= 0)
        {
            var cell = PointToCell(point.Position);
            // X10 encoding has no per-button release: it is always button 3.
            var button = _emulator.SgrMouse ? _reportedButton : 3;
            _ = WriteToShellAsync(EncodeMouseReport(button, cell.Col, ViewportRow(cell.Line), pressed: false));
        }

        _reportedButton = -1;

        if (_selecting)
        {
            ProtectedCursor = _hoverLink is null ? null : InputSystemCursor.Create(InputSystemCursorShape.Hand);
        }

        _selecting = false;
    }

    private bool AppOwnsMouse => _emulator.MouseMode != TerminalEmulator.MouseTracking.None && !IsShiftDown();

    /// <summary>The button of an app-reported press (0 left, 1 middle, 2 right), or -1 when none is held.</summary>
    private int _reportedButton = -1;
    private (int Line, int Col) _lastReportedCell;

    private static int MouseButtonCode(PointerPoint point) =>
        point.Properties.IsRightButtonPressed ? 2 : point.Properties.IsMiddleButtonPressed ? 1 : 0;

    /// <summary>A buffer line as a 0-based row of the visible viewport, which is what mouse reports use.</summary>
    private int ViewportRow(int line) => line - TopLine;

    /// <summary>SGR (1006) mouse encoding when the app requested it, X10 otherwise. Col is 0-based; pass pressed=false for release.</summary>
    private string EncodeMouseReport(int button, int col, int line, bool pressed)
    {
        if (_emulator.SgrMouse)
        {
            return $"\x1b[<{button};{col + 1};{line + 1}{(pressed ? 'M' : 'm')}";
        }

        // X10: three raw bytes after CSI M, each offset by 32.
        var b = (char)(32 + button);
        var x = (char)(32 + Math.Min(col + 1, 223));
        var y = (char)(32 + Math.Min(line + 1, 223));
        return $"\x1b[M{b}{x}{y}";
    }

    /// <summary>Maps a point to an absolute buffer line and column, clamped to the content.</summary>
    private (int Line, int Col) PointToCell(Windows.Foundation.Point point)
    {
        EnsureMetrics();

        // Same TopLine the frame was drawn from, so a click lands on the row under it.
        var totalLines = _emulator.TotalLines;
        var row = (int)Math.Floor((point.Y - PadTop) / _lineHeight);
        var line = Math.Clamp(TopLine + row, 0, Math.Max(0, totalLines - 1));
        var col = Math.Clamp((int)Math.Floor((point.X - PadLeft) / _charWidth), 0, _emulator.Cols - 1);
        return (line, col);
    }

    private void SelectWordAt(Windows.Foundation.Point point)
    {
        var (line, col) = PointToCell(point);
        var row = _emulator.GetLine(line).ToArray();

        bool IsWordChar(int i) =>
            i < row.Length && (char.IsLetterOrDigit(row[i].Code) || row[i].Code == '_'
                || row[i].Code == '-' || row[i].Code == '.' || row[i].Code == '/' || row[i].Code == '\\');

        var start = col;
        while (start > 0 && IsWordChar(start - 1))
        {
            start--;
        }

        var end = col;
        while (end < row.Length && IsWordChar(end))
        {
            end++;
        }

        _selectionAnchor = (line, start);
        _selectionHead = (line, end);
        InvalidateAll();
    }

    /// <summary>The selection's intersection with one row, or null: from inclusive, to exclusive.</summary>
    private (int from, int to)? SelectionOnRow(int line)
    {
        if (!HasSelection)
        {
            return null;
        }

        var (a, b) = _selectionAnchor.CompareTo(_selectionHead) <= 0
            ? (_selectionAnchor, _selectionHead)
            : (_selectionHead, _selectionAnchor);

        if (line < a.Line || line > b.Line)
        {
            return null;
        }

        var from = line == a.Line ? a.Col : 0;
        var to = line == b.Line ? Math.Min(b.Col + 1, _emulator.Cols) : _emulator.Cols;
        return (from, to);
    }

    private string GetSelectionText()
    {
        var (a, b) = _selectionAnchor.CompareTo(_selectionHead) <= 0
            ? (_selectionAnchor, _selectionHead)
            : (_selectionHead, _selectionAnchor);

        if (a == b)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        for (var line = a.Line; line <= b.Line; line++)
        {
            var row = _emulator.GetLine(line).ToArray();
            // A scrollback line written before the window widened can be narrower
            // than the selection's column.
            var from = Math.Min(line == a.Line ? a.Col : 0, row.Length);
            var to = line == b.Line ? Math.Min(b.Col + 1, row.Length) : row.Length;

            var last = from - 1;
            for (var col = from; col < to; col++)
            {
                if (row[col].Code != ' ' && !row[col].WideTail)
                {
                    last = col;
                }
            }

            for (var col = from; col <= last; col++)
            {
                if (!row[col].WideTail)
                {
                    builder.Append(row[col].Code == '\0' ? ' ' : row[col].Code);
                }
            }

            if (line != b.Line)
            {
                builder.Append("\r\n");
            }
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------ keyboard

    private void OnTerminalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsCtrlDown();
        var alt = IsAltDown();
        var shift = IsShiftDown();

        // Clipboard shortcuts win over sending control characters - with a selection,
        // Ctrl+C copies instead of interrupting the shell.
        if (ctrl && e.Key == VirtualKey.C && HasSelection)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        // Ctrl+Insert copies too, and never interrupts the shell.
        if (ctrl && e.Key == VirtualKey.Insert)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == VirtualKey.V)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        var sequence = EncodeKey(e.Key, ctrl, alt, shift);
        if (sequence is not null)
        {
            _ = WriteToShellAsync(sequence);
            e.Handled = true;
            return;
        }

        // Control combinations arrive here; printable characters come through
        // CharacterReceived instead.
        if (ctrl && e.Key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            var code = (char)(e.Key - VirtualKey.A + 1);
            _ = WriteToShellAsync((alt ? "\x1b" : "") + code);
            e.Handled = true;
            return;
        }

        if (ctrl && ControlPunctuation.TryGetValue(e.Key, out var mapped))
        {
            _ = WriteToShellAsync(mapped.ToString());
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+left bracket and friends, mapped to their control characters. The OEM keys have no VirtualKey names, so they are addressed by scancode value; ^ is Shift+6, _ is Shift+minus.</summary>
    private static readonly IReadOnlyDictionary<VirtualKey, char> ControlPunctuation = new Dictionary<VirtualKey, char>
    {
        [VirtualKey.Space] = '\0',    // Ctrl+Space
        [(VirtualKey)219] = '\x1b',   // Ctrl+[
        [(VirtualKey)220] = '\x1c',   // Ctrl+\
        [(VirtualKey)221] = '\x1d',   // Ctrl+]
        [(VirtualKey)54] = '\x1e',    // Ctrl+^
        [(VirtualKey)189] = '\x1f',   // Ctrl+_
    };

    /// <summary>
    /// VT sequences for the special keys, xterm-style: arrows respect DECCKM
    /// (application cursor keys), modifiers are encoded as the 1;m parameter,
    /// function keys use their standard CSI numbers.
    /// </summary>
    private string? EncodeKey(VirtualKey key, bool ctrl, bool alt, bool shift)
    {
        var modifier = (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        var esc = alt ? "\x1b" : "";
        var mods = modifier > 1 ? $";{modifier + 1}" : "";

        switch (key)
        {
            case VirtualKey.Enter:
                return esc + "\r";
            case VirtualKey.Back:
                return esc + "\x7f";
            case VirtualKey.Escape:
                return "\x1b";
            case VirtualKey.Tab:
                return shift ? "\x1b[Z" : "\t";
            case VirtualKey.Up: return Arrow('A', mods);
            case VirtualKey.Down: return Arrow('B', mods);
            case VirtualKey.Right: return Arrow('C', mods);
            case VirtualKey.Left: return Arrow('D', mods);
            case VirtualKey.Home: return ApplicationKeys('H', 'H', mods);
            case VirtualKey.End: return ApplicationKeys('F', 'F', mods);
            case VirtualKey.Insert: return $"\x1b[2{mods}~";
            case VirtualKey.Delete: return $"\x1b[3{mods}~";
            case VirtualKey.PageUp: return $"\x1b[5{mods}~";
            case VirtualKey.PageDown: return $"\x1b[6{mods}~";
            case VirtualKey.F1: return "\x1bOP";
            case VirtualKey.F2: return "\x1bOQ";
            case VirtualKey.F3: return "\x1bOR";
            case VirtualKey.F4: return "\x1bOS";
            case VirtualKey.F5: return $"\x1b[15{mods}~";
            case VirtualKey.F6: return $"\x1b[17{mods}~";
            case VirtualKey.F7: return $"\x1b[18{mods}~";
            case VirtualKey.F8: return $"\x1b[19{mods}~";
            case VirtualKey.F9: return $"\x1b[20{mods}~";
            case VirtualKey.F10: return $"\x1b[21{mods}~";
            case VirtualKey.F11: return $"\x1b[23{mods}~";
            case VirtualKey.F12: return $"\x1b[24{mods}~";
            default:
                return null;
        }
    }

    private string Arrow(char letter, string mods)
    {
        if (mods.Length > 0)
        {
            return $"\x1b[1{mods}{letter}";
        }

        return _emulator.ApplicationCursorKeys ? $"\x1bO{letter}" : $"\x1b[{letter}";
    }

    private string ApplicationKeys(char csiChar, char ss3Char, string mods)
    {
        if (mods.Length > 0)
        {
            return $"\x1b[1{mods}{csiChar}";
        }

        return _emulator.ApplicationCursorKeys ? $"\x1bO{ss3Char}" : $"\x1b[{csiChar}";
    }

    private void OnTerminalCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (IsCtrlDown() || IsAltDown())
        {
            // Control combos are handled in KeyDown as explicit sequences.
            return;
        }

        var ch = args.Character;
        if (ch < ' ')
        {
            return;
        }

        args.Handled = true;
        _ = WriteToShellAsync(ch.ToString());
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsCtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsAltDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
}
