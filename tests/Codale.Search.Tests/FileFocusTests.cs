using Codale.Core.Helper;

namespace Codale.Search.Tests;

public sealed class FileFocusTests
{
    private sealed class OneCallModel(string? rangesJson, string? bestJson = null) : ISearchModel
    {
        public string? Prompt { get; private set; }

        public string? SystemPrompt { get; private set; }

        public Task<ToolCall?> NextCallAsync(
            string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            (SystemPrompt, Prompt) = (systemPrompt, conversation);
            if (rangesJson is null && bestJson is null)
            {
                return Task.FromResult<ToolCall?>(null);
            }

            return Task.FromResult(bestJson is not null
                ? ToolCall.FromJson($$$"""{"tool":"highlight","arguments":{"best":"{{{bestJson}}}"}}""")
                : ToolCall.FromJson($$$"""{"tool":"highlight","arguments":{"ranges":"{{{rangesJson}}}"}}"""));
        }
    }

    private static readonly string[] Game =
    [
        "const canvas = document.getElementById('c');",
        "let velocityY = 0;",
        "",
        "function jump() {",
        "    if (onGround) {",
        "        velocityY = -12;",
        "    }",
        "}",
        "",
        "document.addEventListener('keydown', e => {",
        "    if (e.code === 'Space') jump();",
        "});",
    ];

    [Fact]
    public async Task A_file_that_fits_is_read_whole_even_when_the_search_words_hit_it()
    {
        var model = new OneCallModel("4-8");

        var spans = await FileFocus.AskAsync(model, "how does the player jump", "game.js", Game, default);

        Assert.Equal([new LineSpan(4, 8)], spans);
        Assert.Contains("1| const canvas", model.Prompt);
        Assert.DoesNotContain("## block", model.Prompt);
    }

    [Fact]
    public async Task A_file_too_big_to_read_whole_becomes_candidate_blocks_the_model_chooses_among()
    {
        // Filler dense enough that even clipped it overflows the whole-file budget.
        var filler = Enumerable.Range(0, 1600).Select(i => $"const filler{i} = '{new string('x', 140)}';");
        var big = Game.Append("").Concat(filler).ToArray();
        Assert.False(FileFocus.FitsWhole(big));

        // "jump" hits lines 4 and 11, close enough to form one block: 4-12.
        var model = new OneCallModel(null, bestJson: "1");

        var spans = await FileFocus.AskAsync(model, "how does the player jump", "game.js", big, default);

        Assert.Equal([new LineSpan(4, 12)], spans);
        Assert.Contains("## block 1", model.Prompt);
        Assert.Contains("function jump()", model.Prompt);
        Assert.DoesNotContain("1| const canvas", model.Prompt);
    }

    [Fact]
    public async Task A_query_that_hits_nothing_falls_back_to_whole_file_ranges()
    {
        var model = new OneCallModel("4-8");

        var spans = await FileFocus.AskAsync(model, "quantum entanglement flux", "game.js", Game, default);

        Assert.Equal([new LineSpan(4, 8)], spans);
        Assert.Contains("1| const canvas", model.Prompt);
        Assert.Contains("12| });", model.Prompt);
    }

    [Fact]
    public async Task No_answer_is_null_so_the_instant_spans_stay()
    {
        Assert.Null(await FileFocus.AskAsync(new OneCallModel(null), "jump", "game.js", Game, default));
    }

    [Fact]
    public void Candidate_blocks_widen_to_blank_lines_and_merge_overlaps()
    {
        // Lines 4 and 11 both mention jump; 11 - 4 is within the grouping gap, so
        // they widen and merge into the one blank-line-bounded block.
        var candidates = FileFocus.Candidates("jump", Game);

        Assert.Equal([new LineSpan(4, 12)], candidates);
    }

    [Fact]
    public void Out_of_range_and_repeated_block_numbers_are_dropped()
    {
        var candidates = new[] { new LineSpan(4, 7), new LineSpan(9, 11) };

        Assert.Equal([new LineSpan(9, 11), new LineSpan(4, 7)],
            FileFocus.PickCandidates("99, 2, 2, 1", candidates));
        Assert.Empty(FileFocus.PickCandidates("", candidates));
    }

    [Fact]
    public void Instant_spans_group_nearby_lines_with_the_search_words()
    {
        var spans = FileFocus.Instant("where is jump", Game);

        Assert.Equal([new LineSpan(4, 4), new LineSpan(11, 11)], spans);
    }

    [Theory]
    [InlineData("4-8, 10-12", "4-8,10-12")]
    [InlineData("10 to 12; 4..8", "10-12,4-8")]
    [InlineData("7", "7-7")]
    [InlineData("8-4", "4-8")]
    [InlineData("4-8, 6-11", "4-11")]
    [InlineData("1-999", "1-12")]
    [InlineData("", "")]
    public void Ranges_are_parsed_clamped_and_merged(string text, string expected) =>
        Assert.Equal(expected, string.Join(",", FileFocus.ParseRanges(text, 12).Select(s => $"{s.Start}-{s.End}")));

    [Fact]
    public void A_file_too_large_for_the_context_keeps_the_relevant_part_and_its_outline()
    {
        var lines = new List<string>();
        for (var i = 0; i < 4000; i++)
        {
            lines.Add(i % 50 == 0 ? $"function helper{i}() {{" : $"    total += compute(value{i}, factor, offset, scale); // padding padding");
        }

        lines[3000] = "    applyGravity(player); // jump arc";

        var kept = FileFocus.Condense("jump", lines).ToList();

        Assert.True(kept.Sum(l => l.Text.Length + 8) <= FileFocus.MaxFileChars);
        Assert.Contains(kept, l => l.Number == 3001);
        Assert.Contains(kept, l => l.Number == 1 && l.Text.StartsWith("function helper0"));
    }
}
