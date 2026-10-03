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

    /// <summary>The model a session starts with, and the everyday / background one.</summary>
    public string Model { get; set; } = "";

    /// <summary>The most capable model, offered next to the default; may be blank.</summary>
    public string SmartModel { get; set; } = "";

    /// <summary>USD per million fresh input tokens, for the usage estimate; 0 when unset.</summary>
    public decimal InputPricePerMillion { get; set; }

    /// <summary>USD per million output tokens; 0 when unset.</summary>
    public decimal OutputPricePerMillion { get; set; }

    /// <summary>USD per million cached input tokens (reads); 0 bills them as fresh input.</summary>
    public decimal CachedInputPricePerMillion { get; set; }

    /// <summary>True once any price is set, so the cost row can say "not set" instead of $0.</summary>
    public bool HasPricing => InputPricePerMillion > 0 || OutputPricePerMillion > 0;

    /// <summary>What one request costs at these prices.</summary>
    public decimal EstimateCost(Codale.Core.Agents.UsageSnapshot usage)
    {
        var cacheRate = CachedInputPricePerMillion > 0 ? CachedInputPricePerMillion : InputPricePerMillion;
        return (usage.InputTokens + usage.CacheCreationInputTokens) * InputPricePerMillion / 1_000_000m
            + usage.CacheReadInputTokens * cacheRate / 1_000_000m
            + usage.OutputTokens * OutputPricePerMillion / 1_000_000m;
    }
}
