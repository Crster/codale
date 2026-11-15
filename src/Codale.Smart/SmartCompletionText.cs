namespace Codale.Smart;

/// <summary>Turns a model's raw continuation into ghost text for the caret.</summary>
public static class SmartCompletionText
{
    /// <summary>
    /// Instruct models often start their answer with what is already typed ("Get-ChildI" → "Get-ChildItem -File"):
    /// the longest such overlap (three characters or more) is dropped, so the ghost holds only what comes next.
    /// </summary>
    public static string? WithoutRepeat(string prefix, string completion)
    {
        var line = prefix.Replace("\r\n", "\n");
        line = line[(line.LastIndexOf('\n') + 1)..];
        for (var length = Math.Min(line.Length, completion.Length); length >= 3; length--)
        {
            if (line.EndsWith(completion[..length], StringComparison.Ordinal))
            {
                completion = completion[length..];
                break;
            }
        }
        return completion.Trim().Length == 0 ? null : completion;
    }

    /// <summary>The first line only, never a fence or tool syntax: it is ghost text, not an answer.</summary>
    public static string? Clean(string? text)
    {
        var line = (text ?? "").Replace("\r\n", "\n").Split('\n')[0].TrimEnd();
        return line.Trim().Length == 0 || line.Length > 160 || line.Contains("```", StringComparison.Ordinal) ||
            line.Contains("<tool_call>", StringComparison.Ordinal) || line.Any(char.IsControl) ? null : line;
    }
}
