using Codale.Db.Search;

namespace Codale.App.Services;

/// <summary>
/// The Embedding role for the app: the configured vector model, or Codale's local hashed embedder while it is off.
/// The model id is stored beside every vector, so changing the setting re-embeds lazily without reopening the project.
/// </summary>
public sealed class SmartEmbedder : IEmbedder, IDisposable
{
    private readonly SmartExperienceHost _smart;
    private readonly SmartEmbeddingService _service;
    private volatile SmartProviderClient _client;

    public SmartEmbedder(SmartExperienceHost smart)
    {
        _smart = smart;
        _service = new SmartEmbeddingService(new SmartStatusSink(smart.Status));
        _client = smart.CreateClient(SmartFeatureSettings.Embedding);
        _smart.Settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        _client = _smart.CreateClient(SmartFeatureSettings.Embedding);

    public void Dispose() => _smart.Settings.Changed -= OnSettingsChanged;

    /// <summary>Whether a vector model (not the local hashed embedder) produces the vectors.</summary>
    public bool UsesModel => SmartEmbeddingService.UsesModel(_client);

    public string Model => SmartEmbeddingService.ModelOf(_client);

    public float[] Embed(string text) => _service.Embed(_client, text);
}