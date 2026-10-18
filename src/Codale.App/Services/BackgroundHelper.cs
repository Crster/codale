using Codale.Agents;
using Codale.Agents.Completion;
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

    private static readonly InlineCompleter Completer = new(Create());

    /// <summary>
    /// The completer for per-keystroke suggestions (shell and editor ghost text), or null unless Settings
    /// runs background jobs on an API model: a CLI start-up per keystroke is far too slow to complete as
    /// the user types. Any OpenAI-compatible model works; a small, fast one feels best.
    /// </summary>
    public static InlineCompleter? Completions => AppSettings.HelperApiEndpoint is not null ? Completer : null;

    public static HelperModel Create() => new(
        () => AppSettings.HelperApiEndpoint is { } api ? new OpenAiEndpoint(api.BaseUrl, api.ApiKey, api.Model) : null,
        () => AppSettings.HelperProvider,
        () => AppSettings.TierModelFor("codex", "flash") ?? HelperModel.DefaultCodexModel,
        () => Endpoints.HelperEndpoint("claude"),
        () => Endpoints.HelperEndpoint("codex"));
}
