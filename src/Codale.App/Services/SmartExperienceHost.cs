using Codale.Smart;

namespace Codale.App.Services;

/// <summary>Routes each Smart task to the model role configured for it.</summary>
public sealed class SmartExperienceHost(AppSettingsStore settings) : IAsyncDisposable, ISmartTaskHost
{
    public AppSettingsStore Settings => settings;
    public SmartExperience Instance { get; } = new(
        Path.Combine(settings.DataFolder, "smart", "history"));
    private bool _started;
    private string? _configuration;
    private SmartFeatureRouter? _router;
    public SmartStatusTracker Status { get; } = new();

    /// <summary>A client for one model role (General, Auto Complete, Embedding or Decision) as currently configured.</summary>
    public SmartProviderClient CreateClient(string role)
    {
        var provider = settings.SmartProviderFor(role);
        var key = settings.SmartApiProfileFor(role);
        var profile = provider is "claude" or "codex"
            ? SmartFeatureSettings.ResolveAccount(settings.ByokProviders, provider, key, settings.AccountLabel(provider))
            : settings.FindByok(key);
        return new(role, provider, profile, settings.SmartRoleModel(role));
    }

    public SmartFeatureRouter CreateRouter() =>
        new(SmartFeatureSettings.Roles.ToDictionary(r => r.Id, r => (ISmartProviderClient)CreateClient(r.Id)));

    /// <summary>The client that writes a task's answer right now, or null when it is off (classification may still go to <see cref="Decider"/>).</summary>
    public ISmartProviderClient? ClientFor(string task) => (_router ?? CreateRouter()).ClientFor(task);

    /// <summary>The configured decision model, or null when the Decision role is off.</summary>
    public ISmartProviderClient? Decider => (_router ?? CreateRouter()).Decider;

    bool ISmartTaskHost.IsFeatureEnabled(string task) => settings.SmartFeatureEnabled(task);

    bool ISmartTaskHost.IsReady => Instance.Status.State == SmartState.Ready;

    Task<SmartResult<T>?> ISmartTaskHost.GenerateAsync<T>(SmartRequestContext context, string system, string input, int cap,
        Func<string, T?> parse, bool background, CancellationToken ct) where T : class =>
        Instance.GenerateAsync(context, system, input, cap, parse, background, ct);

    public void Start()
    {
        if (_started) return;
        _started = true;
        Instance.ActivityUpdated += OnActivityUpdated;
        settings.Changed += OnSettingsChanged;
        Apply();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Apply();

    private void OnActivityUpdated(SmartActivity activity)
    {
        if (activity.State is "completed" or "cancelled" or "error")
            Status.Finish(activity.Id, activity.State, activity.Error);
        else Status.Start(activity.Id, activity.Context.Task, activity.StartedAt);
    }

    private void Apply()
    {
        Instance.Thinking = settings.SmartThinking;
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

    public void Retry() { _configuration = null; Apply(); }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            settings.Changed -= OnSettingsChanged;
            Instance.ActivityUpdated -= OnActivityUpdated;
            _started = false;
        }
        await Instance.DisposeAsync();
    }
}