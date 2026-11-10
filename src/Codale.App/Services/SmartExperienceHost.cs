using Codale.Smart;

namespace Codale.App.Services;

/// <summary>One independent native model service per app process.</summary>
public static class SmartExperienceHost
{
    public static SmartExperience Instance { get; } = new(
        Path.Combine(McpServerSettings.CodaleDataRoot(), "smart", "models"),
        Path.Combine(AppContext.BaseDirectory, "SmartNative"),
        Path.Combine(McpServerSettings.CodaleDataRoot(), "smart", "history"));
    private static bool _started, _enabled;
    private static string? _modelId;
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
        var enabled = AppSettings.SmartExperience;
        var model = SmartModels.Resolve(AppSettings.SmartModel);
        if (enabled == _enabled && model.Id == _modelId) return;
        _enabled = enabled;
        _modelId = model.Id;
        _ = Instance.ConfigureAsync(enabled, model);
    }
    public static void Cancel() { AppSettings.SmartExperience = false; }
    public static void Retry() { if (AppSettings.SmartExperience) _ = Instance.ConfigureAsync(true, SmartModels.Resolve(AppSettings.SmartModel)); }
}
