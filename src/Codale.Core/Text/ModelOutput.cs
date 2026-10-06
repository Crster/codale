using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codale.Core.Text;

/// <summary>
/// Cleans a model's reply before anything reads it. Small and local models leak their chat
/// template into the text - a <c>&lt;think&gt;</c> block, <c>&lt;|im_end|&gt;</c>, gpt-oss's channel
/// markers, a terminal escape - and nothing downstream should have to know about that.
/// </summary>
/// <remarks>
/// <see cref="Clean"/> only touches the edges of the reply, so it is safe for code a model
/// wrote: a file that mentions <c>&lt;think&gt;</c> in a string keeps it. <see cref="CleanText"/> is
/// for prose and one-liners: it also cuts at a template token anywhere, and undoes a reply
/// sent back as one JSON string, one fenced block or with its line breaks escaped.
/// </remarks>
public static partial class ModelOutput
{
    /// <summary>The reply without control characters, leading reasoning or template tokens at its edges.</summary>
    public static string Clean(string? text) => Edges(text, orphanClose: false);

    /// <summary>
    /// <see cref="Clean"/> for a reply that must hold a JSON tool call: a lone closing
    /// <c>&lt;/think&gt;</c> also drops everything before it, which code could not afford.
    /// </summary>
    public static string CleanCall(string? text) => Edges(text, orphanClose: true);

    /// <summary>
    /// <see cref="Clean"/>, then for prose: whatever follows an end-of-turn token is the model
    /// talking past its turn, reasoning blocks and template tokens anywhere go, and a reply
    /// wrapped as a JSON string or a single fence is unwrapped.
    /// </summary>
    public static string CleanText(string? text)
    {
        text = Edges(text, orphanClose: true);

        while (EndOfTurn().Match(text) is { Success: true } end)
        {
            // A stray token in front of the answer is dropped; one after it ends the answer.
            text = end.Index == 0 ? text[end.Length..].TrimStart() : text[..end.Index];
        }

        text = ReasoningBlock().Replace(text, "");
        text = StripTemplateTokens(text);
        return Unwrap(text.Trim()).Trim();
    }

    /// <summary>Removes every <c>&lt;|token|&gt;</c> of a chat template from the text.</summary>
    public static string StripTemplateTokens(string text) => TemplateToken().Replace(text, "");

    private static string Edges(string? text, bool orphanClose)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        text = ControlChars().Replace(text, "");
        text = FinalChannel(text);
        text = LeadingTemplate().Replace(text, "");
        text = StripLeadingReasoning(text, orphanClose);
        text = LeadingTemplate().Replace(text, "");
        text = TrailingTemplate().Replace(text, "");
        return text.Trim();
    }

    /// <summary>
    /// gpt-oss writes channels: <c>&lt;|channel|&gt;analysis&lt;|message|&gt;…&lt;|end|&gt;</c>, then the
    /// answer on the final channel. Only the final channel is the reply; an analysis channel
    /// alone means the model never reached its answer. Only a reply that opens with a channel
    /// is read this way, so code that mentions the markers is left alone.
    /// </summary>
    private static string FinalChannel(string text)
    {
        const string Channel = "<|channel|>";
        const string Message = "<|message|>";

        var opening = text.TrimStart();
        if (!opening.StartsWith(Channel, StringComparison.Ordinal) && !opening.StartsWith("<|start|>", StringComparison.Ordinal))
        {
            return text;
        }

        var channel = text.LastIndexOf(Channel, StringComparison.Ordinal);
        if (channel < 0)
        {
            return text;
        }

        var message = text.IndexOf(Message, channel, StringComparison.Ordinal);
        if (message < 0)
        {
            return text;
        }

        var name = text[(channel + Channel.Length)..message].Trim();
        return name.StartsWith("analysis", StringComparison.OrdinalIgnoreCase) ? "" : text[(message + Message.Length)..];
    }

    private static string StripLeadingReasoning(string text, bool orphanClose)
    {
        while (true)
        {
            var trimmed = text.TrimStart();
            if (ReasoningOpen().Match(trimmed) is { Success: true } open)
            {
                var closeTag = $"</{open.Groups[1].Value}>";
                var close = trimmed.IndexOf(closeTag, open.Length, StringComparison.OrdinalIgnoreCase);

                // Cut off mid-thought: nothing in it is the answer.
                if (close < 0)
                {
                    return "";
                }

                text = trimmed[(close + closeTag.Length)..];
                continue;
            }

            // The opening tag was in the chat template, so only the closing one came back.
            if (orphanClose && ReasoningClose().Match(trimmed) is { Success: true } orphan &&
                !trimmed[..orphan.Index].Contains($"<{orphan.Groups[1].Value}>", StringComparison.OrdinalIgnoreCase))
            {
                text = trimmed[(orphan.Index + orphan.Length)..];
                continue;
            }

            return text;
        }
    }

    private static string Unwrap(string text)
    {
        // The whole reply as one JSON string: "Fix the login redirect".
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            try
            {
                if (JsonSerializer.Deserialize<string>(text) is { } inner)
                {
                    text = inner.Trim();
                }
            }
            catch (JsonException)
            {
            }
        }

        // The whole reply in one prose fence; a code fence is left alone.
        if (WholeFence().Match(text) is { Success: true } fence)
        {
            text = fence.Groups[1].Value.Trim();
        }

        // Line breaks sent back escaped: several \n and not one real line break.
        if (!text.Contains('\n') && text.Split("\\n").Length > 2)
        {
            text = text.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"");
        }

        return text;
    }

    /// <summary>Terminal escape sequences, C0 controls other than tab, line breaks and form feed, and invisible marks.</summary>
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)?|\x1B[@-Z\\-_]|[\x00-\x08\x0B\x0E-\x1F\x7F​⁠﻿]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(
        @"\A(?:\s*(?:<\|im_start\|>(?:assistant)?|<\|start_header_id\|>assistant<\|end_header_id\|>|<\|begin_of_text\|>|" +
        @"<\|startoftext\|>|<\|assistant\|>|<\|start\|>assistant|<start_of_turn>model|<\|im_end\|>|<\|eot_id\|>|<\|end\|>))+")]
    private static partial Regex LeadingTemplate();

    [GeneratedRegex(
        @"(?:\s*(?:<\|im_end\|>|<\|eot_id\|>|<\|eom_id\|>|<\|endoftext\|>|<\|end_of_text\|>|<\|end\|>|<\|return\|>|<\|call\|>|<end_of_turn>))+\s*\z")]
    private static partial Regex TrailingTemplate();

    [GeneratedRegex(
        @"<\|(?:im_end|im_start|eot_id|eom_id|endoftext|end_of_text|end|return|start|start_header_id)\|>|</s>|<end_of_turn>|<start_of_turn>|<eos>|\[/?INST\]")]
    private static partial Regex EndOfTurn();

    [GeneratedRegex(@"\A<(think|thinking|reasoning|reflection)>", RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningOpen();

    [GeneratedRegex(@"</(think|thinking|reasoning)>", RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningClose();

    [GeneratedRegex(@"<(think|thinking|reasoning|reflection)>.*?(?:</\1>|\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReasoningBlock();

    [GeneratedRegex(@"<\|[^<>|\s]{1,32}\|>")]
    private static partial Regex TemplateToken();

    [GeneratedRegex(@"\A```(?:markdown|md|text|txt|plaintext)?[ \t]*\r?\n(.*?)\r?\n?```\s*\z", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex WholeFence();
}
