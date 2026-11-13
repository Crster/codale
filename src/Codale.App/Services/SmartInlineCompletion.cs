using System.Text.Json;

namespace Codale.App.Services;

/// <summary>
/// One-line continuations for the chat composer and the terminal input: the Auto Complete (FIM) model, or the Decision
/// model through an insertion prompt when no FIM model is set. Waits for a typing pause, so it is asked once per pause.
/// </summary>
internal static class SmartInlineCompletion
{
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(450);
    private const int Tokens = 32;

    public static bool IsEnabled(string task) =>
        AppSettings.SmartFeatureEnabled(task) && SmartExperienceHost.Instance.Status.State == Codale.Smart.SmartState.Ready;

    /// <summary>The text to insert after <paramref name="prefix"/>; null when off, superseded or nothing useful came back.</summary>
    public static async Task<string?> SuggestAsync(string task, string project, string prefix, CancellationToken ct)
    {
        if (!IsEnabled(task)) return null;
        try
        {
            await Task.Delay(Pause, ct).ConfigureAwait(false);
            var input = JsonSerializer.Serialize(new { prefix, suffix = "" });
            var result = await SmartExperienceHost.Instance.GenerateAsync(new(project, project, task), "", input, Tokens,
                Clean, background: true, ct).ConfigureAwait(false);
            return result?.Value is { } text ? WithoutRepeat(prefix, text) : null;
        }
        catch (OperationCanceledException) { return null; }
    }

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
