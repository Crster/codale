namespace Codale.App.Services;

public static class SmartFeatureSettings
{
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
