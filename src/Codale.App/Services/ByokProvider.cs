namespace Codale.App.Services;

/// <summary>
/// One bring-your-own-key provider as it appears in settings.json under
/// <c>byok.providers</c>: an Anthropic Messages API endpoint with its key and the model
/// names Claude should ask for. The status bar only picks between these.
/// </summary>
public sealed class ByokProvider
{
    /// <summary>The label shown in the status bar and the picker; unique across providers.</summary>
    public string Name { get; set; } = "";

    /// <summary>The Anthropic Messages API base URL (no /v1/messages suffix).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The plain key in memory; settings.json holds it DPAPI-protected (<c>enc:</c> prefix), see <see cref="SecretProtector"/>.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The model a session starts with, and the everyday / background one.</summary>
    public string Model { get; set; } = "";

    /// <summary>The most capable model, offered next to the default; may be blank.</summary>
    public string SmartModel { get; set; } = "";
}
