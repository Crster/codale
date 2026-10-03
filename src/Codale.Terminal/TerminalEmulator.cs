namespace Codale.Terminal;

/// <summary>
/// A VT/xterm terminal emulator: feeds raw ConPTY output through a VT500-style
/// parser into a <see cref="TerminalBuffer"/>. This is the layer xterm.js used to
/// own - escape sequences, SGR styling, scrolling, the alternate screen - reimplemented
/// natively for Codale.
///
/// The parser is the classic state machine (Ground, Escape, CSI, OSC, DCS, SOS/PM/APC);
/// everything it recognizes is dispatched straight onto the buffer. Sequences it does
/// not recognize are consumed silently, which is the correct failure mode for a
/// terminal: unknown controls must never leak to the screen as text.
/// </summary>
public sealed class TerminalEmulator
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsIgnore,
        SosPmApcString,
    }

    private readonly TerminalBuffer _buffer;

    private State _state = State.Ground;

    // Collectors for the in-flight sequence.
    // Hostile or corrupt streams can send endless ';' / intermediates; real sequences stay far below these.
    private const int MaxParams = 32;
    private const int MaxIntermediates = 4;

    private readonly List<int> _params = [];
    private bool _paramOverflow;
    private bool _paramHasSubparams;
    private bool _privateMarker;
    private readonly List<char> _intermediates = [];
    private readonly char[] _oscBuffer = new char[512];
    private int _oscLength;

    // Modes the renderer and input encoder need to read.
    private bool _cursorVisible = true;
    private bool _applicationCursorKeys;
    private bool _bracketedPaste;

    public enum MouseTracking
    {
        None,
        /// <summary>X10: press and release reports.</summary>
        Press,
        /// <summary>Button-event tracking: press, release and drag while held.</summary>
        Drag,
        /// <summary>Any-event tracking: all motion.</summary>
        Any,
    }

    private MouseTracking _mouseTracking = MouseTracking.None;
    private bool _sgrMouse;

    public enum CursorShape
    {
        BlinkBlock,
        Block,
        BlinkUnderline,
        Underline,
        BlinkBeam,
        Beam,
    }

    private CursorShape _cursorShape = CursorShape.BlinkBlock;

    public TerminalEmulator(int cols = 80, int rows = 25)
    {
        _buffer = new TerminalBuffer(cols, rows);
    }

    // ------------------------------------------------------------------ public surface

    public int Cols => _buffer.Cols;
    public int Rows => _buffer.Rows;
    public int CursorRow => _buffer.CursorRow;
    public int CursorCol => _buffer.CursorCol;
    public bool CursorVisible => _cursorVisible;
    public CursorShape Shape => _cursorShape;
    public bool ApplicationCursorKeys => _applicationCursorKeys;
    public bool BracketedPaste => _bracketedPaste;
    public MouseTracking MouseMode => _mouseTracking;
    public bool SgrMouse => _sgrMouse;
    public bool InAlternate => _buffer.InAlternate;

    /// <summary>How many scrolled-off lines are currently kept.</summary>
    public int ScrollbackCount => _buffer.ScrollbackVisible ? _buffer.ScrollbackCount : 0;

    /// <summary>Lines dropped from a full scrollback so far; see <see cref="TerminalBuffer.TrimmedLines"/>.</summary>
    public long TrimmedLines => _buffer.TrimmedLines;

    /// <summary>Row <paramref name="index"/> of the buffer the renderer draws, 0 = oldest visible line.</summary>
    public ReadOnlySpan<TerminalCell> GetLine(int index) => _buffer.GetLine(index);

    /// <summary>The live screen row, 0 = top of the screen.</summary>
    public TerminalCell[] GetScreenRow(int index) => _buffer.GetScreenRow(index);

    /// <summary>Lines addressable through <see cref="GetLine"/>: the visible scrollback (none on the alt screen) plus the screen.</summary>
    public int TotalLines => ScrollbackCount + _buffer.Rows;

    public int ScrollbackLimit
    {
        get => _buffer.ScrollbackLimit;
        set => _buffer.ScrollbackLimit = value;
    }

    /// <summary>Bumped on every feed; lets the view cache pointer-hover computations against it.</summary>
    public long Revision { get; private set; }

    /// <summary>Raw output from the pseudoconsole. Raises <see cref="Invalidated"/> at the end of the chunk, once.</summary>
    public void Feed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Combine surrogate pairs so an astral character prints as one cell.
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                HandlePrintable('\uFFFD');
                continue;
            }

            Step(c);
        }

        Revision++;
        Invalidated?.Invoke();
    }

    public void Resize(int cols, int rows) => _buffer.Resize(cols, rows);

    /// <summary>Clears the screen and the scrollback, keeping the cursor where it is (the menu's Clear).</summary>
    public void Clear()
    {
        _buffer.EraseRows(0, _buffer.Rows);
        _buffer.ClearScrollback();
    }

    // ------------------------------------------------------------------ events

    /// <summary>The buffer changed; the renderer should repaint.</summary>
    public event Action? Invalidated;

    /// <summary>A reply the emulator must send back to the shell (DSR, DA). Route it to the PTY input.</summary>
    public event Action<string>? Response;

    /// <summary>The application set its title (OSC 0/2).</summary>
    public event Action<string>? TitleChanged;

    /// <summary>The application rang the bell (BEL).</summary>
    public event Action? Bell;

    // ------------------------------------------------------------------ state machine

    private void Step(char c)
    {
        switch (_state)
        {
            case State.Ground:
                StepGround(c);
                break;

            case State.Escape:
                StepEscape(c);
                break;

            case State.EscapeIntermediate:
                if (IsIntermediate(c))
                {
                    break; // extra intermediates ignored
                }

                if (c >= 0x30 && c <= 0x7E)
                {
                    _state = State.Ground;
                }
                else if (c == 0x1B)
                {
                    _state = State.Escape;
                }

                break;

            case State.CsiEntry:
            case State.CsiParam:
            case State.CsiIntermediate:
            case State.CsiIgnore:
                StepCsi(c);
                break;

            case State.OscString:
                StepOsc(c);
                break;

            case State.DcsEntry:
            case State.DcsParam:
            case State.DcsIntermediate:
            case State.DcsPassthrough:
            case State.DcsIgnore:
                StepDcs(c);
                break;

            case State.SosPmApcString:
                // Consume everything until ST.
                if (c == 0x1B)
                {
                    _state = State.Escape;
                }

                break;
        }
    }

    private void StepGround(char c)
    {
        switch (c)
        {
            case '\x1B':
                BeginSequence();
                _state = State.Escape;
                break;
            case '\r':
                _buffer.MoveCursorCol(0);
                break;
            case '\n':
            case '\x0B':
            case '\x0C':
                _buffer.LineFeed();
                break;
            case '\b':
                _buffer.MoveCursorHorizontal(_buffer.CursorCol - 1);
                break;
            case '\t':
                _buffer.TabForward();
                break;
            case '\x07':
                Bell?.Invoke();
                break;
            case '\x00':
            case '\x05': // ENQ ignored
            case '\x0E': // SO
            case '\x0F': // SI
                break;
            default:
                if (c >= 0x20)
                {
                    HandlePrintable(c);
                }

                break;
        }
    }

    private void HandlePrintable(char c)
    {
        _lastPrinted = c;
        _buffer.Put(c, _buffer.CurrentStyle);
    }

    private void BeginSequence()
    {
        _params.Clear();
        _params.Add(0);
        _paramOverflow = false;
        _paramHasSubparams = false;
        _privateMarker = false;
        _intermediates.Clear();
        _oscLength = 0;
    }

    /// <summary>ECMA-48 intermediate byte (SP through /).</summary>
    private static bool IsIntermediate(char c) => c is >= '\x20' and <= '\x2F';

    /// <summary>ECMA-48 final byte (@ through ~) that ends an escape or control sequence.</summary>
    private static bool IsFinal(char c) => c is >= '\x40' and <= '\x7E';

    private void AddIntermediate(char c)
    {
        if (_intermediates.Count < MaxIntermediates)
        {
            _intermediates.Add(c);
        }
    }

    private void StepEscape(char c)
    {
        switch (c)
        {
            case '[':
                _state = State.CsiEntry;
                break;
            case ']':
                _state = State.OscString;
                break;
            case 'P':
            case '\x90':
                _state = State.DcsEntry;
                break;
            case 'X':
            case '^':
            case '_':
            case '\x98':
            case '\x9E':
            case '\x9F':
                _state = State.SosPmApcString;
                break;
            case >= '\x20' and <= '\x2F':
                AddIntermediate(c);
                _state = State.EscapeIntermediate;
                break;
            default:
                _state = State.Ground;
                DispatchEscape(c);
                break;
        }
    }

    private void DispatchEscape(char c)
    {
        switch (c)
        {
            case '7': // DECSC
                _buffer.SaveCursor();
                break;
            case '8': // DECRC
                _buffer.RestoreCursor();
                break;
            case 'D': // IND
                _buffer.LineFeed();
                break;
            case 'M': // RI
                _buffer.ReverseLineFeed();
                break;
            case 'E': // NEL
                _buffer.MoveCursorCol(0);
                _buffer.LineFeed();
                break;
            case 'H': // HTS
                _buffer.SetTabStop();
                break;
            case 'c': // RIS - full reset
                FullReset();
                break;
            case '=': // DECKPAM - keypad mode, ignored (keys are encoded directly)
            case '>': // DECKPNM
            case '\\': // ST terminator already handled
                break;
        }
    }

    private void StepCsi(char c)
    {
        if (c == 0x1B)
        {
            BeginSequence();
            _state = State.Escape;
            return;
        }

        switch (_state)
        {
            case State.CsiEntry:
                if (c == '?')
                {
                    _privateMarker = true;
                    _state = State.CsiParam;
                    return;
                }

                if (IsIntermediate(c))
                {
                    AddIntermediate(c);
                    _state = State.CsiIntermediate;
                    return;
                }

                if (c >= '0' && c <= '9')
                {
                    _params[^1] = c - '0';
                    _state = State.CsiParam;
                    return;
                }

                _state = State.CsiParam;
                StepCsi(c);
                return;

            case State.CsiParam:
                if (c >= '0' && c <= '9')
                {
                    // Past the cap the extra parameters are dropped, digits included.
                    if (!_paramOverflow)
                    {
                        var last = _params[^1];
                        _params[^1] = last <= 9999 ? last * 10 + (c - '0') : last;
                    }

                    return;
                }

                if (c == ';' || c == ':')
                {
                    _paramHasSubparams |= c == ':';
                    if (_params.Count < MaxParams)
                    {
                        _params.Add(0);
                    }
                    else
                    {
                        _paramOverflow = true;
                    }

                    return;
                }

                if (IsIntermediate(c))
                {
                    AddIntermediate(c);
                    _state = State.CsiIntermediate;
                    return;
                }

                if (IsFinal(c))
                {
                    _state = State.Ground;
                    DispatchCsi(c);
                    return;
                }

                _state = State.CsiIgnore;
                return;

            case State.CsiIntermediate:
                if (IsIntermediate(c))
                {
                    AddIntermediate(c);
                    return;
                }

                if (IsFinal(c))
                {
                    _state = State.Ground;
                    DispatchCsi(c);
                    return;
                }

                _state = State.CsiIgnore;
                return;

            case State.CsiIgnore:
                // Swallow until the final byte.
                if (IsFinal(c))
                {
                    _state = State.Ground;
                }

                return;
        }
    }

    private void StepOsc(char c)
    {
        if (c == '\x07')
        {
            EndOsc();
            _state = State.Ground;
        }
        else if (c == '\x1B')
        {
            // ESC \ (ST): finish the string, then let Escape consume the '\' so it is not printed.
            EndOsc();
            BeginSequence();
            _state = State.Escape;
        }
        else if (_oscLength < _oscBuffer.Length)
        {
            _oscBuffer[_oscLength++] = c;
        }
    }

    private void StepDcs(char c)
    {
        switch (_state)
        {
            case State.DcsEntry:
                if (IsIntermediate(c))
                {
                    _state = State.DcsIntermediate;
                }
                else if (c >= '0' && c <= '9' || c == ';' || c == ':' || c == '?' || c == '<' || c == '=' || c == '>')
                {
                    _state = State.DcsParam;
                }
                else if (IsFinal(c))
                {
                    _state = State.DcsIgnore;
                }
                else
                {
                    _state = State.DcsPassthrough;
                }

                break;
            case State.DcsParam:
            case State.DcsIntermediate:
                if (IsFinal(c))
                {
                    _state = State.DcsIgnore;
                }
                else if (c == 0x1B)
                {
                    _state = State.Escape;
                }

                break;
            case State.DcsPassthrough:
            case State.DcsIgnore:
                if (c == 0x1B)
                {
                    _state = State.Escape;
                }

                break;
        }
    }

    /// <summary>OSC payload complete: the first parameter is the code, the rest the text.</summary>
    private void EndOsc()
    {
        var code = 0;
        var index = 0;
        while (index < _oscLength && _oscBuffer[index] >= '0' && _oscBuffer[index] <= '9')
        {
            code = code * 10 + (_oscBuffer[index] - '0');
            index++;
        }

        if (index < _oscLength && _oscBuffer[index] == ';')
        {
            index++;
        }

        switch (code)
        {
            case 0:
            case 2:
                TitleChanged?.Invoke(new string(_oscBuffer, index, _oscLength - index));
                break;
        }

        // Palette/dynamic-colour queries (OSC 4/10/11) go unanswered so the app keeps our theme.
    }

    // ------------------------------------------------------------------ CSI dispatch

    private int Param(int index, int fallback)
    {
        if (index >= _params.Count)
        {
            return fallback;
        }

        var value = _params[index];
        return value == 0 ? fallback : value;
    }

    private void DispatchCsi(char final)
    {
        // DECSCUSR arrives as CSI Ps SP q - an intermediate space before the final byte.
        if (final == 'q' && _intermediates.Contains(' '))
        {
            SetCursorShape(Param(0, 0));
            return;
        }

        if (_privateMarker)
        {
            DispatchPrivateCsi(final);
            return;
        }

        switch (final)
        {
            case '@': // ICH - insert blanks at the cursor; the rest of the line shifts right
                InsertBlanks(Param(0, 1));
                break;
            case 'A': // CUU
                _buffer.MoveCursorVertical(-Param(0, 1));
                break;
            case 'B':
            case 'e': // CUD, VPR
                _buffer.MoveCursorVertical(Param(0, 1));
                break;
            case 'C':
            case 'a': // CUF, HPR
                _buffer.MoveCursorHorizontal(_buffer.CursorCol + Param(0, 1));
                break;
            case 'D': // CUB
                _buffer.MoveCursorHorizontal(_buffer.CursorCol - Param(0, 1));
                break;
            case 'E': // CNL
                _buffer.MoveCursorVertical(Param(0, 1));
                _buffer.MoveCursorCol(0);
                break;
            case 'F': // CPL
                _buffer.MoveCursorVertical(-Param(0, 1));
                _buffer.MoveCursorCol(0);
                break;
            case 'G':
            case '`': // CHA, HPA
                _buffer.MoveCursorCol(Param(0, 1) - 1);
                break;
            case 'H':
            case 'f': // CUP, HVP
                _buffer.MoveCursorTo(Param(0, 1) - 1, Param(1, 1) - 1);
                break;
            case 'I': // CHT - forward tab
                for (var i = 0; i < Param(0, 1); i++)
                {
                    _buffer.TabForward();
                }

                break;
            case 'J': // ED
                DispatchEraseDisplay(Param(0, 0));
                break;
            case 'K': // EL
                DispatchEraseLine(Param(0, 0));
                break;
            case 'L': // IL
                _buffer.InsertLines(_buffer.CursorRow, Param(0, 1));
                _buffer.MoveCursorCol(0);
                break;
            case 'M': // DL
                _buffer.DeleteLines(_buffer.CursorRow, Param(0, 1));
                _buffer.MoveCursorCol(0);
                break;
            case 'P': // DCH
                DeleteCharacters(Param(0, 1));
                break;
            case 'S': // SU
                _buffer.ScrollUp(Param(0, 1));
                break;
            case 'T': // SD
                _buffer.ScrollDown(Param(0, 1));
                break;
            case 'X': // ECH
                EraseCharacters(Param(0, 1));
                break;
            case 'Z': // CBT - backward tab
                for (var i = 0; i < Param(0, 1); i++)
                {
                    _buffer.TabBackward();
                }

                break;
            case 'b': // REP - repeat the last printed character
                for (var i = 0; i < Param(0, 1); i++)
                {
                    _buffer.Put(_lastPrinted, _buffer.CurrentStyle);
                }

                break;
            case 'c': // DA1
                Response?.Invoke("\x1b[?1;2c");
                break;
            case 'd': // VPA
                _buffer.MoveCursorRow(Param(0, 1) - 1);
                break;
            case 'g': // TBC
                if (Param(0, 0) == 3)
                {
                    _buffer.ClearAllTabStops();
                }
                else
                {
                    _buffer.ClearTabStop();
                }

                break;
            case 'm': // SGR
                DispatchSgr();
                break;
            case 'n': // DSR
                DispatchDsr();
                break;
            // SM/RM (CSI h/l) are not handled: only IRM insert mode is even commonly sent, and ConPTY does not use it.
            case 'r': // DECSTBM
                _buffer.SetMargins(Param(0, 1) - 1, Param(1, _buffer.Rows) - 1);
                break;
            case 's': // SCOSC - save cursor
                _buffer.SaveCursor();
                break;
            case 't':
                // Window manipulation (XTWINOPS): only resize feedback matters.
                break;
            case 'u': // SCORC - restore cursor
                _buffer.RestoreCursor();
                break;
        }
    }

    private char _lastPrinted = ' ';

    /// <summary>ICH: shift the tail of the cursor's line right, inserting blanks.</summary>
    private void InsertBlanks(int count)
    {
        var row = _buffer.GetScreenRow(_buffer.CursorRow);
        var col = _buffer.CursorCol;
        var style = _buffer.CurrentStyle;
        count = Math.Min(count, _buffer.Cols - col);

        for (var i = _buffer.Cols - 1; i >= col + count; i--)
        {
            row[i] = row[i - count];
        }

        for (var i = col; i < Math.Min(col + count, _buffer.Cols); i++)
        {
            row[i] = TerminalCell.Blank(style);
        }
    }

    /// <summary>DCH: delete characters at the cursor; the tail shifts left and blanks pad the end.</summary>
    private void DeleteCharacters(int count)
    {
        var row = _buffer.GetScreenRow(_buffer.CursorRow);
        var col = _buffer.CursorCol;
        var style = _buffer.CurrentStyle;
        count = Math.Min(count, _buffer.Cols - col);

        for (var i = col; i + count < _buffer.Cols; i++)
        {
            row[i] = row[i + count];
        }

        for (var i = Math.Max(col, _buffer.Cols - count); i < _buffer.Cols; i++)
        {
            row[i] = TerminalCell.Blank(style);
        }
    }

    /// <summary>ECH: blank characters in place, without shifting.</summary>
    private void EraseCharacters(int count)
    {
        var col = _buffer.CursorCol;
        count = Math.Min(count, _buffer.Cols - col);
        _buffer.EraseInLine(_buffer.CursorRow, col, col + count);
    }

    private void DispatchEraseDisplay(int mode)
    {
        var row = _buffer.CursorRow;
        switch (mode)
        {
            case 0: // cursor to end
                _buffer.EraseInLine(row, _buffer.CursorCol, _buffer.Cols);
                _buffer.EraseRows(row + 1, _buffer.Rows);
                break;
            case 1: // start to cursor
                _buffer.EraseRows(0, row);
                _buffer.EraseInLine(row, 0, _buffer.CursorCol + 1);
                break;
            case 2: // whole screen
            case 3: // whole screen + scrollback
                _buffer.EraseRows(0, _buffer.Rows);
                if (mode == 3)
                {
                    _buffer.ClearScrollback();
                }

                break;
        }
    }

    private void DispatchEraseLine(int mode)
    {
        var row = _buffer.CursorRow;
        switch (mode)
        {
            case 0:
                _buffer.EraseInLine(row, _buffer.CursorCol, _buffer.Cols);
                break;
            case 1:
                _buffer.EraseInLine(row, 0, _buffer.CursorCol + 1);
                break;
            case 2:
                _buffer.EraseInLine(row, 0, _buffer.Cols);
                break;
        }
    }

    private void DispatchDsr()
    {
        switch (Param(0, 0))
        {
            case 5: // status report
                Response?.Invoke("\x1b[0n");
                break;
            case 6: // cursor position
                Response?.Invoke($"\x1b[{_buffer.CursorRow + 1};{_buffer.CursorCol + 1}R");
                break;
        }
    }

    // ------------------------------------------------------------------ SGR

    private void DispatchSgr()
    {
        var style = _buffer.CurrentStyle;
        var fg = style.Fg;
        var bg = style.Bg;
        var flags = style.Flags;

        // An SGR with no parameters is a full reset.
        if (_params.Count == 1 && _params[0] == 0 && !_paramHasSubparams && _intermediates.Count == 0)
        {
            _buffer.SetStyle(CellStyle.Default);
            return;
        }

        for (var i = 0; i < _params.Count; i++)
        {
            switch (_params[i])
            {
                case 0:
                    fg = TerminalColor.Default;
                    bg = TerminalColor.Default;
                    flags = CellFlags.None;
                    break;
                case 1: flags |= CellFlags.Bold; break;
                case 2: flags |= CellFlags.Faint; break;
                case 3: flags |= CellFlags.Italic; break;
                case 4: flags |= CellFlags.Underline; break;
                case 5:
                case 6: flags |= CellFlags.Blink; break;
                case 7: flags |= CellFlags.Reverse; break;
                case 8: flags |= CellFlags.Hidden; break;
                case 9: flags |= CellFlags.Strikeout; break;
                case 21: flags &= ~CellFlags.Bold; break;
                case 22: flags &= ~(CellFlags.Bold | CellFlags.Faint); break;
                case 23: flags &= ~CellFlags.Italic; break;
                case 24: flags &= ~CellFlags.Underline; break;
                case 25: flags &= ~CellFlags.Blink; break;
                case 27: flags &= ~CellFlags.Reverse; break;
                case 28: flags &= ~CellFlags.Hidden; break;
                case 29: flags &= ~CellFlags.Strikeout; break;
                case 39: fg = TerminalColor.Default; break;
                case 49: bg = TerminalColor.Default; break;
                case >= 30 and <= 37: fg = TerminalColor.Indexed((byte)(_params[i] - 30)); break;
                case >= 40 and <= 47: bg = TerminalColor.Indexed((byte)(_params[i] - 40)); break;
                case >= 90 and <= 97: fg = TerminalColor.Indexed((byte)(_params[i] - 90 + 8)); break;
                case >= 100 and <= 107: bg = TerminalColor.Indexed((byte)(_params[i] - 100 + 8)); break;
                case 38:
                case 48:
                    var isFg = _params[i] == 38;
                    var colour = ParseColour(ref i);
                    if (colour is { } parsed)
                    {
                        if (isFg)
                        {
                            fg = parsed;
                        }
                        else
                        {
                            bg = parsed;
                        }
                    }

                    break;
            }
        }

        _buffer.SetStyle(new CellStyle(fg, bg, flags));
    }

    /// <summary>
    /// Parses an extended colour (38/48) payload starting just after the code and
    /// advances <paramref name="i"/> past everything it consumed. Handles both the
    /// 256-colour (5;index) and true-colour (2;r;g;b) forms, including the
    /// colon-separated variant some apps emit.
    /// </summary>
    private TerminalColor? ParseColour(ref int i)
    {
        var mode = Param(i + 1, 0);
        if (mode == 5)
        {
            var index = Param(i + 2, 0);
            i += 2;
            return TerminalColor.Indexed((byte)Math.Min(index, 255));
        }

        if (mode == 2)
        {
            // Either 2;r;g;b or 2:colour-space:r:g:b;... - find three colour values
            // after the mode, skipping an optional colourspace id.
            Span<int> values = stackalloc int[3];
            var found = 0;
            var j = i + 2;
            // A colour space id larger than 255 means the colon form with 5 components.
            var skip = Param(i + 2, 0) > 255 ? 1 : 0;
            j += skip;
            for (; j < _params.Count && found < 3; j++)
            {
                values[found++] = Math.Min(Param(j, 0), 255);
            }

            if (found == 3)
            {
                i = j - 1;
                return TerminalColor.FromRgb((byte)values[0], (byte)values[1], (byte)values[2]);
            }

            i = _params.Count;
            return null;
        }

        i = _params.Count;
        return null;
    }

    // ------------------------------------------------------------------ private modes

    private void DispatchPrivateCsi(char final)
    {
        switch (final)
        {
            case 'h':
            case 'l':
                SetPrivateModes(final == 'h');
                break;
            case 'n':
                // DECRQM ignored.
                break;
        }
    }

    private void SetPrivateModes(bool on)
    {
        for (var i = 0; i < _params.Count; i++)
        {
            switch (_params[i])
            {
                case 1: // DECCKM
                    _applicationCursorKeys = on;
                    break;
                case 6: // DECOM
                    _buffer.SetOriginMode(on);
                    break;
                case 7: // DECAWM
                    _buffer.SetAutowrap(on);
                    break;
                case 25: // DECTCEM
                    _cursorVisible = on;
                    break;
                case 47:
                    _buffer.SetAlternateScreen(on);
                    break;
                case 1047:
                    if (on)
                    {
                        _buffer.SetAlternateScreen(true);
                    }
                    else
                    {
                        _buffer.EraseRows(0, _buffer.Rows);
                        _buffer.SetAlternateScreen(false);
                    }

                    break;
                case 1048:
                    if (on)
                    {
                        _buffer.SaveCursor();
                    }
                    else
                    {
                        _buffer.RestoreCursor();
                    }

                    break;
                case 1049:
                    if (on)
                    {
                        _buffer.SaveCursor();
                        _buffer.SetAlternateScreen(true);
                        _buffer.EraseRows(0, _buffer.Rows);
                    }
                    else
                    {
                        _buffer.SetAlternateScreen(false);
                        _buffer.RestoreCursor();
                    }

                    break;
                case 1000:
                    _mouseTracking = on ? MouseTracking.Press : MouseTracking.None;
                    break;
                case 1002:
                    _mouseTracking = on ? MouseTracking.Drag : MouseTracking.None;
                    break;
                case 1003:
                    _mouseTracking = on ? MouseTracking.Any : MouseTracking.None;
                    break;
                case 1006:
                    _sgrMouse = on;
                    break;
                case 2004:
                    _bracketedPaste = on;
                    break;
                case 12:
                    // Cursor blink on/off - we keep our own blink.
                    break;
            }
        }
    }

    /// <summary>Full reset (ESC c): back to the power-on state, scrollback kept.</summary>
    private void FullReset()
    {
        _cursorVisible = true;
        _applicationCursorKeys = false;
        _bracketedPaste = false;

        _mouseTracking = MouseTracking.None;
        _sgrMouse = false;
        _cursorShape = CursorShape.BlinkBlock;
        _buffer.Reset();
    }

    /// <summary>The cursor shape the app asked for (DECSCUSR, CSI Ps SP q).</summary>
    public void SetCursorShape(int style)
    {
        _cursorShape = style switch
        {
            0 or 1 => CursorShape.BlinkBlock,
            2 => CursorShape.Block,
            3 => CursorShape.BlinkUnderline,
            4 => CursorShape.Underline,
            5 => CursorShape.BlinkBeam,
            6 => CursorShape.Beam,
            _ => CursorShape.BlinkBlock,
        };
    }
}
