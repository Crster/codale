using Codale.Smart;

namespace Codale.App.Services;

/// <summary>
/// One-line continuations for the chat composer and the terminal input: the Auto Complete (FIM) model, or the Decision
/// model through an insertion prompt when no FIM model is set. Waits for a typing pause, so it is asked once per pause.
/// </summary>
internal sealed class SmartInlineCompletion(SmartExperienceHost smart)
{
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(450);
    private const int Tokens = 32;

    public bool IsEnabled(string task) => SmartHelperRunner.IsAvailable(smart, task);

    /// <summary>The text to insert after <paramref name="prefix"/>; null when off, superseded or nothing useful came back.</summary>
    public Task<string?> SuggestAsync(string task, string project, string prefix, CancellationToken ct) =>
        SmartInlineSuggester.SuggestAsync(smart, task, project, prefix, Pause, Tokens, ct);
}