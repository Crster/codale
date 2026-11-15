using System.Text.Json;
using Codale.Core.Agents;

namespace Codale.Smart;

/// <summary>The helper calls the tool-style features make through a Smart host.</summary>
public static class SmartHelperRunner
{
    /// <summary>A helper runs only when its feature is on and the model is ready.</summary>
    public static bool IsAvailable(ISmartTaskHost host, string task) => host.IsFeatureEnabled(task) && host.IsReady;

    /// <summary>
    /// One helper answer within <paramref name="timeout"/>; null when the helper is off, the model has no answer, or the
    /// deadline passes (a caller's own cancellation still propagates).
    /// </summary>
    public static async Task<T?> RunAsync<T>(ISmartTaskHost host, string projectPath, string task, TimeSpan timeout,
        string system, string input, int cap, Func<string, T?> parse, CancellationToken ct) where T : class
    {
        if (!IsAvailable(host, task)) return null;
        return await SmartRequestWorkflow.RunWithDeadlineAsync<T>(timeout, async token =>
        {
            var result = await host.GenerateAsync(new SmartRequestContext(projectPath, projectPath, task),
                system, input, cap, parse, background: false, token).ConfigureAwait(false);
            return result?.Value;
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>One-line continuations for the composer and terminal input, asked once per typing pause.</summary>
public static class SmartInlineSuggester
{
    /// <summary>The text to insert after <paramref name="prefix"/>; null when off, superseded or nothing useful came back.</summary>
    public static async Task<string?> SuggestAsync(ISmartTaskHost host, string task, string project, string prefix,
        TimeSpan pause, int tokens, CancellationToken ct)
    {
        if (!SmartHelperRunner.IsAvailable(host, task)) return null;
        try
        {
            await Task.Delay(pause, ct).ConfigureAwait(false);
            var input = JsonSerializer.Serialize(new { prefix, suffix = "" });
            var result = await host.GenerateAsync(new SmartRequestContext(project, project, task), "", input, tokens,
                SmartCompletionText.Clean, background: true, ct).ConfigureAwait(false);
            return result?.Value is { } text ? SmartCompletionText.WithoutRepeat(prefix, text) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
