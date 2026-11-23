using Codale.Agents.Providers;
using Codale.Core.Agents;
using Codale.Db.Search;
using Codale.Settings;
using Codale.Smart;

namespace Codale.Search.Tests;

/// <summary>
/// The user's configured Smart models for an eval run, built the way the app's SmartExperienceHost builds them,
/// from a private copy of the stored preferences.
/// </summary>
internal sealed class SmartModels : IAsyncDisposable
{
    private readonly AppSettingsStore _settings;
    private readonly SmartFeatureRouter _router;

    public SmartExperience Experience { get; }
    public IEmbedder? Embedder { get; }
    public string GeneralModel => _settings.SmartRoleModel(SmartFeatureSettings.General);

    private SmartModels(AppSettingsStore settings, string work, bool embed)
    {
        _settings = settings;
        _router = new(SmartFeatureSettings.Roles.ToDictionary(r => r.Id, r => (ISmartProviderClient)Client(r.Id)));
        Experience = new SmartExperience(Path.Combine(work, "smart-history"));
        var embedding = Client(SmartFeatureSettings.Embedding);
        if (embed && SmartEmbeddingService.UsesModel(embedding)) Embedder = new ModelEmbedder(embedding);
    }

    public static SmartModels? Open(string? settingsFolder, string work, bool embed)
    {
        if (string.IsNullOrWhiteSpace(settingsFolder)) return null;
        using var source = new ApplicationDatabase(settingsFolder);
        if (!File.Exists(Path.Combine(source.DatabasePath, "index__records.db"))) return null;
        var preferences = source.UseRecords(db => db.Open<Setting>().Find());
        var copy = Directory.CreateDirectory(Path.Combine(work, "settings")).FullName;
        using (var target = new ApplicationDatabase(copy)) target.UseRecords(db =>
        {
            db.Open<Setting>().PutMany(preferences.Select(row => new KeyValuePair<string, Setting>(row.Key, row.Value)));
            return true;
        });
        return new SmartModels(new AppSettingsStore(copy), work, embed);
    }

    public Task StartAsync() => Experience.ConfigureRemoteAsync("eval", _router.HasGeneration ? _router.CompleteAsync : null,
        featureEnabled: _router.IsFeatureEnabled, laneFor: _router.LaneFor);

    private SmartProviderClient Client(string role)
    {
        var provider = _settings.SmartProviderFor(role);
        var key = _settings.SmartApiProfileFor(role);
        var profile = provider is "claude" or "codex"
            ? SmartFeatureSettings.ResolveAccount(_settings.ByokProviders, provider, key, _settings.AccountLabel(provider))
            : _settings.FindByok(key);
        return new(role, provider, profile, _settings.SmartRoleModel(role));
    }

    public async ValueTask DisposeAsync()
    {
        await Experience.DisposeAsync();
        _settings.Dispose();
    }

    private sealed class ModelEmbedder(ISmartProviderClient client) : IEmbedder
    {
        private readonly SmartEmbeddingService _service = new(new NoStatus(), TimeSpan.FromSeconds(30));
        public string Model => SmartEmbeddingService.ModelOf(client);
        public float[] Embed(string text) => _service.Embed(client, text);
    }

    private sealed class NoStatus : ISmartRequestStatus
    {
        public void Start(string id, string task) { }
        public void Finish(string id, string outcome, string? error = null) { }
    }
}
