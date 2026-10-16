using Codale.Agents;
using Codale.Agents.OpenAi;

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

    public static HelperModel Create() => new(
        () => AppSettings.HelperApiEndpoint is { } api ? new OpenAiEndpoint(api.BaseUrl, api.ApiKey, api.Model) : null,
        () => AppSettings.HelperProvider,
        () => AppSettings.TierModelFor("codex", "flash") ?? HelperModel.DefaultCodexModel,
        () => Endpoints.HelperEndpoint("claude"),
        () => Endpoints.HelperEndpoint("codex"));
}
