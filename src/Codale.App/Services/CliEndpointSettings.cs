using Codale.Agents.OpenAi;
using Codale.Core.Agents;

namespace Codale.App.Services;

/// <summary>
/// The agent endpoint a chat uses: a BYOK provider listed in settings.json
/// (<see cref="AppSettings.ByokProviders"/>), named by the chat. The provider is
/// OpenAI-compatible, so Claude reaches it through the local <see cref="MessagesBridge"/>:
/// the bridge's URL and token and the model names reach Claude as ANTHROPIC_* variables.
/// </summary>
/// <remarks>
/// Values are read from <see cref="AppSettings"/> on every call, so an edited provider
/// applies to the next session start without any event wiring. With "" (Default)
/// there is no endpoint and Claude runs with its own login.
/// </remarks>
public sealed class CliEndpointSettings
{
    private static string Clean(string? raw) => string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim();

    /// <summary>True when the named provider exists and has a base URL, i.e. sessions should use the endpoint.</summary>
    public bool IsActive(string providerName) => Clean(AppSettings.FindByok(providerName)?.BaseUrl).Length > 0;

    /// <summary>
    /// The models the provider offers, default first; empty when the endpoint is off.
    /// The smart model is left out when it is blank or the same as the default.
    /// </summary>
    public IReadOnlyList<AgentModelInfo> Models(string providerName)
    {
        var models = new List<AgentModelInfo>();
        var active = AppSettings.FindByok(providerName);
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
    /// What Claude's spawn environment gains, or null when the provider is Default or has no
    /// base URL. Claude is pointed at the bridge with a token for this provider; the provider's
    /// own key stays in the bridge. Every model slot the CLI can route to is pinned to one of
    /// the two configured names, so nothing asks for an Anthropic id the provider does not
    /// serve: opus / fable are the smart model, sonnet / haiku / background calls the default one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bridge could not start.</exception>
    public IReadOnlyDictionary<string, string>? ClaudeEnvironment(string providerName)
    {
        // One snapshot: each lookup copies the provider.
        var active = AppSettings.FindByok(providerName);
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

        // The CLI abandons a request after 10 minutes by default; a slow provider or a long
        // reasoning pass behind the bridge can outlast that. A value the user set wins.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("API_TIMEOUT_MS")))
        {
            env["API_TIMEOUT_MS"] = "1800000";
        }

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
            env["ANTHROPIC_DEFAULT_FABLE_MODEL"] = smart;
        }

        return env;
    }
}
