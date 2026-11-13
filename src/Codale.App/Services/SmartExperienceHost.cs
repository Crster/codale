using Codale.Smart;

namespace Codale.App.Services;

/// <summary>Routes each Smart task to the model role configured for it.</summary>
public static class SmartExperienceHost
{
    public static SmartExperience Instance { get; } = new(
        Path.Combine(McpServerSettings.CodaleDataRoot(), "smart", "history"));
    private static bool _started;
    private static string? _configuration;
    private static SmartFeatureRouter? _router;
    public static SmartStatusTracker Status { get; } = new();

    /// <summary>A client for one model role (General, Auto Complete, Embedding or Decision) as currently configured.</summary>
    public static SmartProviderClient CreateClient(string role)
    {
        var provider = AppSettings.SmartProviderFor(role);
        var key = AppSettings.SmartApiProfileFor(role);
        var profile = provider is "claude" or "codex"
            ? SmartFeatureSettings.ResolveAccount(AppSettings.ByokProviders, provider, key, AppSettings.AccountLabel(provider))
            : AppSettings.FindByok(key);
        return new(role, provider, profile, AppSettings.SmartRoleModel(role));
    }

    public static SmartFeatureRouter CreateRouter() =>
        new(SmartFeatureSettings.Roles.ToDictionary(r => r.Id, r => CreateClient(r.Id)));

    /// <summary>The client that writes a task's answer right now, or null when it is off (classification may still go to <see cref="Decider"/>).</summary>
    public static SmartProviderClient? ClientFor(string task) => (_router ?? CreateRouter()).ClientFor(task);

    /// <summary>The configured decision model, or null when the Decision role is off.</summary>
    public static SmartProviderClient? Decider => (_router ?? CreateRouter()).Decider;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        Instance.ActivityUpdated += activity =>
        {
            if (activity.State is "completed" or "cancelled" or "error")
                Status.Finish(activity.Id, activity.State, activity.Error);
            else Status.Start(activity.Id, activity.Context.Task, activity.StartedAt);
        };
        AppSettings.Changed += (_, _) => Apply();
        Apply();
    }

    private static void Apply()
    {
        Instance.Thinking = AppSettings.SmartThinking;
        var router = CreateRouter();
        _router = router;
        if (router.ConfigurationKey == _configuration) return;
        _configuration = router.ConfigurationKey;
        Status.Configure(SmartFeatureSettings.Roles.Select(r =>
        {
            var client = CreateClient(r.Id);
            return (r.Id, client.IsEnabled, client.IsEnabled ? client.ConfigurationError : null);
        }));
        _ = Instance.ConfigureRemoteAsync("Smart experience", router.HasGeneration ? router.CompleteAsync : null,
            featureEnabled: router.IsFeatureEnabled, laneFor: router.LaneFor);
    }

    public static void Retry() { _configuration = null; Apply(); }
}
