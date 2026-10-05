namespace Codale.App.Services;

/// <summary>
/// One bring-your-own-key provider as it appears in settings.json under
/// <c>byok.providers</c>: an OpenAI-compatible Chat Completions endpoint with its key and
/// the model names Claude should ask for. The status bar only picks between these.
/// </summary>
public sealed class ByokProvider
{
    /// <summary>The label shown in the status bar and the picker; unique across providers.</summary>
    public string Name { get; set; } = "";

    /// <summary>The OpenAI-compatible base URL, as an OpenAI SDK takes it (usually ending in /v1; no /chat/completions suffix).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The plain key in memory; settings.json holds it DPAPI-protected (<c>enc:</c> prefix), see <see cref="SecretProtector"/>.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The lite model: what a session starts with, and the everyday / background one.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string LiteModel { get; set; } = "";

    /// <summary>The most capable model, offered next to the lite model; may be blank.</summary>
    public string SmartModel { get; set; } = "";

    // Usage estimates use Claude Sonnet 5.5 rates as the base cost (USD per million tokens).
    private const decimal InputPricePerMillion = 2m;
    private const decimal OutputPricePerMillion = 10m;
    private const decimal CachedInputPricePerMillion = 0.20m;

    /// <summary>Always true: every provider is estimated at the Sonnet 5.5 base rates.</summary>
    public bool HasPricing => true;

    /// <summary>What one request costs at the Sonnet 5.5 base rates.</summary>
    public decimal EstimateCost(Codale.Core.Agents.UsageSnapshot usage)
    {
        return (usage.InputTokens + usage.CacheCreationInputTokens) * InputPricePerMillion / 1_000_000m
            + usage.CacheReadInputTokens * CachedInputPricePerMillion / 1_000_000m
            + usage.OutputTokens * OutputPricePerMillion / 1_000_000m;
    }
}
