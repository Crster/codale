using Codale.Core.Markdown;

namespace Codale.Core.Tests;

public sealed class MarkdownParserTests
{
    [Fact]
    public void A_plan_reads_as_headings_steps_and_prose()
    {
        var blocks = MarkdownParser.Parse("""
            # Snake fix

            The board wraps now.

            ## Steps

            1. **Read** the loop
            2. Wrap the index
            """);

        Assert.Equal(5, blocks.Count);
        Assert.True(blocks[0] is MarkdownBlock.Heading { Level: 1, Content: [MarkdownInline.Text { Value: "Snake fix" }] });
        Assert.True(blocks[1] is MarkdownBlock.Paragraph);
        Assert.True(blocks[2] is MarkdownBlock.Heading { Level: 2 });
        Assert.True(blocks[3] is MarkdownBlock.ListItem { Depth: 0, Number: 1 });
        Assert.True(blocks[4] is MarkdownBlock.ListItem { Depth: 0, Number: 2 });
    }

    [Fact]
    public void A_pipe_table_reads_as_a_table_not_a_folded_paragraph()
    {
        var blocks = MarkdownParser.Parse("""
            ## Summary
            | Piece | What it does |
            |---|:---:|
            | `fartSound` | Plays a clip |
            | `a|b` | escaped \| pipe |
            The cloud appears at the tail.
            """);

        Assert.Equal(3, blocks.Count);
        var table = Assert.IsType<MarkdownBlock.Table>(blocks[1]);
        Assert.Equal([TableAlignment.None, TableAlignment.Center], table.Alignments);
        Assert.True(table.Header[0] is [MarkdownInline.Text { Value: "Piece" }]);
        Assert.Equal(2, table.Rows.Count);
        Assert.True(table.Rows[0][0] is [MarkdownInline.CodeSpan { Value: "fartSound" }]);
        Assert.True(table.Rows[1][0] is [MarkdownInline.CodeSpan { Value: "a|b" }]);
        Assert.True(table.Rows[1][1] is [MarkdownInline.Text { Value: "escaped | pipe" }]);
        Assert.True(blocks[2] is MarkdownBlock.Paragraph);
    }

    [Fact]
    public void A_pipe_without_a_delimiter_row_stays_prose()
    {
        var blocks = MarkdownParser.Parse("a | b\nc | d");

        Assert.True(blocks is [MarkdownBlock.Paragraph]);
    }

    [Fact]
    public void Steps_carry_their_literal_ordinal_and_nested_depth()
    {
        var blocks = MarkdownParser.Parse("""
            1. first
            2. second
               - nested
            10. tenth
            """);

        Assert.Equal(4, blocks.Count);
        Assert.True(blocks[0] is MarkdownBlock.ListItem { Number: 1, Depth: 0 });
        Assert.True(blocks[1] is MarkdownBlock.ListItem { Number: 2, Depth: 0 });
        Assert.True(blocks[2] is MarkdownBlock.ListItem { Number: 0, Depth: 1 });
        Assert.True(blocks[3] is MarkdownBlock.ListItem { Number: 10, Depth: 0 });
    }

    [Fact]
    public void A_list_continues_past_a_blank_line_when_more_rows_follow()
    {
        var blocks = MarkdownParser.Parse("""
            - one

            - two

            Done.
            """);

        Assert.Equal(3, blocks.Count);
        Assert.True(blocks[0] is MarkdownBlock.ListItem);
        Assert.True(blocks[1] is MarkdownBlock.ListItem);
        Assert.True(blocks[2] is MarkdownBlock.Paragraph);
    }

    [Fact]
    public void List_continuation_lines_join_their_row()
    {
        var blocks = MarkdownParser.Parse("""
            - one
              wrapped
            - two
            """);

        Assert.Equal(2, blocks.Count);
        Assert.True(blocks[0] is MarkdownBlock.ListItem
        {
            Content: [MarkdownInline.Text { Value: "one wrapped" }],
        });
    }

    [Fact]
    public void Bold_italic_and_code_spans_nest_as_runs()
    {
        var blocks = MarkdownParser.Parse("a **bold *italic* end** and `code span` text");

        var paragraph = Assert.IsType<MarkdownBlock.Paragraph>(blocks[0]);

        Assert.Equal(5, paragraph.Content.Count);
        Assert.True(paragraph.Content[1] is MarkdownInline.Emphasis
        {
            Strong: true,
            Content:
            [
                MarkdownInline.Text { Value: "bold " },
                MarkdownInline.Emphasis { Strong: false, Content: [MarkdownInline.Text { Value: "italic" }] },
                MarkdownInline.Text { Value: " end" },
            ],
        });
        Assert.True(paragraph.Content[3] is MarkdownInline.CodeSpan { Value: "code span" });
    }

    [Fact]
    public void Underscores_inside_words_stay_literal()
    {
        // __init__ bolds its core the way CommonMark does, so the intraword case that
        // must survive is the single underscore: file_name.txt, snake_case names.
        var blocks = MarkdownParser.Parse("read file_name.txt and my_var never");

        var paragraph = Assert.IsType<MarkdownBlock.Paragraph>(blocks[0]);

        Assert.Equal(
            [new MarkdownInline.Text("read file_name.txt and my_var never")],
            paragraph.Content);
    }

    [Fact]
    public void A_hash_without_a_space_is_not_a_heading()
    {
        var blocks = MarkdownParser.Parse("#hashtag stays prose");

        var paragraph = Assert.IsType<MarkdownBlock.Paragraph>(blocks[0]);

        Assert.Equal([new MarkdownInline.Text("#hashtag stays prose")], paragraph.Content);
    }

    [Fact]
    public void A_trailing_hash_sequence_is_stripped_from_headings()
    {
        var blocks = MarkdownParser.Parse("## Fix the bug ##");

        var heading = Assert.IsType<MarkdownBlock.Heading>(blocks[0]);

        Assert.Equal([new MarkdownInline.Text("Fix the bug")], heading.Content);
    }

    [Fact]
    public void Fenced_code_keeps_lines_verbatim_and_names_its_language()
    {
        var blocks = MarkdownParser.Parse("""
            ```shell
            dotnet build
              indented - stays
            ```

            after
            """);

        Assert.Equal(2, blocks.Count);
        var code = Assert.IsType<MarkdownBlock.CodeBlock>(blocks[0]);
        Assert.Equal("shell", code.Language);
        Assert.Equal("dotnet build\n  indented - stays", code.Text);
        Assert.True(blocks[1] is MarkdownBlock.Paragraph);
    }

    [Fact]
    public void An_unterminated_fence_stays_monospace_to_the_end()
    {
        var blocks = MarkdownParser.Parse("```\nline one\nline two");

        var code = Assert.IsType<MarkdownBlock.CodeBlock>(blocks.Single());

        Assert.Equal("line one\nline two", code.Text);
    }

    [Fact]
    public void A_quote_reparses_its_inside()
    {
        var blocks = MarkdownParser.Parse("> quoted **bold** text\n> more");

        var quote = Assert.IsType<MarkdownBlock.Quote>(blocks.Single());
        var paragraph = Assert.IsType<MarkdownBlock.Paragraph>(quote.Content.Single());

        Assert.Contains(paragraph.Content, inline => inline is MarkdownInline.Emphasis { Strong: true });
    }

    [Fact]
    public void Rules_break_paragraphs_without_eating_the_neighbours()
    {
        var blocks = MarkdownParser.Parse("above\n---\n\nbelow");

        Assert.Equal(3, blocks.Count);
        Assert.True(blocks[0] is MarkdownBlock.Paragraph);
        Assert.True(blocks[1] is MarkdownBlock.ThematicBreak);
        Assert.True(blocks[2] is MarkdownBlock.Paragraph);
    }

    [Fact]
    public void Fences_quotes_and_lists_interrupt_a_paragraph_and_the_text_after_a_closed_fence_resumes()
    {
        var blocks = MarkdownParser.Parse("intro\n```\ncode\n```\nmiddle\n> said\n- item\nafter");

        Assert.Collection(
            blocks,
            b => Assert.IsType<MarkdownBlock.Paragraph>(b),
            b => Assert.Equal("code", Assert.IsType<MarkdownBlock.CodeBlock>(b).Text),
            b => Assert.IsType<MarkdownBlock.Paragraph>(b),
            b => Assert.IsType<MarkdownBlock.Quote>(b),
            b => Assert.IsType<MarkdownBlock.ListItem>(b),
            b => Assert.IsType<MarkdownBlock.Paragraph>(b));
    }

    [Fact]
    public void Soft_breaks_fold_to_spaces_within_a_paragraph()
    {
        var blocks = MarkdownParser.Parse("first line\nsecond line");

        var paragraph = Assert.IsType<MarkdownBlock.Paragraph>(blocks.Single());

        Assert.Equal([new MarkdownInline.Text("first line second line")], paragraph.Content);
    }

    [Fact]
    public void Links_render_and_images_become_their_alt_text()
    {
        var blocks = MarkdownParser.Parse("see [the docs](https://example.com/a?b=c) and ![a pic](https://example.com/x.png)");

        var paragraph = Assert.IsType<MarkdownBlock.Paragraph>(blocks[0]);

        Assert.True(paragraph.Content[1] is MarkdownInline.Link { Url: "https://example.com/a?b=c" });
        Assert.True(paragraph.Content[3] is MarkdownInline.Link { Url: "https://example.com/x.png" });
    }

    [Fact]
    public void Entities_and_backslash_escapes_decode()
    {
        var blocks = MarkdownParser.Parse("a &amp; b \\*not italic\\* &#65;");

        var paragraph = Assert.IsType<MarkdownBlock.Paragraph>(blocks[0]);

        Assert.Equal(
            [new MarkdownInline.Text("a & b *not italic* A")],
            paragraph.Content);
    }

    [Fact]
    public void Deeply_nested_quotes_cannot_overflow_the_stack()
    {
        // 100k nested levels would recurse a stack overflow - which cannot be caught - if unbounded.
        var blocks = MarkdownParser.Parse(new string('>', 100_000) + " deep");

        var depth = 0;
        IReadOnlyList<MarkdownBlock> level = blocks;
        while (level.Count == 1 && level[0] is MarkdownBlock.Quote quote)
        {
            depth++;
            level = quote.Content;
        }

        Assert.Equal(MarkdownParser.MaxQuoteDepth, depth);
        Assert.NotEmpty(level);
    }

    [Theory]
    [InlineData("&#0;", "\uFFFD")]
    [InlineData("&#xD800;", "\uFFFD")]
    [InlineData("&#1114112;", "\uFFFD")]
    [InlineData("&#x110000;", "\uFFFD")]
    [InlineData("&#128512;", "\U0001F600")]
    [InlineData("&#x1F600;", "\U0001F600")]
    [InlineData("&#x41;", "A")]
    public void Numeric_entities_decode_to_valid_text_only(string markup, string expected)
    {
        var blocks = MarkdownParser.Parse("a" + markup + "b");

        var paragraph = Assert.IsType<MarkdownBlock.Paragraph>(blocks[0]);
        Assert.Equal([new MarkdownInline.Text("a" + expected + "b")], paragraph.Content);
    }

    [Fact]
    public void Windows_line_endings_parse_the_same()
    {
        var blocks = MarkdownParser.Parse("# Title\r\n\r\nBody text\r\n- step");

        Assert.Equal(3, blocks.Count);
        Assert.True(blocks[0] is MarkdownBlock.Heading);
        Assert.True(blocks[1] is MarkdownBlock.Paragraph);
        Assert.True(blocks[2] is MarkdownBlock.ListItem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Empty_input_yields_no_blocks(string? markdown)
    {
        Assert.Empty(MarkdownParser.Parse(markdown));
    }

    [Fact]
    public void Unbalanced_markers_degrade_to_literal_text()
    {
        var blocks = MarkdownParser.Parse("a ** b ` c ~~ d [e](");

        var paragraph = Assert.IsType<MarkdownBlock.Paragraph>(blocks.Single());

        var rendered = string.Concat(paragraph.Content.Select(i => i switch
        {
            MarkdownInline.Text t => t.Value,
            MarkdownInline.CodeSpan c => c.Value,
            _ => "?",
        }));
        Assert.Contains("a ** b", rendered);
        Assert.Contains("c", rendered);
        Assert.Contains("d [e](", rendered);
    }
}

