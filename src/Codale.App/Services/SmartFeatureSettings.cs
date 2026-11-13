namespace Codale.App.Services;

/// <summary>
/// Smart features are configured by model role, not by use: one text model (General), one fill-in-the-middle
/// model (Auto Complete), one embedding model (Embedding) and an optional fast model (Decision). Each task
/// names the roles it can run on, in order of preference.
/// </summary>
public static class SmartFeatureSettings
{
    public const string General = "general";
    public const string Completion = "completion";
    public const string Embedding = "embedding";
    public const string Decision = "decision";

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

    public static readonly (string Id, string Label, string Description, string Placeholder)[] Roles =
    [
        (General, "General", "Commit messages, session names, summaries", "Flash model"),
        (Completion, "Auto Complete", "Fill-in-the-middle for editor, composer and shell", "FIM model"),
        (Embedding, "Embedding", "Vectors for indexing and search", "Embedding model"),
        (Decision, "Decision", "Fast classifier for auto mode and command risk · uses General when off", "Decision model"),
    ];

    public static string LabelFor(string role) => Roles.FirstOrDefault(r => r.Id == role).Label ?? role;

    /// <summary>Roles that never fall back to the profile's Flash (chat) model: each needs a model of its own kind.</summary>
    public static bool RequiresDedicatedModel(string role) => role is Completion or Embedding or Decision;
    public static bool SupportsProvider(string role, string provider) => provider == "off"
        || !RequiresDedicatedModel(role) || provider == "openai";

    /// <summary>Older call sites and stored activity use a few aliases for the same task.</summary>
    public static string Normalize(string task) => task switch
    {
        "shell-command" => "shell",
        "assessment" or "command-confirmation" => "assess",
        "rank" => "ranking",
        _ => task,
    };

    /// <summary>Completion tasks insert text at a cursor; on a chat model they use an insertion prompt instead of raw FIM.</summary>
    public static bool IsCompletionTask(string task) => Normalize(task) is "completion" or "compose-completion" or "shell-completion";

    /// <summary>
    /// The roles that can write a task's text, most preferred first. Decision is never one: a decision model
    /// (OpenRouter's System One models) only answers choice, yes/no and score questions; see <see cref="IsDecisionTask"/>.
    /// </summary>
    public static IReadOnlyList<string> RolesFor(string task) => Normalize(task) switch
    {
        "completion" => [Completion],
        // Ghost text needs an answer within a second: a chat model is too slow (measured 3-12 s on General).
        "compose-completion" or "shell-completion" => [Completion],
        "embedding" => [Embedding],
        _ => [General],
    };

    /// <summary>Tasks that are a classification: the Decision model answers them when set, General's chat prompts otherwise.</summary>
    public static bool IsDecisionTask(string task) => Normalize(task) is "routing" or "assess";

    /// <summary>The activity name of a request to the Decision model.</summary>
    public const string DecideTask = "decide";

    /// <summary>Background work reads code; every other task answers the person who is waiting on it.</summary>
    public static bool IsBackgroundTask(string task) => Normalize(task) is "summary" or "context" or "embedding" or "keywords";

    public static bool IsProvider(string provider) => provider is "claude" or "codex" or "openai" or "anthropic";

    public const string MigratedKey = "ai.smart.rolesMigrated";

    /// <summary>
    /// One-time move from the old Smart groups (Developer Tools, Codebase Context, Code Completion, Search Embeddings)
    /// to model roles: General takes Developer Tools' provider, profiles and models (Codebase Context's when Developer
    /// Tools was off); Auto Complete and Embedding keep their keys; Decision starts off. False when already done.
    /// </summary>
    public static bool MigrateLegacyGroups(IDictionary<string, string> values)
    {
        if (values.TryGetValue(MigratedKey, out var done) && done == "1") return false;
        string Provider(string group) => values.TryGetValue($"ai.smart.groups.{group}.provider", out var raw) && IsProvider(raw) ? raw : "off";
        var source = Provider("assistance") != "off" ? "assistance" : Provider("context") != "off" ? "context" : null;
        if (source is not null && Provider(General) == "off")
        {
            foreach (var (key, value) in values.ToArray())
            {
                if (!key.StartsWith("ai.smart.", StringComparison.Ordinal)) continue;
                var moved = key.Replace($".groups.{source}.", $".groups.{General}.", StringComparison.Ordinal);
                if (moved != key) values[moved] = value;
            }
        }
        foreach (var key in values.Keys.Where(k => k == "ai.smartThinking" || k.StartsWith("ai.smart.", StringComparison.Ordinal)
            && (k.Contains(".groups.assistance.", StringComparison.Ordinal) || k.Contains(".groups.context.", StringComparison.Ordinal))).ToArray())
        {
            values.Remove(key);
        }
        foreach (var role in Roles)
            values.TryAdd($"ai.smart.groups.{role.Id}.provider", "off");
        values[MigratedKey] = "1";
        return true;
    }

    public static readonly (string Id, string Label)[] Features =
    [
        ("commit", "Commit messages"), ("title", "Session names"), ("summary", "File summaries"),
        ("context", "Context exploration"), ("ranking", "Search ranking"), ("brief", "Agent briefs"),
        ("shell", "Run shell parser"), ("keywords", "Search keywords"), ("assess", "Command risk assessment"),
        ("routing", "Auto mode routing"), ("completion", "Code autocomplete"), ("compose-completion", "Composer autocomplete"),
        ("shell-completion", "Shell autocomplete"), ("embedding", "Embeddings"),
    ];
}
