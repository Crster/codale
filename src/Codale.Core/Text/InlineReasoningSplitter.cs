using System.Text;

namespace Codale.Core.Text;

/// <summary>
/// Splits a streamed reply into reasoning and answer for a model that writes its reasoning
/// inline - <c>&lt;think&gt;…&lt;/think&gt;</c> at the start of its text - instead of in a field of
/// its own, and drops chat-template tokens (<c>&lt;|im_end|&gt;</c>) from the answer. Text arrives
/// in arbitrary pieces, so a tag split across two pieces is held back until it is whole.
/// </summary>
/// <remarks>
/// Only a tag that opens the reply (or follows a finished reasoning block) counts: an answer
/// that talks about <c>&lt;think&gt;</c> tags further in keeps them.
/// </remarks>
public sealed class InlineReasoningSplitter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    /// <summary>Longest template token held back while it may still be completing.</summary>
    private const int MaxTokenChars = 34;

    private enum State { Start, Thinking, Answer }

    private readonly StringBuilder _pending = new();
    private State _state = State.Start;
    private bool _thought;

    /// <summary>The parts of the reply that are ready, in order; a tag still arriving is held.</summary>
    public IReadOnlyList<(bool Thinking, string Text)> Feed(string piece)
    {
        _pending.Append(piece);
        var output = new List<(bool Thinking, string Text)>();
        Drain(output, final: false);
        return output;
    }

    /// <summary>Whatever is still held, once the stream has ended.</summary>
    public IReadOnlyList<(bool Thinking, string Text)> Flush()
    {
        var output = new List<(bool Thinking, string Text)>();
        Drain(output, final: true);
        return output;
    }

    /// <summary>A whole reply split at once: its reasoning and its answer.</summary>
    public static (string Reasoning, string Answer) Split(string text)
    {
        var splitter = new InlineReasoningSplitter();
        var parts = splitter.Feed(text).Concat(splitter.Flush()).ToList();
        return (string.Concat(parts.Where(p => p.Thinking).Select(p => p.Text)).Trim(),
                string.Concat(parts.Where(p => !p.Thinking).Select(p => p.Text)));
    }

    private void Drain(List<(bool Thinking, string Text)> output, bool final)
    {
        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            switch (_state)
            {
                case State.Start:
                {
                    var trimmed = text.TrimStart();
                    if (trimmed.StartsWith(Open, StringComparison.OrdinalIgnoreCase))
                    {
                        _pending.Clear().Append(trimmed[Open.Length..]);
                        _state = State.Thinking;
                        _thought = true;
                        continue;
                    }

                    // Blank so far, or the first letters of "<think>": wait for more.
                    if (!final && Open.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    // The blank lines between a reasoning block and the answer are not the answer.
                    if (_thought)
                    {
                        _pending.Clear().Append(trimmed);
                    }

                    _state = State.Answer;
                    continue;
                }

                case State.Thinking:
                {
                    var close = text.IndexOf(Close, StringComparison.OrdinalIgnoreCase);
                    if (close >= 0)
                    {
                        Emit(output, thinking: true, text[..close]);
                        _pending.Clear().Append(text[(close + Close.Length)..]);
                        _state = State.Start;
                        continue;
                    }

                    var keep = final ? 0 : PartialTag(text, Close);
                    Emit(output, thinking: true, text[..^keep]);
                    _pending.Clear().Append(text[^keep..]);
                    return;
                }

                default:
                {
                    var keep = final ? 0 : PartialToken(text);
                    Emit(output, thinking: false, ModelOutput.StripTemplateTokens(text[..^keep]));
                    _pending.Clear().Append(text[^keep..]);
                    return;
                }
            }
        }
    }

    private static void Emit(List<(bool Thinking, string Text)> output, bool thinking, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (output.Count > 0 && output[^1].Thinking == thinking)
        {
            output[^1] = (thinking, output[^1].Text + text);
        }
        else
        {
            output.Add((thinking, text));
        }
    }

    /// <summary>How many trailing chars could be the start of <paramref name="tag"/>.</summary>
    private static int PartialTag(string text, string tag)
    {
        for (var length = Math.Min(tag.Length - 1, text.Length); length > 0; length--)
        {
            if (text.EndsWith(tag[..length], StringComparison.OrdinalIgnoreCase))
            {
                return length;
            }
        }

        return 0;
    }

    /// <summary>How many trailing chars could be a template token still arriving: a lone "&lt;" or an unclosed "&lt;|".</summary>
    private static int PartialToken(string text)
    {
        if (text.EndsWith('<'))
        {
            return 1;
        }

        var start = text.LastIndexOf("<|", StringComparison.Ordinal);
        return start >= 0 && text.Length - start <= MaxTokenChars && text.IndexOf("|>", start, StringComparison.Ordinal) < 0
            ? text.Length - start
            : 0;
    }
}
