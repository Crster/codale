using Codale.Agents.OpenAi;
using Codale.Core.Agents;

namespace Codale.App.Services;

/// <summary>
/// The agent endpoint in use: the BYOK provider picked in the status bar, one of those
/// listed in settings.json (<see cref="AppSettings.ByokProviders"/>). The provider is
/// OpenAI-compatible, so Claude reaches it through the local <see cref="MessagesBridge"/>:
/// the bridge's URL and token and the model names reach Claude as ANTHROPIC_* variables.
/// </summary>
/// <remarks>
/// Values are read from <see cref="AppSettings"/> on every access, so a change of
/// selection applies to the next session start without any event wiring. With
/// "Default" selected there is no endpoint and Claude runs with its own login.
/// </remarks>
public sealed class CliEndpointSettings
{
    private static string Clean(string? raw) => string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim();

    /// <summary>The provider's OpenAI-compatible base URL (usually ending in /v1).</summary>
    public string? BaseUrl => Clean(AppSettings.ActiveByok?.BaseUrl);

    public string? ApiKey => Clean(AppSettings.ActiveByok?.ApiKey);

    /// <summary>The model a session starts with, and the everyday / background one.</summary>
    public string? DefaultModel => Clean(AppSettings.ActiveByok?.Model);

    /// <summary>The most capable model, for the hard work; offered next to the default.</summary>
    public string? SmartModel => Clean(AppSettings.ActiveByok?.SmartModel);

    /// <summary>True when a base URL is set, i.e. sessions should use the endpoint.</summary>
    public bool IsActive => BaseUrl is { Length: > 0 };

    /// <summary>
    /// The models the endpoint offers, default first; empty when the endpoint is off.
    /// The smart model is left out when it is blank or the same as the default.
    /// </summary>
    public IReadOnlyList<AgentModelInfo> Models()
    {
        var models = new List<AgentModelInfo>();
        var active = AppSettings.ActiveByok;
        if (Clean(active?.BaseUrl).Length == 0)
        {
            return models;
        }

        var defaultModel = Clean(active?.Model);
        var smartModel = Clean(active?.SmartModel);
        if (defaultModel.Length > 0)
        {
            models.Add(new AgentModelInfo { Id = defaultModel, DisplayName = defaultModel, Description = "Default model", IsDefault = true });
        }

        if (smartModel.Length > 0 && !string.Equals(smartModel, defaultModel, StringComparison.Ordinal))
        {
            models.Add(new AgentModelInfo { Id = smartModel, DisplayName = smartModel, Description = "Smart model" });
        }

        return models;
    }

    /// <summary>
    /// What Claude's spawn environment gains, or null when no endpoint is set. Claude is
    /// pointed at the bridge with a token for this provider; the provider's own key stays
    /// in the bridge. Every model slot the CLI can route to is pinned to one of the two
    /// configured names, so nothing asks for an Anthropic id the provider does not serve:
    /// opus is the smart model, sonnet / haiku / background calls are the default one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bridge could not start.</exception>
    public IReadOnlyDictionary<string, string>? ClaudeEnvironment()
    {
        // One snapshot: each AppSettings.ActiveByok read copies the provider.
        var active = AppSettings.ActiveByok;
        if (Clean(active?.BaseUrl) is not { Length: > 0 } baseUrl)
        {
            return null;
        }

        var defaultModel = Clean(active?.Model);
        var smartModel = Clean(active?.SmartModel);
        var def = defaultModel.Length > 0 ? defaultModel : smartModel;
        var smart = smartModel.Length > 0 ? smartModel : defaultModel;

        var (bridgeUrl, token) = MessagesBridge.Shared.Register(new OpenAiRoute(baseUrl, Clean(active?.ApiKey), def, smart));
        var env = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = bridgeUrl,
            ["ANTHROPIC_API_KEY"] = token,
        };

        if (def is { Length: > 0 })
        {
            env["ANTHROPIC_MODEL"] = def;
            env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = def;
            env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = def;
            env["ANTHROPIC_SMALL_FAST_MODEL"] = def;
        }

        if (smart is { Length: > 0 })
        {
            env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = smart;
        }

        return env;
    }
}
