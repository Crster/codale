namespace Codale.Core.Agents;

/// <summary>Codale's shared permission vocabulary, independent of any CLI protocol.</summary>
public static class AgentPermissionMode
{
    public const string Default = "auto";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "manual", "acceptEdits", "auto", "bypassPermissions", "dontAsk", "plan",
    };

    public static string Normalize(string? mode) =>
        mode is not null && Allowed.Contains(mode) ? mode : Default;
}
