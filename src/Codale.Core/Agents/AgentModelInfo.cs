namespace Codale.Core.Agents;

/// <summary>
/// One model a CLI offers, for the session card's picker. Claude only documents
/// aliases, so the list is static; the free-text picker stays open for ids we do not know.
/// </summary>
public sealed record AgentModelInfo
{
    /// <summary>What to pass to the CLI as the model: an alias or a full id.</summary>
    public required string Id { get; init; }

    public string DisplayName { get; init; } = "";

    public string? Description { get; init; }

    /// <summary>True when the CLI itself starts with this model.</summary>
    public bool IsDefault { get; init; }

    /// <summary>The effort the CLI picks for this model when none is forced.</summary>
    public string? DefaultEffort { get; init; }

    /// <summary>Efforts this model advertises, when the CLI knows them.</summary>
    public IReadOnlyList<string> SupportedEfforts { get; init; } = [];

    public override string ToString() => Description is { Length: > 0 } d ? $"{DisplayName} · {d}" : DisplayName;
}
