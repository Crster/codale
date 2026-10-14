using Codale.Core.Agents;

namespace Codale.App.Services;

/// <summary>
/// The agent endpoint a chat uses: a BYOK provider listed in settings.json
/// (<see cref="AppSettings.ByokProviders"/>), named by the chat. Claude sessions point
/// straight at the provider's Anthropic-compatible base URL; Codex sessions at its
/// OpenAI-compatible one. The key and model names reach the CLI as environment variables.
/// </summary>
/// <remarks>
/// Values are read from <see cref="AppSettings"/> on every call, so an edited provider
/// applies to the next session start without any event wiring. With "" (Default)
/// there is no endpoint and the CLI runs with its own login.
/// </remarks>
public sealed class CliEndpointSettings
{
    private static string Clean(string? raw) => string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim();

    /// <summary>True when the named provider exists and has the base URL the CLI needs, i.e. sessions should use the endpoint.</summary>
    public bool IsActive(string providerName, bool openAiCompatible = true) =>
        Clean(openAiCompatible ? AppSettings.FindByok(providerName)?.BaseUrl : AppSettings.FindByok(providerName)?.AnthropicBaseUrl).Length > 0;

    /// <summary>
    /// The models the provider offers, default first; empty when the endpoint is off.
    /// The pro model is left out when it is blank or the same as the default.
    /// </summary>
    public IReadOnlyList<AgentModelInfo> Models(string providerName)
    {
        var models = new List<AgentModelInfo>();
        var active = AppSettings.FindByok(providerName);
        if (Clean(active?.BaseUrl).Length == 0 && Clean(active?.AnthropicBaseUrl).Length == 0)
        {
            return models;
        }

        var flashModel = Clean(active?.FlashModel);
        var proModel = Clean(active?.ProModel);
        if (flashModel.Length > 0)
        {
            models.Add(new AgentModelInfo { Id = flashModel, DisplayName = flashModel, Description = "Flash model", IsDefault = true, ContextWindow = ContextWindowFor(providerName, flashModel) });
        }

        if (proModel.Length > 0 && !string.Equals(proModel, flashModel, StringComparison.Ordinal))
        {
            models.Add(new AgentModelInfo { Id = proModel, DisplayName = proModel, Description = "Pro model", ContextWindow = ContextWindowFor(providerName, proModel) });
        }

        return models;
    }

    /// <summary>
    /// What Claude's spawn environment gains, or null when the provider is Default. The
    /// provider must expose an Anthropic-compatible base URL; an OpenAI-only provider
    /// cannot serve Claude without the removed local bridge, so that throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">The provider is OpenAI-only.</exception>
    public IReadOnlyDictionary<string, string>? ClaudeEnvironment(string providerName)
    {
        var active = AppSettings.FindByok(providerName);
        if (active is null)
        {
            return null;
        }

        var anthropicBase = Clean(active.AnthropicBaseUrl);
        if (anthropicBase.Length == 0)
        {
            var openAiBase = Clean(active.BaseUrl);
            if (openAiBase.Length == 0)
            {
                return null;
            }

            throw new InvalidOperationException(
                $"Provider \"{active.Name}\" has no Anthropic-compatible base URL, so Claude cannot reach it. " +
                "Add the Anthropic base URL in BYOK settings, or use Codex for this provider.");
        }

        var env = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = anthropicBase,
            ["ANTHROPIC_AUTH_TOKEN"] = Clean(active.ApiKey),
        };

        // The CLI abandons a request after 10 minutes by default; a slow provider or a long
        // reasoning pass can outlast that. A value the user set wins.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("API_TIMEOUT_MS")))
        {
            env["API_TIMEOUT_MS"] = "1800000";
        }

        return env;
    }

    /// <summary>
    /// What Codex's spawn environment gains, or null when the provider is Default or has
    /// no OpenAI-compatible base URL.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CodexEnvironment(string providerName)
    {
        var active = AppSettings.FindByok(providerName);
        if (Clean(active?.BaseUrl) is not { Length: > 0 } baseUrl)
        {
            return null;
        }

        var env = new Dictionary<string, string>
        {
            ["OPENAI_BASE_URL"] = baseUrl,
            ["OPENAI_API_KEY"] = Clean(active?.ApiKey),
        };

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("API_TIMEOUT_MS")))
        {
            env["API_TIMEOUT_MS"] = "1800000";
        }

        return env;
    }

    /// <summary>
    /// Custom model provider settings for Codex CLI, or null when the provider has no
    /// OpenAI-compatible base URL.
    /// </summary>
    public Codale.Agents.Codex.CodexCustomProvider? CodexProvider(string providerName)
    {
        var active = AppSettings.FindByok(providerName);
        if (active is null || Clean(active.BaseUrl) is not { Length: > 0 } baseUrl)
        {
            return null;
        }

        return new Codale.Agents.Codex.CodexCustomProvider(
            Name: string.IsNullOrWhiteSpace(active.Name) ? "custom" : active.Name.Trim(),
            BaseUrl: baseUrl.TrimEnd('/'),
            EnvKey: "OPENAI_API_KEY",
            WireApi: "responses");
    }

    /// <summary>A model's published context size, falling back to the provider-wide setting.</summary>
    public int? ContextWindowFor(string providerName, string? model)
    {
        var active = AppSettings.FindByok(providerName);
        if (active is null || Clean(model) is not { Length: > 0 } id)
        {
            return active?.ContextWindow is > 0 ? active.ContextWindow : null;
        }

        return active.ModelContextWindows is not null &&
            active.ModelContextWindows.TryGetValue(id, out var window) && window > 0
            ? window
            : active.ContextWindow is > 0 ? active.ContextWindow : null;
    }
}
