namespace Codale.Agents.Providers;

/// <summary>
/// One Smart model role as the host uses it: whether it is on, whether its setup is usable, how it identifies its
/// vectors, and the two requests the decider and embedder make. <see cref="SmartProviderClient"/> implements it;
/// tests use a fake.
/// </summary>
public interface ISmartProviderClient
{
    string Role { get; }

    /// <summary>Whether the role is switched on for a provider it supports.</summary>
    bool IsEnabled { get; }

    /// <summary>Why the role cannot run with its current setup, or null when it can.</summary>
    string? ConfigurationError { get; }

    /// <summary>The identity stored beside every vector; "off" when the role produces no vectors.</summary>
    string EmbeddingIdentity { get; }

    /// <summary>Identifies the provider, profile and model that answer, stored with a feature's results.</summary>
    string FeatureIdentity { get; }

    /// <summary>Identifies the setup for router caching: a change means the roles must be rebuilt.</summary>
    string ConfigurationKey { get; }

    /// <summary>One completion for a task (chat, FIM, or helper); the router calls it for the role that serves the task.</summary>
    Task<string> CompleteAsync(string task, string system, string input, int tokens, CancellationToken ct);

    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    Task<IReadOnlyDictionary<string, SmartDecision>> DecideAsync(object state,
        IReadOnlyDictionary<string, SmartQuestion> questions, CancellationToken ct);
}