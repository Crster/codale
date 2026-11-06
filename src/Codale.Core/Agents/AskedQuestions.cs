using System.Text;
using System.Text.Json;

namespace Codale.Core.Agents;

/// <summary>One question the agent asked through AskUserQuestion, with the reader's answer once there is one.</summary>
public sealed record AskedQuestion(string? Header, string Question, string? Answer);

/// <summary>
/// The question-and-answer record of an AskUserQuestion call, for its step's output: the questions come
/// from the call's input, the answers from what the reader picked or - for a transcript read back - from
/// the CLI's result text, which reads
/// <c>Your questions have been answered: "question"="answer", "question"="a, b". You can now continue…</c>.
/// </summary>
public static class AskedQuestions
{
    /// <summary>The questions in <paramref name="input"/>, answered from <paramref name="resultText"/> where it says.</summary>
    public static IReadOnlyList<AskedQuestion> Read(JsonElement input, string? resultText)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("questions", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var asked = new List<(string? Header, string Question)>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("question", out var question) &&
                question.ValueKind == JsonValueKind.String &&
                question.GetString() is { Length: > 0 } text)
            {
                var header = item.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.String
                    ? h.GetString()
                    : null;
                asked.Add((header, text));
            }
        }

        var answers = AnswersFromResult(asked.Select(q => q.Question).ToList(), resultText);
        return asked.Select((q, i) => new AskedQuestion(q.Header, q.Question, answers[i])).ToList();
    }

    /// <summary>
    /// Each question's answer as the result text quotes it. The questions are known, so each answer is
    /// cut between its own <c>"question"="</c> and the next question's: an answer may itself hold quotes
    /// or commas without throwing the split off.
    /// </summary>
    private static string?[] AnswersFromResult(IReadOnlyList<string> questions, string? resultText)
    {
        var answers = new string?[questions.Count];
        if (string.IsNullOrEmpty(resultText))
        {
            return answers;
        }

        var starts = new int[questions.Count];
        var from = 0;
        for (var i = 0; i < questions.Count; i++)
        {
            var marker = $"\"{questions[i]}\"=\"";
            var at = resultText.IndexOf(marker, from, StringComparison.Ordinal);
            starts[i] = at < 0 ? -1 : at + marker.Length;
            if (at >= 0)
            {
                from = starts[i];
            }
        }

        for (var i = 0; i < questions.Count; i++)
        {
            if (starts[i] < 0)
            {
                continue;
            }

            var end = resultText.Length;
            for (var next = i + 1; next < questions.Count; next++)
            {
                if (starts[next] >= 0)
                {
                    end = starts[next] - questions[next].Length - 4; // back over "question"="
                    break;
                }
            }

            if (end == resultText.Length)
            {
                var closing = resultText.LastIndexOf("\". ", StringComparison.Ordinal);
                end = closing >= starts[i] ? closing + 1 : resultText.Length;
            }

            var answer = resultText[starts[i]..Math.Max(starts[i], end)].TrimEnd();
            answer = answer.TrimEnd(',', ' ').TrimEnd();
            if (answer.EndsWith('"'))
            {
                answer = answer[..^1];
            }

            answers[i] = answer.Length > 0 ? answer : null;
        }

        return answers;
    }

    /// <summary>The record as the step's output: each question, then its answer.</summary>
    public static string Format(IReadOnlyList<AskedQuestion> questions)
    {
        var text = new StringBuilder();
        foreach (var asked in questions)
        {
            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            if (asked.Header is { Length: > 0 } header)
            {
                text.Append('[').Append(header).Append("] ");
            }

            text.Append("Q: ").Append(asked.Question.Trim()).Append('\n');
            text.Append("A: ").Append(asked.Answer is { Length: > 0 } answer ? answer : "(no answer)");
        }

        return text.ToString();
    }
}
