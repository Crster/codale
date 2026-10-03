namespace Codale.Terminal;

/// <summary>
/// The terminal's cell grid and its scrollback. The buffer owns the geometry
/// (margins, tab stops, cursor) and the primitive operations the VT parser calls;
/// parsing itself lives in <see cref="TerminalEmulator"/>.
/// </summary>
public sealed class TerminalBuffer
{
    /// <summary>The current screen: one cell array per row, top to bottom.</summary>
    private TerminalCell[][] _grid;

    private readonly ScrollbackRing _scrollback = new();

    /// <summary>How many scrolled-off lines are kept. The oldest lines drop first.</summary>
    public int ScrollbackLimit = 10000;

    private int _cols;
    private int _rows;

    /// <summary>Scroll region, 0-based inclusive. The full screen by default.</summary>
    private int _scrollTop;
    private int _scrollBottom;

    private int _cursorRow;
    private int _cursorCol;

    /// <summary>
    /// xterm's deferred wrap: after printing in the last column the cursor stays
    /// put and only wraps when the next printable character arrives. Without this,
    /// ConPTY output that lands exactly on the last column would wrap too early.
    /// </summary>
    private bool _pendingWrap;

    private CellStyle _currentStyle = CellStyle.Default;

    /// <summary>DECSC state, kept per screen because main and alt never share it.</summary>
    private struct SavedCursor
    {
        public int Row;
        public int Col;
        public CellStyle Style;
        public bool OriginMode;
        public bool PendingWrap;
    }

    private SavedCursor _savedMain;
    private SavedCursor _savedAlt;
    private SavedCursor _saved;

    private readonly HashSet<int> _tabStops = [];

    // The main and alt grids both persist; _grid points at whichever is live.
    private TerminalCell[][] _mainGrid = [];
    private TerminalCell[][] _altGrid = [];

    // Modes that survive a buffer switch; the alt screen keeps its own origin mode.
    private bool _altScreen;
    private bool _originModeMain;
    private bool _originModeAlt;

    public TerminalBuffer(int cols = 80, int rows = 25)
    {
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _grid = MakeGrid(_cols, _rows);
        ResetMargins();
        ResetTabStops();
    }

    public int Cols => _cols;
    public int Rows => _rows;

    public int CursorRow => _cursorRow;
    public int CursorCol => _cursorCol;
    public bool PendingWrap => _pendingWrap;

    public int ScrollTop => _scrollTop;
    public int ScrollBottom => _scrollBottom;

    public bool OriginMode => _altScreen ? _originModeAlt : _originModeMain;

    public bool InAlternate => _altScreen;

    public CellStyle CurrentStyle => _currentStyle;

    /// <summary>DECAWM: whether printing past the last column wraps to the next line.</summary>
    private bool _autowrap = true;

    public void SetAutowrap(bool on) => _autowrap = on;

    public int ScrollbackCount => _scrollback.Count;

    /// <summary>
    /// Row <paramref name="index"/> of the combined buffer, where 0 is the oldest
    /// scrollback line and <see cref="ScrollbackCount"/> is the top of the live screen.
    /// The alt screen has no scrollback, so there the index addresses the screen directly.
    /// </summary>
    public ReadOnlySpan<TerminalCell> GetLine(int index)
    {
        var offset = _altScreen ? 0 : _scrollback.Count;
        var screenIndex = index - offset;
        if (screenIndex < 0)
        {
            return _scrollback[index];
        }

        return _grid[screenIndex];
    }

    /// <summary>Whether the scrollback is part of the addressable buffer right now (it never is on the alt screen).</summary>
    public bool ScrollbackVisible => !_altScreen;

    /// <summary>The live screen row, 0 = top of the screen.</summary>
    public TerminalCell[] GetScreenRow(int index) => _grid[index];

    // ------------------------------------------------------------------ cursor and style

    public void MoveCursorTo(int row, int col)
    {
        _pendingWrap = false;

        // Origin mode (DECOM) makes row coordinates relative to the scroll region.
        var top = OriginMode ? _scrollTop : 0;
        var bottom = OriginMode ? _scrollBottom : _rows - 1;

        _cursorRow = Math.Clamp(row, top, bottom);
        _cursorCol = Math.Clamp(col, 0, _cols - 1);
    }

    public void MoveCursorCol(int col)
    {
        _pendingWrap = false;
        _cursorCol = Math.Clamp(col, 0, _cols - 1);
    }

    public void MoveCursorRow(int row)
    {
        _pendingWrap = false;
        _cursorRow = Math.Clamp(row, OriginMode ? _scrollTop : 0, OriginMode ? _scrollBottom : _rows - 1);
    }

    /// <summary>Moves the cursor without origin-mode clamping (for CUB/CUF which always stay on the line).</summary>
    public void MoveCursorHorizontal(int col) => _cursorCol = Math.Clamp(col, 0, _cols - 1);

    public void MoveCursorVertical(int delta) => MoveCursorRow(_cursorRow + delta);

    /// <summary>Advances one cell right, wrapping with xterm's deferred-wrap semantics.</summary>
    public void AdvanceCursor()
    {
        if (_cursorCol < _cols - 1)
        {
            _cursorCol++;
            _pendingWrap = false;
        }
        else if (_pendingWrap)
        {
            LineFeed();
        }
        else
        {
            _pendingWrap = true;
        }
    }

    public void SetStyle(CellStyle style) => _currentStyle = style;

    public void SaveCursor()
    {
        _saved = new SavedCursor
        {
            Row = _cursorRow,
            Col = _cursorCol,
            Style = _currentStyle,
            OriginMode = OriginMode,
            PendingWrap = _pendingWrap,
        };

        if (_altScreen)
        {
            _savedAlt = _saved;
        }
        else
        {
            _savedMain = _saved;
        }
    }

    public void RestoreCursor()
    {
        _saved = _altScreen ? _savedAlt : _savedMain;
        _cursorRow = Math.Clamp(_saved.Row, 0, _rows - 1);
        _cursorCol = Math.Clamp(_saved.Col, 0, _cols - 1);
        _currentStyle = _saved.Style;
        SetOriginMode(_saved.OriginMode);
        _pendingWrap = _saved.PendingWrap;
    }

    public void SetOriginMode(bool on)
    {
        if (_altScreen)
        {
            _originModeAlt = on;
        }
        else
        {
            _originModeMain = on;
        }

        if (on)
        {
            MoveCursorTo(0, 0);
        }
    }

    // ------------------------------------------------------------------ tabs

    public void ResetTabStops()
    {
        _tabStops.Clear();
        for (var col = 8; col < _cols; col += 8)
        {
            _tabStops.Add(col);
        }
    }

    public void SetTabStop() => _tabStops.Add(_cursorCol);

    public void ClearTabStop() => _tabStops.Remove(_cursorCol);

    public void ClearAllTabStops() => _tabStops.Clear();

    /// <summary>Moves to the next tab stop, or the last column when none remain.</summary>
    public void TabForward()
    {
        var col = _cursorCol + 1;
        while (col < _cols - 1 && !_tabStops.Contains(col))
        {
            col++;
        }

        _cursorCol = Math.Min(col, _cols - 1);
        _pendingWrap = false;
    }

    /// <summary>Moves to the previous tab stop, or column 0 when none remain.</summary>
    public void TabBackward()
    {
        var col = _cursorCol - 1;
        while (col > 0 && !_tabStops.Contains(col))
        {
            col--;
        }

        _cursorCol = Math.Max(col, 0);
        _pendingWrap = false;
    }

    // ------------------------------------------------------------------ line feeding

    /// <summary>Line feed: scrolls the region when the cursor is on its bottom edge, otherwise moves down.</summary>
    public void LineFeed()
    {
        _pendingWrap = false;
        if (_cursorRow == _scrollBottom)
        {
            ScrollUp(1);
        }
        else if (_cursorRow < _rows - 1)
        {
            _cursorRow++;
        }
    }

    /// <summary>Reverse line feed (ESC M): scrolls the region backwards at its top edge.</summary>
    public void ReverseLineFeed()
    {
        _pendingWrap = false;
        if (_cursorRow == _scrollTop)
        {
            ScrollDown(1);
        }
        else if (_cursorRow > 0)
        {
            _cursorRow--;
        }
    }

    // ------------------------------------------------------------------ scrolling and regions

    public void SetMargins(int top, int bottom)
    {
        // An invalid or full-screen pair resets the margins.
        if (top > bottom || (top == 0 && bottom == _rows - 1))
        {
            ResetMargins();
            return;
        }

        _scrollTop = Math.Clamp(top, 0, _rows - 1);
        _scrollBottom = Math.Clamp(bottom, 0, _rows - 1);
        MoveCursorTo(0, 0);
    }

    public void ResetMargins()
    {
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    /// <summary>Scrolls the region up by <paramref name="count"/> lines; vacated lines become blank. The top line(s) go to scrollback when the region is the full screen.</summary>
    public void ScrollUp(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);

        if (_scrollTop == 0 && !_altScreen)
        {
            for (var i = 0; i < count; i++)
            {
                PushToScrollback(_grid[i]);
            }
        }

        for (var row = _scrollTop; row + count <= _scrollBottom; row++)
        {
            _grid[row] = _grid[row + count];
        }

        for (var row = _scrollBottom - count + 1; row <= _scrollBottom; row++)
        {
            _grid[row] = NewBlankRow();
        }
    }

    /// <summary>Scrolls the region down by <paramref name="count"/> lines, opening blank lines at the top.</summary>
    public void ScrollDown(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);

        for (var row = _scrollBottom; row - count >= _scrollTop; row--)
        {
            _grid[row] = _grid[row - count];
        }

        for (var row = _scrollTop; row < _scrollTop + count; row++)
        {
            _grid[row] = NewBlankRow();
        }
    }

    /// <summary>Moves lines out of the region between two rows, like IL/DL: everything below the deleted block moves up.</summary>
    public void InsertLines(int atRow, int count)
    {
        if (atRow < _scrollTop || atRow > _scrollBottom || count <= 0)
        {
            return;
        }

        count = Math.Min(count, _scrollBottom - atRow + 1);
        for (var row = _scrollBottom; row - count >= atRow; row--)
        {
            _grid[row] = _grid[row - count];
        }

        for (var row = atRow; row < atRow + count; row++)
        {
            _grid[row] = NewBlankRow();
        }
    }

    public void DeleteLines(int atRow, int count)
    {
        if (atRow < _scrollTop || atRow > _scrollBottom || count <= 0)
        {
            return;
        }

        count = Math.Min(count, _scrollBottom - atRow + 1);
        for (var row = atRow; row + count <= _scrollBottom; row++)
        {
            _grid[row] = _grid[row + count];
        }

        for (var row = _scrollBottom - count + 1; row <= _scrollBottom; row++)
        {
            _grid[row] = NewBlankRow();
        }
    }

    // ------------------------------------------------------------------ cell writes

    /// <summary>Writes one character at the cursor and advances. A double-width character claims the next cell too.</summary>
    public void Put(char code, CellStyle style)
    {
        if (_pendingWrap)
        {
            if (_autowrap)
            {
                LineFeed();
                _cursorCol = 0;
            }
            else
            {
                _pendingWrap = false;
            }
        }

        ref var cell = ref _grid[_cursorRow][_cursorCol];
        cell.Code = code;
        cell.Style = style;
        cell.WideTail = false;

        // CJK-range characters occupy two cells; the tail renders as blank.
        if (IsWide(code) && _cursorCol + 1 < _cols)
        {
            var tail = TerminalCell.Blank(style);
            tail.WideTail = true;
            _grid[_cursorRow][_cursorCol + 1] = tail;
            _cursorCol++;
        }

        AdvanceCursor();
    }

    /// <summary>True for the East Asian wide ranges the terminal is likely to meet.</summary>
    private static bool IsWide(char code)
    {
        int c = code;
        return c is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0xA4CF and not 0x303F
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6;
    }

    /// <summary>Erases cells between two columns of one row, inclusive-exclusive, with <paramref name="style"/> as the blank style.</summary>
    public void EraseInLine(int row, int fromCol, int toCol)
    {
        row = Math.Clamp(row, 0, _rows - 1);
        fromCol = Math.Max(0, fromCol);
        toCol = Math.Min(toCol, _cols);

        var style = EraseStyle();
        for (var col = fromCol; col < toCol; col++)
        {
            _grid[row][col] = TerminalCell.Blank(style);
        }
    }

    /// <summary>Erases whole rows, inclusive-exclusive.</summary>
    public void EraseRows(int fromRow, int toRow)
    {
        fromRow = Math.Max(0, fromRow);
        toRow = Math.Min(toRow, _rows);

        var style = EraseStyle();
        for (var row = fromRow; row < toRow; row++)
        {
            _grid[row] = NewBlankRow(style);
        }
    }

    /// <summary>Whether erased cells adopt the current background (BCE) or stay plain. ConPTY expects background-colour erase.</summary>
    private CellStyle EraseStyle() => new(TerminalColor.Default, _currentStyle.Bg, CellFlags.None);

    private TerminalCell[] NewBlankRow() => NewBlankRow(EraseStyle());

    private TerminalCell[] NewBlankRow(CellStyle style)
    {
        // A full scrollback evicts one line per scrolled line; recycling its array keeps a
        // long-running terminal from allocating a row per newline.
        var row = _spareRow;
        if (row is not null && row.Length == _cols)
        {
            _spareRow = null;
        }
        else
        {
            row = new TerminalCell[_cols];
        }

        Array.Fill(row, TerminalCell.Blank(style));
        return row;
    }

    // ------------------------------------------------------------------ scrollback

    /// <summary>A row dropped from a full scrollback, nothing else references it, so the next blank row can reuse it.</summary>
    private TerminalCell[]? _spareRow;

    /// <summary>Moves a finished screen line into scrollback, dropping the oldest when the limit is reached.</summary>
    private void PushToScrollback(TerminalCell[] line)
    {
        if (_scrollback.Push(line, ScrollbackLimit, out var evicted))
        {
            TrimmedLines++;
            _spareRow = evicted;
        }
    }

    /// <summary>
    /// How many lines have ever fallen off the top of a full scrollback. Every trim
    /// shifts absolute line indices down by one, so a renderer holding indices (a
    /// selection, a scrolled-up viewport) subtracts the change to stay on its text.
    /// </summary>
    public long TrimmedLines { get; private set; }

    /// <summary>Drops the scrollback (ED 3, and the Clear menu command).</summary>
    public void ClearScrollback() => _scrollback.Clear();

    /// <summary>Power-on reset (ESC c): blank screen, default style, cursor home, margins and tab stops reset.</summary>
    public void Reset()
    {
        _grid = MakeGrid(_cols, _rows);
        _altScreen = false;
        _currentStyle = CellStyle.Default;
        _cursorRow = _cursorCol = 0;
        _pendingWrap = false;
        _originModeMain = _originModeAlt = false;
        _savedMain = _savedAlt = default;
        ResetMargins();
        ResetTabStops();
    }

    // ------------------------------------------------------------------ alternate screen

    /// <summary>
    /// Switches to or from the alternate buffer. Both grids persist independently:
    /// leaving the alt screen puts the main screen's contents back exactly as they
    /// were (this is what full-screen programs like less rely on).
    /// </summary>
    public void SetAlternateScreen(bool on)
    {
        if (on == _altScreen)
        {
            return;
        }

        if (on)
        {
            _mainGrid = _grid;
            _altGrid = MakeGrid(_cols, _rows);
            _grid = _altGrid;
            _altScreen = true;
        }
        else
        {
            _grid = _mainGrid;
            _altScreen = false;
        }
    }

    // ------------------------------------------------------------------ resize

    /// <summary>
    /// Resizes the grid. Lines keep their content and are truncated or extended with
    /// blanks; the cursor is clamped. No reflow: ConPTY repaints the screen after a
    /// resize, so rewrapping here would only fight its own redraw.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == _cols && rows == _rows)
        {
            return;
        }

        var style = EraseStyle();

        // Shrinking below the cursor: slide the screen up so the cursor's line stays
        // on screen (the lines that fall off the top go to scrollback on the main
        // screen), instead of cutting off the prompt at the bottom.
        var shift = Math.Max(0, _cursorRow - (rows - 1));
        if (!_altScreen)
        {
            for (var row = 0; row < shift && row < _grid.Length; row++)
            {
                PushToScrollback(_grid[row]);
            }
        }

        _grid = ResizeGrid(_grid, shift, cols, rows, style);

        // The screen that is not showing has to follow too, or switching back to it
        // later would address a grid of the old size.
        if (_altScreen)
        {
            _mainGrid = ResizeGrid(_mainGrid, 0, cols, rows, CellStyle.Default);
            _altGrid = _grid;
        }

        _cols = cols;
        _rows = rows;

        _cursorRow = Math.Clamp(_cursorRow - shift, 0, rows - 1);
        _cursorCol = Math.Clamp(_cursorCol, 0, cols - 1);
        _pendingWrap = false;
        ResetMargins();
        ResetTabStops();
    }

    /// <summary>Copies <paramref name="grid"/> from row <paramref name="skip"/> into a cols x rows grid, truncating or padding with blanks.</summary>
    private static TerminalCell[][] ResizeGrid(TerminalCell[][] grid, int skip, int cols, int rows, CellStyle style)
    {
        var newGrid = new TerminalCell[rows][];
        for (var row = 0; row < rows; row++)
        {
            var newRow = new TerminalCell[cols];
            var copy = 0;
            if (row + skip < grid.Length)
            {
                var oldRow = grid[row + skip];
                copy = Math.Min(cols, oldRow.Length);
                Array.Copy(oldRow, newRow, copy);
            }

            for (var col = copy; col < cols; col++)
            {
                newRow[col] = TerminalCell.Blank(style);
            }

            newGrid[row] = newRow;
        }

        return newGrid;
    }

    private static TerminalCell[][] MakeGrid(int cols, int rows)
    {
        var grid = new TerminalCell[rows][];
        for (var row = 0; row < rows; row++)
        {
            var line = new TerminalCell[cols];
            for (var col = 0; col < cols; col++)
            {
                line[col] = TerminalCell.Blank(CellStyle.Default);
            }

            grid[row] = line;
        }

        return grid;
    }
}

/// <summary>
/// Scrollback storage with O(1) append and drop-oldest: a plain List paid a 10,000-entry
/// memmove for every line scrolled off once the limit was reached.
/// </summary>
internal sealed class ScrollbackRing
{
    private TerminalCell[]?[] _items = new TerminalCell[]?[256];
    private int _head;

    public int Count { get; private set; }

    public TerminalCell[] this[int index] => _items[(_head + index) % _items.Length]!;

    /// <summary>Appends a line; returns true when the oldest line was dropped to stay within the limit.</summary>
    public bool Push(TerminalCell[] line, int limit, out TerminalCell[]? evicted)
    {
        var dropped = false;
        evicted = null;
        if (Count >= limit && Count > 0)
        {
            evicted = _items[_head];
            _items[_head] = null;
            _head = (_head + 1) % _items.Length;
            Count--;
            dropped = true;
        }

        if (Count == _items.Length)
        {
            Grow();
        }

        _items[(_head + Count) % _items.Length] = line;
        Count++;
        return dropped;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        Count = 0;
    }

    private void Grow()
    {
        var bigger = new TerminalCell[]?[_items.Length * 2];
        for (var i = 0; i < Count; i++)
        {
            bigger[i] = _items[(_head + i) % _items.Length];
        }

        _items = bigger;
        _head = 0;
    }
}
