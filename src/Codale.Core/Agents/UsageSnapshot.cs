namespace Codale.Core.Agents;

/// <summary>Token accounting for a single message or a whole turn.</summary>
public readonly record struct UsageSnapshot
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadInputTokens { get; init; }
    public int CacheCreationInputTokens { get; init; }
    public int ThinkingTokens { get; init; }

    /// <summary>
    /// What actually occupies the context window: fresh input plus everything read
    /// from or written to the cache. Cache reads are cheap, but they still take up context.
    /// </summary>
    public int ContextTokens => InputTokens + CacheReadInputTokens + CacheCreationInputTokens;

    public int TotalTokens => ContextTokens + OutputTokens;
}
