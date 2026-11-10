namespace Codale.Core.Features;

/// <summary>The provider conversation backing one explicit application history entry.</summary>
public sealed record ProviderSession(string SessionId, string Provider, string ProviderSessionId, string Worktree, string Endpoint);

public sealed record SourceReference(string Path, string Hash);
public sealed record KnowledgeHit(string Id, string SessionId, string Provider, string Worktree, string Revision,
    string Text, IReadOnlyList<SourceReference> References);
public sealed record SourceChunk(string Worktree, string Path, string Hash, int StartLine, string Text, long Id = 0);

/// <summary>
/// The context a prompt was enriched with. <see cref="Tokens"/> is the UTF-8 byte count, a conservative
/// upper bound on tokens. <see cref="Files"/> and <see cref="Findings"/> say what it carried, for the chat entry.
/// </summary>
public sealed record RetrievalResult(string Text, IReadOnlyList<string> Keys, int Tokens, long ElapsedMilliseconds)
{
    public IReadOnlyList<string> Files { get; init; } = [];
    public int Snippets { get; init; }
    public int Findings { get; init; }
    public string Method { get; init; } = "";
    public string? SmartActivityId { get; init; }

    /// <summary>The prompt changed the subject, so this was retrieved for it alone.</summary>
    public bool NewTopic { get; init; }
}
