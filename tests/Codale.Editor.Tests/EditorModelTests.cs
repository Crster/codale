using Codale.Core.Syntax;
using Codale.Editor;

namespace Codale.Editor.Tests;

public class TextBufferTests
{
    [Fact]
    public void Load_splits_every_line_ending_kind()
    {
        var buffer = new TextBuffer();
        buffer.Load("a\r\nb\nc\rd");
        Assert.Equal(["a", "b", "c", "d"], buffer.Lines);
        Assert.Equal("a\r\nb\r\nc\r\nd", buffer.GetText());
        Assert.Equal(4, buffer.CharCount);
    }

    [Fact]
    public void Replace_reports_the_change_and_keeps_untouched_lines()
    {
        var buffer = new TextBuffer();
        buffer.Load("one\ntwo\nthree");
        var untouched = buffer[2];
        TextChange? seen = null;
        buffer.Changed += c => seen = c;

        var end = buffer.Replace(new(0, 1), new(1, 1), "X\nY");

        Assert.Equal(["oX", "Ywo", "three"], buffer.Lines);
        Assert.Equal(new DocumentPosition(1, 1), end);
        Assert.Same(untouched, buffer[2]);
        Assert.Equal(new TextChange(new(0, 1), new(1, 1), new(1, 1)), seen);
        Assert.Equal("oX\r\nYwo\r\nthree".Replace("\r\n", "").Length, buffer.CharCount);
    }

    [Fact]
    public void GetText_is_cached_until_an_edit()
    {
        var buffer = new TextBuffer();
        buffer.Load("a\nb");
        var first = buffer.GetText();
        Assert.Same(first, buffer.GetText());
        buffer.Replace(new(0, 1), new(0, 1), "z");
        Assert.Equal("az\r\nb", buffer.GetText());
    }
}

public class TextUnitsTests
{
    [Theory]
    [InlineData("a👍b", 1, 3)]
    [InlineData("👨‍👩‍👧x", 0, 8)]
    [InlineData("éx", 0, 2)]
    [InlineData("abc", 1, 2)]
    public void NextBoundary_steps_over_whole_clusters(string line, int col, int expected) =>
        Assert.Equal(expected, TextUnits.NextBoundary(line, col));

    [Theory]
    [InlineData("a👍b", 3, 1)]
    [InlineData("👨‍👩‍👧x", 8, 0)]
    [InlineData("éx", 2, 0)]
    [InlineData("abc", 2, 1)]
    public void PreviousBoundary_steps_back_over_whole_clusters(string line, int col, int expected) =>
        Assert.Equal(expected, TextUnits.PreviousBoundary(line, col));

    [Fact]
    public void Snap_never_lands_inside_a_surrogate_pair() =>
        Assert.Equal(1, TextUnits.Snap("a👍b", 2));

    [Fact]
    public void Wrap_breaks_between_clusters_by_cells()
    {
        var starts = new List<int>();
        var rows = TextUnits.Wrap("ab😀cd", 3, 4, starts);
        Assert.Equal(3, rows);
        Assert.Equal([0, 2, 5], starts);
        Assert.Equal(3, TextUnits.Wrap(new string('x', 25), 10, 4, null));
    }
}

public class EditorModelTests
{
    private static EditorModel Model(string text)
    {
        var model = new EditorModel { Clock = () => 0 };
        model.Load(text);
        return model;
    }

    private static string Text(EditorModel model) => string.Join("\n", model.Buffer.Lines);

    [Fact]
    public void Typing_at_several_carets_edits_each_once()
    {
        var model = Model("ab\nab\nab");
        model.SetSelections([new(new(0, 1)), new(new(1, 1)), new(new(2, 1))]);
        model.Type("X");
        Assert.Equal("aXb\naXb\naXb", Text(model));
        Assert.Equal([new(0, 2), new(1, 2), new(2, 2)], model.Selections.Select(s => s.Caret));
    }

    [Fact]
    public void Several_carets_on_one_line_shift_by_the_edits_before_them()
    {
        var model = Model("a b c");
        model.SetSelections([new(new(0, 1)), new(new(0, 3)), new(new(0, 5))]);
        model.Type("--");
        Assert.Equal("a-- b-- c--", Text(model));
        Assert.Equal([new(0, 3), new(0, 7), new(0, 11)], model.Selections.Select(s => s.Caret));
    }

    [Fact]
    public void Newline_at_several_carets_shifts_later_lines()
    {
        var model = Model("ab\ncd");
        model.SetSelections([new(new(0, 1)), new(new(1, 1))]);
        model.InsertNewLine();
        Assert.Equal("a\nb\nc\nd", Text(model));
        Assert.Equal([new(1, 0), new(3, 0)], model.Selections.Select(s => s.Caret));
    }

    [Fact]
    public void Newline_between_braces_opens_an_indented_block()
    {
        var model = Model("  f() {}");
        model.SetCaret(new(0, 7));
        model.InsertNewLine();
        Assert.Equal("  f() {\n      \n  }", Text(model));
        Assert.Equal(new DocumentPosition(1, 6), model.Primary.Caret);
    }

    [Fact]
    public void Backspace_removes_a_whole_emoji_and_an_indent_level()
    {
        var model = Model("x👍\n        y");
        model.SetSelections([new(new(0, 3)), new(new(1, 8))]);
        model.Backspace();
        Assert.Equal("x\n    y", Text(model));
    }

    [Fact]
    public void Undo_and_redo_restore_text_and_carets()
    {
        var model = Model("abc");
        model.SetSelections([new(new(0, 0)), new(new(0, 3))]);
        model.Type("!");
        model.Type("?");
        Assert.Equal("!?abc!?", Text(model));

        model.Undo();
        Assert.Equal("abc", Text(model));
        Assert.Equal([new(0, 0), new(0, 3)], model.Selections.Select(s => s.Caret));

        model.Redo();
        Assert.Equal("!?abc!?", Text(model));
        Assert.Equal([new(0, 2), new(0, 7)], model.Selections.Select(s => s.Caret));
    }

    [Fact]
    public void Typing_coalesces_but_a_caret_move_seals_the_step()
    {
        var model = Model("");
        model.Type("a");
        model.Type("b");
        model.MoveHorizontal(back: true, word: false, extend: false);
        model.Type("c");
        model.Undo();
        Assert.Equal("ab", Text(model));
        model.Undo();
        Assert.Equal("", Text(model));
    }

    [Fact]
    public void MoveLines_moves_the_block_and_its_selection()
    {
        var model = Model("1\n2\n3\n4");
        model.SetSelection(new Selection(new(1, 0), new(2, 1)));
        Assert.True(model.MoveLines(up: false));
        Assert.Equal("1\n4\n2\n3", Text(model));
        Assert.Equal(new Selection(new(2, 0), new(3, 1)), model.Primary with { PreferredX = double.NaN });

        model.MoveLines(up: true);
        model.MoveLines(up: true);
        Assert.Equal("2\n3\n1\n4", Text(model));
        Assert.False(model.MoveLines(up: true));
    }

    [Fact]
    public void MoveLines_moves_separate_blocks_independently()
    {
        var model = Model("a\nb\nc\nd\ne");
        model.SetSelections([new(new(1, 0)), new(new(3, 0))]);
        model.MoveLines(up: true);
        Assert.Equal("b\na\nd\nc\ne", Text(model));
        Assert.Equal([0, 2], model.Selections.Select(s => s.Caret.Line));
        model.Undo();
        Assert.Equal("a\nb\nc\nd\ne", Text(model));
    }

    [Fact]
    public void DuplicateLines_copies_down_and_follows_the_copy()
    {
        var model = Model("a\nb\nc");
        model.SetSelections([new(new(0, 1)), new(new(2, 0))]);
        model.DuplicateLines(down: true);
        Assert.Equal("a\na\nb\nc\nc", Text(model));
        Assert.Equal([new(1, 1), new(4, 0)], model.Selections.Select(s => s.Caret));

        model.DuplicateLines(down: false);
        Assert.Equal("a\na\na\nb\nc\nc\nc", Text(model));
        Assert.Equal([new(1, 1), new(5, 0)], model.Selections.Select(s => s.Caret));
    }

    [Fact]
    public void DeleteLines_removes_the_line_and_keeps_the_column()
    {
        var model = Model("abc\ndef\nghi");
        model.SetCaret(new(1, 2));
        model.DeleteLines();
        Assert.Equal("abc\nghi", Text(model));
        Assert.Equal(new DocumentPosition(1, 2), model.Primary.Caret);

        model.SetCaret(new(1, 1));
        model.DeleteLines();
        Assert.Equal("abc", Text(model));
    }

    [Fact]
    public void AddNextOccurrence_selects_word_then_following_copies()
    {
        var model = Model("foo bar foo\nfoobar foo");
        model.SetCaret(new(0, 1));
        model.AddNextOccurrence();
        Assert.Equal(new Selection(new(0, 0), new(0, 3)), model.Primary);
        model.AddNextOccurrence();
        model.AddNextOccurrence();
        Assert.Equal(3, model.Selections.Count);
        Assert.Equal(new DocumentPosition(1, 7), model.Primary.Start);
        model.Type("x");
        Assert.Equal("x bar x\nfoobar x", Text(model));
    }

    [Fact]
    public void Paste_distributes_lines_across_carets()
    {
        var model = Model("a\nb");
        model.SetSelections([new(new(0, 1)), new(new(1, 1))]);
        model.Paste("1\r\n2");
        Assert.Equal("a1\nb2", Text(model));
    }

    [Fact]
    public void Indent_and_outdent_shift_selected_lines()
    {
        var model = Model("a\nb");
        model.SetSelection(new Selection(new(0, 0), new(1, 1)));
        model.Tab();
        Assert.Equal("    a\n    b", Text(model));
        model.OutdentLines();
        Assert.Equal("a\nb", Text(model));
    }

    [Fact]
    public void Overlapping_selections_merge()
    {
        var model = Model("abcdef");
        model.SetSelections([new Selection(new(0, 0), new(0, 3)), new Selection(new(0, 2), new(0, 5)), new(new(0, 5))]);
        Assert.Single(model.Selections);
        Assert.Equal(new Selection(new(0, 0), new(0, 5)), model.Primary);
    }

    [Fact]
    public void Markers_follow_inserted_and_removed_lines()
    {
        var model = Model("a\nb\nc\nd");
        var markers = new LineMarkers(model.Buffer);
        markers.Toggle(2);
        model.SetCaret(new(0, 0));
        model.Type("x\ny\n");
        Assert.Equal([4], markers.Lines);
        model.SetSelection(new Selection(new(0, 0), new(3, 0)));
        model.Backspace();
        Assert.Equal([1], markers.Lines);
        Assert.Equal(1, markers.Next(0));
        Assert.Equal(1, markers.Previous(0));
    }
}

public class SyntaxHighlighterTests
{
    // A toy grammar: '#' to end of line is a comment; "/*" opens a block comment that "*/" closes.
    private static int s_calls;

    private static bool Fake(string language, string line, object? stateIn, List<CodeToken> tokens, out object? stateOut)
    {
        s_calls++;
        var inBlock = stateIn is true;
        if (inBlock)
        {
            var close = line.IndexOf("*/", StringComparison.Ordinal);
            tokens.Add(new CodeToken(0, close < 0 ? line.Length : close + 2, CodeTokenKind.Comment));
            stateOut = close < 0;
            return true;
        }

        var open = line.IndexOf("/*", StringComparison.Ordinal);
        if (open >= 0)
        {
            tokens.Add(new CodeToken(open, line.Length - open, CodeTokenKind.Comment));
            stateOut = !line.AsSpan(open).Contains("*/", StringComparison.Ordinal);
            return true;
        }

        var hash = line.IndexOf('#');
        if (hash >= 0)
        {
            tokens.Add(new CodeToken(hash, line.Length - hash, CodeTokenKind.Comment));
        }

        stateOut = false;
        return true;
    }

    [Fact]
    public void Edits_relex_only_what_changed()
    {
        var model = new EditorModel();
        model.Load(string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"line {i} # c")));
        var highlighter = new SyntaxHighlighter(model.Buffer, Fake) { LanguageId = "x" };
        while (highlighter.Advance(999, TimeSpan.FromSeconds(1)))
        {
        }

        Assert.Equal(1000, highlighter.ValidUpTo);
        s_calls = 0;
        model.SetCaret(new(500, 0));
        model.Type("z");
        while (highlighter.Advance(999, TimeSpan.FromSeconds(1)))
        {
        }

        Assert.True(s_calls <= 2, $"re-lexed {s_calls} lines");
    }

    [Fact]
    public void Opening_a_block_comment_recolours_following_lines()
    {
        var model = new EditorModel();
        model.Load("a\nb\nc");
        var highlighter = new SyntaxHighlighter(model.Buffer, Fake) { LanguageId = "x" };
        highlighter.Advance(2, TimeSpan.FromSeconds(1));
        Assert.Empty(highlighter.Get(2).Tokens);

        model.SetCaret(new(0, 0));
        model.Type("/*");
        highlighter.Advance(2, TimeSpan.FromSeconds(1));
        Assert.Equal(CodeTokenKind.Comment, highlighter.Get(2).KindAt(0));
    }

    [Fact]
    public void Far_lines_get_provisional_colour_without_the_whole_file()
    {
        var model = new EditorModel();
        model.Load(string.Join("\n", Enumerable.Range(0, 100_000).Select(i => "x # c")));
        var highlighter = new SyntaxHighlighter(model.Buffer, Fake) { LanguageId = "x" };
        s_calls = 0;
        var tokens = highlighter.Get(99_990);
        Assert.True(tokens.Provisional);
        Assert.Equal(CodeTokenKind.Comment, tokens.KindAt(3));
        Assert.True(s_calls < 100);
    }
}
