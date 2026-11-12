using Codale.Smart;

namespace Codale.App.Services;

/// <summary>Routes each Smart task to its independently configured provider.</summary>
public static class SmartExperienceHost
{
    public static SmartExperience Instance { get; } = new(
        Path.Combine(McpServerSettings.CodaleDataRoot(), "smart", "history"));
    private static bool _started;
    private static string? _configuration;
    public static SmartProviderClient CreateClient(string feature)
    {
        var provider = AppSettings.SmartProviderFor(feature);
        var key = AppSettings.SmartApiProfileFor(feature);
        var profile = provider is "claude" or "codex"
            ? SmartFeatureSettings.ResolveAccount(AppSettings.ByokProviders, provider, key, AppSettings.AccountLabel(provider))
            : AppSettings.FindByok(key);
        return new(provider, profile, SmartFeatureSettings.Features.ToDictionary(f => f.Id, f => AppSettings.SmartFeatureModel(f.Id)));
    }
    public static void Start()
    {
        if (_started) return;
        _started = true;
        AppSettings.Changed += (_, _) => Apply();
        Apply();
    }
    private static void Apply()
    {
        Instance.Thinking = AppSettings.SmartThinking;
        var router = new SmartFeatureRouter(SmartFeatureSettings.Groups.ToDictionary(g => g.Id, g => CreateClient(g.Id)));
        if (router.ConfigurationKey == _configuration) return;
        _configuration = router.ConfigurationKey;
        _ = Instance.ConfigureRemoteAsync("Smart experience", router.HasGeneration ? router.CompleteAsync : null,
            featureEnabled: router.IsFeatureEnabled);
    }
    public static void Retry() { _configuration = null; Apply(); }
}
