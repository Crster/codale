namespace Codale.Terminal.Tests;

/// <summary>
/// Behavioural tests for the native VT emulator that replaced xterm.js: the things
/// ConPTY output actually exercises - printing, colours, cursor movement, erases,
/// scrolling, the alternate screen, wrap semantics and responses.
/// </summary>
public class TerminalEmulatorTests
{
    private static TerminalEmulator NewEmulator(int cols = 10, int rows = 5)
    {
        var emulator = new TerminalEmulator(cols, rows);
        return emulator;
    }

    private static string LineText(TerminalEmulator emulator, int row)
    {
        var span = emulator.GetScreenRow(row);
        return new string(span.ToArray().Select(c => c.Code).ToArray()).TrimEnd();
    }

    private static TerminalCell Cell(TerminalEmulator emulator, int row, int col) => emulator.GetScreenRow(row)[col];

    [Fact]
    public void Plain_text_lands_at_the_cursor()
    {
        var emulator = NewEmulator();
        emulator.Feed("hello");
        Assert.Equal("hello", LineText(emulator, 0));
        Assert.Equal(5, emulator.CursorCol);
    }

    [Fact]
    public void Newline_and_carriage_return_move_down_and_left()
    {
        var emulator = NewEmulator();
        emulator.Feed("one\r\ntwo");
        Assert.Equal("one", LineText(emulator, 0));
        Assert.Equal("two", LineText(emulator, 1));
    }


    [Fact]
    public void A_full_scrollback_drops_the_oldest_lines_and_keeps_order()
    {
        var emulator = NewEmulator(cols: 10, rows: 3);
        emulator.ScrollbackLimit = 5;

        for (var i = 0; i < 40; i++)
        {
            emulator.Feed($"line{i}\r\n");
        }

        Assert.Equal(5, emulator.ScrollbackCount);
        Assert.True(emulator.TrimmedLines > 0);

        // Oldest surviving line first, contiguous up to the screen.
        var lines = Enumerable.Range(0, emulator.ScrollbackCount)
            .Select(i => new string(emulator.GetLine(i).ToArray().Select(c => c.Code).ToArray()).TrimEnd())
            .ToArray();
        var first = int.Parse(lines[0]["line".Length..]);
        Assert.Equal(Enumerable.Range(first, 5).Select(n => $"line{n}"), lines);
    }
    [Fact]
    public void Scrolling_off_the_bottom_pushes_lines_to_scrollback()
    {
        var emulator = NewEmulator(rows: 3);
        emulator.Feed("\r\n\r\n\r\nlast");
        Assert.Equal(1, emulator.ScrollbackCount);
        Assert.Equal("last", LineText(emulator, 2));
    }

    [Fact]
    public void Deferred_wrap_waits_for_the_next_printable()
    {
        var emulator = NewEmulator(cols: 5);
        emulator.Feed("abcde");
        // xterm semantics: the cursor visually rests on the last column until the
        // next printable arrives.
        Assert.Equal(4, emulator.CursorCol);

        emulator.Feed("x");
        Assert.Equal("abcde", LineText(emulator, 0));
        Assert.Equal("x", LineText(emulator, 1));
    }

    [Fact]
    public void Cursor_positioning_places_text()
    {
        var emulator = NewEmulator();
        emulator.Feed("\x1b[3;4HX");
        Assert.Equal('X', Cell(emulator, 2, 3).Code);
        Assert.Equal(4, emulator.CursorCol);
    }

    [Fact]
    public void Sgr_colours_the_written_cells()
    {
        var emulator = NewEmulator();
        emulator.Feed("\x1b[1;31mR\x1b[0mN");
        var red = Cell(emulator, 0, 0);
        Assert.Equal('R', red.Code);
        Assert.Equal(CellFlags.Bold, red.Style.Flags & CellFlags.Bold);
        Assert.Equal(TerminalColor.Indexed(1), red.Style.Fg);
        Assert.Equal(CellStyle.Default, Cell(emulator, 0, 1).Style);
    }

    [Fact]
    public void Truecolor_is_stored()
    {
        var emulator = NewEmulator();
        emulator.Feed("\x1b[38;2;12;34;56mX");
        Assert.Equal(TerminalColor.FromRgb(12, 34, 56), Cell(emulator, 0, 0).Style.Fg);
    }

    [Fact]
    public void Erase_display_clears_the_right_parts()
    {
        var emulator = NewEmulator();
        emulator.Feed("aaaa\r\nbbbb\r\ncccc\x1b[2;3H\x1b[J");
        Assert.Equal("aaaa", LineText(emulator, 0));
        Assert.Equal("bb", LineText(emulator, 1));
        Assert.Equal("", LineText(emulator, 2));
    }

    [Fact]
    public void Erase_line_keeps_other_rows()
    {
        var emulator = NewEmulator();
        emulator.Feed("keep\r\nwipe\x1b[1G\x1b[2K");
        Assert.Equal("keep", LineText(emulator, 0));
        Assert.Equal("", LineText(emulator, 1));
    }

    [Fact]
    public void Scroll_region_keeps_header_and_footer()
    {
        var emulator = NewEmulator(cols: 10, rows: 4);
        emulator.Feed("\x1b[2;3r"); // margins on rows 1..2
        emulator.Feed("\x1b[3;1Hmiddle\r\nmore");
        // The feed scrolled only inside the region: rows 0 and 3 untouched.
        Assert.Equal("", LineText(emulator, 0));
        Assert.Equal("more", LineText(emulator, 2));
        Assert.Equal(0, emulator.ScrollbackCount);
    }

    [Fact]
    public void Alternate_screen_hides_and_restores_the_main_screen()
    {
        var emulator = NewEmulator();
        emulator.Feed("main");
        emulator.Feed("\x1b[?1049h");
        Assert.True(emulator.InAlternate);
        Assert.Equal("", LineText(emulator, 0));
        emulator.Feed("alt");
        emulator.Feed("\x1b[?1049l");
        Assert.False(emulator.InAlternate);
        Assert.Equal("main", LineText(emulator, 0));
    }

    [Fact]
    public void Dsr_reports_the_cursor_position()
    {
        string? response = null;
        var emulator = NewEmulator();
        emulator.Response += r => response = r;
        emulator.Feed("\x1b[2;3H\x1b[6n");
        Assert.Equal("\x1b[2;3R", response);
    }

    [Fact]
    public void Da_gets_a_response()
    {
        string? response = null;
        var emulator = NewEmulator();
        emulator.Response += r => response = r;
        emulator.Feed("\x1b[c");
        Assert.Equal("\x1b[?1;2c", response);
    }

    [Fact]
    public void Osc_title_is_raised()
    {
        string? title = null;
        var emulator = NewEmulator();
        emulator.TitleChanged += t => title = t;
        emulator.Feed("\x1b]0;window title\x07");
        Assert.Equal("window title", title);
    }

    [Fact]
    public void Unknown_sequences_do_not_leak_text()
    {
        var emulator = NewEmulator();
        // An unknown private mode and an out-of-range truecolour both parse and vanish.
        emulator.Feed("\x1b[?9999h\x1b[38;999;999;999mX");
        Assert.Equal('X', Cell(emulator, 0, 0).Code);
        Assert.Equal(1, emulator.CursorCol);
    }

    [Fact]
    public void Resize_keeps_lines_and_clamps_the_cursor()
    {
        var emulator = NewEmulator(cols: 10, rows: 4);
        emulator.Feed("abcdefghij");
        emulator.Resize(5, 3);
        Assert.Equal(5, emulator.Cols);
        Assert.Equal(3, emulator.Rows);
        Assert.Equal("abcde", LineText(emulator, 0));
        Assert.Equal(4, emulator.CursorCol);
    }

    [Fact]
    public void Tabs_advance_to_the_next_stop()
    {
        var emulator = NewEmulator();
        emulator.Feed("a\tb");
        // b lands on the tab stop at column 8; the cursor has advanced past it.
        Assert.Equal('b', Cell(emulator, 0, 8).Code);
        Assert.Equal(9, emulator.CursorCol);
    }

    [Fact]
    public void Modes_are_reported()
    {
        var emulator = NewEmulator();
        emulator.Feed("\x1b[?1h\x1b[?2004h\x1b[?25l");
        Assert.True(emulator.ApplicationCursorKeys);
        Assert.True(emulator.BracketedPaste);
        Assert.False(emulator.CursorVisible);
    }

    [Fact]
    public void Background_colour_erase_uses_the_current_background()
    {
        var emulator = NewEmulator();
        emulator.Feed("\x1b[41m \x1b[K");
        var erased = Cell(emulator, 0, 1);
        Assert.Equal(TerminalColor.Indexed(1), erased.Style.Bg);
    }

    [Fact]
    public void Shrinking_rows_keeps_the_cursor_line_and_scrolls_the_top_off()
    {
        var emulator = NewEmulator(cols: 10, rows: 4);
        emulator.Feed("a\r\nb\r\nc\r\nd");
        emulator.Resize(10, 2);
        Assert.Equal("c", LineText(emulator, 0));
        Assert.Equal("d", LineText(emulator, 1));
        Assert.Equal(1, emulator.CursorRow);
        Assert.Equal(2, emulator.ScrollbackCount);
        Assert.Equal('a', emulator.GetLine(0)[0].Code);
    }

    [Fact]
    public void Resize_on_the_alternate_screen_also_resizes_the_main_screen()
    {
        var emulator = NewEmulator(cols: 10, rows: 4);
        emulator.Feed("main");
        emulator.Feed("\x1b[?1049h");
        emulator.Resize(20, 6);
        emulator.Feed("\x1b[?1049l");
        Assert.Equal(6, emulator.Rows);
        Assert.Equal(20, emulator.GetScreenRow(5).Length);
        Assert.Equal("main", LineText(emulator, 0));
    }

    [Fact]
    public void Total_lines_on_the_alternate_screen_excludes_scrollback()
    {
        var emulator = NewEmulator(cols: 10, rows: 2);
        emulator.Feed("1\r\n2\r\n3\r\n4");
        Assert.True(emulator.ScrollbackCount > 0);
        emulator.Feed("\x1b[?1049h");
        Assert.Equal(emulator.Rows, emulator.TotalLines);
        for (var i = 0; i < emulator.TotalLines; i++)
        {
            _ = emulator.GetLine(i).Length;
        }
    }

    [Fact]
    public void An_osc_terminated_by_st_does_not_print_the_backslash()
    {
        var emulator = NewEmulator();
        string? title = null;
        emulator.TitleChanged += t => title = t;

        emulator.Feed("\x1b]0;my title\x1b\\after");

        Assert.Equal("my title", title);
        Assert.Equal("after", LineText(emulator, 0));
    }

    [Fact]
    public void An_osc_terminated_by_st_leaves_the_parser_ready_for_the_next_sequence()
    {
        var emulator = NewEmulator();
        emulator.Feed("\x1b]2;t\x1b\\\x1b[3;4Hx");

        Assert.Equal('x', Cell(emulator, 2, 3).Code);
    }

    [Fact]
    public void Endless_parameters_are_capped_and_the_sequence_still_dispatches()
    {
        var emulator = NewEmulator();
        emulator.Feed("\x1b[" + string.Concat(Enumerable.Repeat("1;", 5000)) + "5H");

        // The first parameters still apply (row 1, col 1) and the parser is back in ground state.
        Assert.Equal(0, emulator.CursorRow);
        Assert.Equal(0, emulator.CursorCol);
        emulator.Feed("ok");
        Assert.Equal("ok", LineText(emulator, 0));
    }

    [Fact]
    public void Endless_intermediates_do_not_break_the_parser()
    {
        var emulator = NewEmulator();
        emulator.Feed("\x1b[" + new string(' ', 5000) + "qtext");

        Assert.Equal("text", LineText(emulator, 0));
    }

    [Theory]
    [InlineData(0, TerminalEmulator.CursorShape.BlinkBlock)]
    [InlineData(1, TerminalEmulator.CursorShape.BlinkBlock)]
    [InlineData(2, TerminalEmulator.CursorShape.Block)]
    [InlineData(3, TerminalEmulator.CursorShape.BlinkUnderline)]
    [InlineData(4, TerminalEmulator.CursorShape.Underline)]
    [InlineData(5, TerminalEmulator.CursorShape.BlinkBeam)]
    [InlineData(6, TerminalEmulator.CursorShape.Beam)]
    public void Decscusr_maps_odd_to_blinking_and_even_to_steady(int style, TerminalEmulator.CursorShape expected)
    {
        var emulator = NewEmulator();
        emulator.Feed($"\x1b[{style} q");

        Assert.Equal(expected, emulator.Shape);
    }

    [Fact]
    public void Scrolling_several_lines_at_once_keeps_each_line_in_scrollback()
    {
        var emulator = NewEmulator(cols: 10, rows: 3);
        emulator.Feed("a\r\nb\r\nc");
        emulator.Feed("\x1b[2S");

        Assert.Equal(2, emulator.ScrollbackCount);
        Assert.Equal('a', emulator.GetLine(0)[0].Code);
        Assert.Equal('b', emulator.GetLine(1)[0].Code);
    }

    [Fact]
    public void Recycled_scrollback_rows_come_back_blank()
    {
        var emulator = NewEmulator(cols: 10, rows: 2);
        emulator.ScrollbackLimit = 3;

        for (var i = 0; i < 30; i++)
        {
            emulator.Feed($"row{i}-xxxx\r\n");
        }

        // The bottom screen row was recycled many times and must hold no stale text.
        Assert.Equal(string.Empty, LineText(emulator, 1));
        Assert.Equal("row29-xxxx", LineText(emulator, 0));
    }
}
