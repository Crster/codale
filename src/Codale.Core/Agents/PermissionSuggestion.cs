namespace Codale.Core.Agents;

/// <summary>
/// A shortcut the CLI offers alongside an approval request, e.g.
/// <c>{"type":"setMode","mode":"acceptEdits","destination":"session"}</c>.
/// Rendered as the extra buttons on the approval dialog.
/// </summary>
public sealed record PermissionSuggestion
{
    public required string Type { get; init; }
    public string? Mode { get; init; }
    public string? Destination { get; init; }

    public string ToDisplayLabel() => (Type, Mode, Destination) switch
    {
        ("setMode", "acceptEdits", "session") => "Allow all edits this session",
        ("setMode", var m, "session") => $"Switch session to {m}",
        ("setMode", var m, _) => $"Switch to {m}",
        _ => Type,
    };
}
