namespace Codale.Core.Features;

/// <summary>The provider conversation backing one explicit application history entry.</summary>
public sealed record ProviderSession(string SessionId, string Provider, string ProviderSessionId, string Worktree, string Endpoint);

public sealed record SourceReference(string Path, string Hash);
public sealed record KnowledgeHit(string Id, string SessionId, string Provider, string Worktree, string Revision,
    string Text, IReadOnlyList<SourceReference> References);
public sealed record SourceChunk(string Worktree, string Path, string Hash, int StartLine, string Text);
public sealed record RetrievalResult(string Text, IReadOnlyList<string> Keys, int Tokens, long ElapsedMilliseconds);
