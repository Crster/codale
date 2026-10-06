using Codale.Core.Text;

namespace Codale.Core.Tests;

public sealed class ModelOutputTests
{
    [Theory]
    [InlineData("<think>the user wants a title</think>\n\nFix login redirect", "Fix login redirect")]
    [InlineData("<THINKING>hmm</THINKING>Fix it", "Fix it")]
    [InlineData("<think>a</think><think>b</think> Fix it", "Fix it")]
    [InlineData("<think>cut off before any answer", "")]
    [InlineData("<|im_start|>assistant\nFix it<|im_end|>", "Fix it")]
    [InlineData("<|start_header_id|>assistant<|end_header_id|>\n\nFix it<|eot_id|>", "Fix it")]
    [InlineData("<|channel|>analysis<|message|>thinking<|end|><|start|>assistant<|channel|>final<|message|>Fix it<|return|>", "Fix it")]
    [InlineData("<|channel|>analysis<|message|>still thinking", "")]
    [InlineData("\u001b[32mFix it\u001b[0m\u0000", "Fix it")]
    [InlineData("﻿Fix it", "Fix it")]
    public void Leaked_reasoning_and_template_tokens_are_dropped(string raw, string expected) =>
        Assert.Equal(expected, ModelOutput.Clean(raw));

    [Fact]
    public void Code_keeps_tags_it_merely_mentions()
    {
        const string code = "var a = \"<think>\";\nvar b = \"</think>\";\nvar c = \"<|channel|>x<|message|>\";";

        Assert.Equal(code, ModelOutput.Clean(code));
    }

    [Fact]
    public void Code_keeps_a_lone_closing_tag()
    {
        const string code = "string close = \"</think>\";\nreturn close;";

        Assert.Equal(code, ModelOutput.Clean(code));
    }

    [Theory]
    [InlineData("the model reasoned here</think>\n{\"tool\":\"route\"}", "{\"tool\":\"route\"}")]
    [InlineData("<think>{\"tool\":\"wrong\"}</think>{\"tool\":\"route\"}", "{\"tool\":\"route\"}")]
    public void A_call_loses_reasoning_whose_opening_tag_was_in_the_template(string raw, string expected) =>
        Assert.Equal(expected, ModelOutput.CleanCall(raw));

    [Theory]
    [InlineData("Git panel<|im_end|>\nextra words", "Git panel")]
    [InlineData("Fix it</s>Sure! Anything else?", "Fix it")]
    [InlineData("\"Fix the login redirect\"", "Fix the login redirect")]
    [InlineData("```markdown\n## Goal\nShip it\n```", "## Goal\nShip it")]
    [InlineData("## Goal\\nShip it\\n## Next steps\\nTest", "## Goal\nShip it\n## Next steps\nTest")]
    [InlineData("fix: handle \\n in paths", "fix: handle \\n in paths")]
    [InlineData("Retries live in RetryPolicy.<|eot_id|>", "Retries live in RetryPolicy.")]
    public void Prose_is_cut_at_the_end_of_its_turn_and_unwrapped(string raw, string expected) =>
        Assert.Equal(expected, ModelOutput.CleanText(raw));

    [Fact]
    public void A_code_fence_is_not_unwrapped_as_prose()
    {
        const string reply = "```cs\nvar x = 1;\n```";

        Assert.Equal(reply, ModelOutput.CleanText(reply));
    }

    [Fact]
    public void The_stream_splitter_routes_inline_reasoning_to_thinking_across_pieces()
    {
        var splitter = new InlineReasoningSplitter();
        var parts = new List<(bool Thinking, string Text)>();
        foreach (var piece in new[] { "<thi", "nk>pondering", " more</th", "ink>\n\nThe ans", "wer<|im_", "end|>" })
        {
            parts.AddRange(splitter.Feed(piece));
        }

        parts.AddRange(splitter.Flush());

        Assert.Equal("pondering more", string.Concat(parts.Where(p => p.Thinking).Select(p => p.Text)));
        Assert.Equal("The answer", string.Concat(parts.Where(p => !p.Thinking).Select(p => p.Text)));
    }

    [Fact]
    public void The_stream_splitter_leaves_a_plain_answer_alone()
    {
        var (reasoning, answer) = InlineReasoningSplitter.Split("Use <think> tags like this: <think>x</think>.");

        Assert.Equal("", reasoning);
        Assert.Equal("Use <think> tags like this: <think>x</think>.", answer);
    }

    [Fact]
    public void The_stream_splitter_holds_a_lone_angle_bracket_until_it_knows()
    {
        var splitter = new InlineReasoningSplitter();

        Assert.Equal("a ", string.Concat(splitter.Feed("a <").Select(p => p.Text)));
        Assert.Equal("< b", string.Concat(splitter.Feed(" b").Select(p => p.Text)));
    }
}
