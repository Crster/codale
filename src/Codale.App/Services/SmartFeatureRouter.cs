namespace Codale.App.Services;

public sealed class SmartFeatureRouter(IReadOnlyDictionary<string, SmartProviderClient> clients)
{
    public string ConfigurationKey => string.Join("|", clients.OrderBy(p => p.Key).Select(p => p.Value.ConfigurationKey));
    public bool HasGeneration => SmartFeatureSettings.Features.Any(f => f.Id != "embedding" && IsFeatureEnabled(f.Id));
    public bool IsFeatureEnabled(string feature) => clients.TryGetValue(SmartFeatureSettings.GroupFor(feature), out var client) && client.IsFeatureEnabled(feature);
    public Task<string> CompleteAsync(string feature, string system, string input, int tokens, CancellationToken ct) =>
        clients[SmartFeatureSettings.GroupFor(feature)].CompleteAsync(feature, system, input, tokens, ct);
}
