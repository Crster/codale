namespace Codale.Core.Features;

/// <summary>The provider conversation backing one explicit application history entry.</summary>
public sealed record ProviderSession(string SessionId, string Provider, string ProviderSessionId, string Worktree, string Endpoint);

public sealed record SourceChunk(string Worktree, string Path, string Hash, int StartLine, string Text, long Id = 0, string Symbols = "");
