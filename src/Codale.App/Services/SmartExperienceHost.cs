using Codale.Smart;

namespace Codale.App.Services;

/// <summary>One independent native model service per app process.</summary>
public static class SmartExperienceHost
{
    public static SmartExperience Instance { get; } = new(
        Path.Combine(McpServerSettings.CodaleDataRoot(), "smart", "models"),
        Path.Combine(AppContext.BaseDirectory, "SmartNative"));
    private static bool _started, _enabled;
    public static void Start()
    {
        if (_started) return;
        _started = true;
        AppSettings.Changed += (_, _) => Apply();
        Apply();
    }
    private static void Apply()
    {
        var enabled = AppSettings.SmartExperience;
        if (enabled == _enabled) return;
        _enabled = enabled;
        _ = Instance.SetEnabledAsync(enabled);
    }
    public static void Cancel() { AppSettings.SmartExperience = false; }
    public static void Retry() { if (AppSettings.SmartExperience) _ = Instance.SetEnabledAsync(true); }
}
