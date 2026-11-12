namespace Codale.App.Services;

public static class SmartFeatureSettings
{
    /// <summary>Resolve legacy default choices without losing a detected account's credential home.</summary>
    public static ByokProvider? ResolveAccount(IEnumerable<ByokProvider> profiles, string cli, string? key, string? defaultEmail)
    {
        var accounts = profiles.Where(p => p.IsAccount && p.Supports(cli)).ToArray();
        if (!string.IsNullOrWhiteSpace(key))
            return accounts.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        return accounts.FirstOrDefault(p => p.AccountId.Length == 0)
            ?? accounts.FirstOrDefault(p => !string.IsNullOrWhiteSpace(defaultEmail) && string.Equals(p.Email, defaultEmail, StringComparison.OrdinalIgnoreCase))
            ?? (accounts.Length == 1 ? accounts[0] : null);
    }

    public static readonly (string Id, string Label, string Description)[] Groups =
    [
        ("assistance", "Assistance", "Commit messages and shell commands"),
        ("completion", "Autocomplete", "Suggestions as you type"),
        ("context", "Project context", "Background summaries and search"),
        ("embedding", "Embeddings", "Semantic search · reopen project to apply"),
    ];

    public static string GroupFor(string feature) => feature switch
    {
        "commit" or "shell" or "assess" or "routing" or "brief" => "assistance",
        "summary" or "ranking" or "context" => "context",
        _ => feature,
    };

    public static readonly (string Id, string Label)[] Features =
    [
        ("commit", "Commit messages"), ("shell", "Run shell parser"), ("assess", "Command risk assessment"),
        ("completion", "Code autocomplete"), ("summary", "File summaries"), ("routing", "Message routing"),
        ("context", "Context exploration"), ("ranking", "Search ranking"), ("brief", "Agent briefs"),
        ("embedding", "Embeddings"),
    ];
}
