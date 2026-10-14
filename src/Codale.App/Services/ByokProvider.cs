namespace Codale.App.Services;

/// <summary>
/// One bring-your-own-key provider as it appears in settings.json under
/// <c>byok.providers</c>: an OpenAI-compatible Chat Completions endpoint and an
/// Anthropic-compatible Messages endpoint sharing one key, plus the model names the
/// CLIs should ask for. Claude sessions use the Anthropic base URL directly; Codex
/// sessions use the OpenAI one. The status bar only picks between these.
/// </summary>
public sealed class ByokProvider
{
    /// <summary>The label shown in the status bar and the picker; unique across providers.</summary>
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
}
