using Codale.Agents;
using Codale.Agents.Local;
using Codale.Agents.OpenAi;
using Codale.Core.Helper;

namespace Codale.App.Services;

/// <summary>
/// The background-task model as Settings configures it, read afresh on every call. Each provider
/// runs its cheap model: a BYOK API or a CLI set to a BYOK provider its flash model, the Claude
/// login <see cref="HelperModel.ClaudeModel"/>, the Codex login its flash tier or
/// <see cref="HelperModel.DefaultCodexModel"/>.
/// </summary>
public static class BackgroundHelper
{
    private static readonly CliEndpointSettings Endpoints = new();

    /// <summary>One model for every window: it holds the weights in memory.</summary>
    private static readonly LocalHelperModel LocalModel = new(() => AppSettings.HelperLocalModelPath);

    /// <summary>
    /// The model for per-keystroke completions (shell and editor ghost text), or null unless Settings runs
    /// background jobs on the local model: a remote model is far too slow to complete as the user types.
    /// </summary>
    public static LocalHelperModel? Completions =>
        string.Equals(AppSettings.HelperProvider, AppSettings.LocalHelperProvider, StringComparison.OrdinalIgnoreCase) && LocalModel.IsAvailable
            ? LocalModel
            : null;

    public static HelperModel Create() => new(
        () => AppSettings.HelperApiEndpoint is { } api ? new OpenAiEndpoint(api.BaseUrl, api.ApiKey, api.Model) : null,
        () => AppSettings.HelperProvider,
        () => AppSettings.TierModelFor("codex", "flash") ?? HelperModel.DefaultCodexModel,
        () => Endpoints.HelperEndpoint("claude"),
        () => Endpoints.HelperEndpoint("codex"),
        () => string.Equals(AppSettings.HelperProvider, AppSettings.LocalHelperProvider, StringComparison.OrdinalIgnoreCase)
            ? LocalModel
            : null);
}
