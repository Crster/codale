namespace Codale.App.Services;

/// <summary>
/// An agent profile: a signed-in account with an isolated CLI home, or a compatible
/// endpoint and its models. Existing shared endpoints keep an empty CliId.
/// </summary>
public sealed class ByokProvider
{
    public string Id { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => Id.Length > 0 ? Id : Name.Trim();
    public string CliId { get; set; } = "";
    public bool IsAccount { get; set; }
    public string AccountId { get; set; } = "";
    public string? Email { get; set; }
    public bool Supports(string cli) => CliId.Length > 0 ? CliId == cli :
        cli == "codex" ? BaseUrl.Length > 0 : AnthropicBaseUrl.Length > 0;

    /// <summary>The display label; different agents may use the same label.</summary>
    public string Name { get; set; } = "";

    /// <summary>The OpenAI-compatible base URL, as an OpenAI SDK takes it (usually ending in /v1; no /chat/completions suffix). Used by Codex sessions.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The Anthropic-compatible base URL for the Messages API (the host root; the CLI appends /v1/messages). Used by Claude sessions; blank means the provider is OpenAI-only.</summary>
    public string AnthropicBaseUrl { get; set; } = "";

    /// <summary>The plain key in memory; settings.json holds it DPAPI-protected (<c>enc:</c> prefix), see <see cref="SecretProtector"/>.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The flash model: what a session starts with, and the everyday / background one.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string FlashModel { get; set; } = "";

    /// <summary>The most capable model, offered next to the flash model; may be blank.</summary>
    public string ProModel { get; set; } = "";

    public string? ModelForTier(string tier) =>
        tier == "pro" && !string.IsNullOrWhiteSpace(ProModel) ? ProModel.Trim() :
        !string.IsNullOrWhiteSpace(FlashModel) ? FlashModel.Trim() : null;

    /// <summary>Context windows discovered from each model's metadata, in tokens.</summary>
    public Dictionary<string, int> ModelContextWindows { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Context size in tokens assumed for models whose endpoint publishes no metadata.</summary>
    public const int DefaultContextWindow = 200000;
}
