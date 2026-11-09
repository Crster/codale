using Codale.App.ViewModels;
using Codale.Core.Background;
using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.App.Services;

/// <summary>Automatic mode classification. It never selects or changes a history session.</summary>
public sealed class MessageRouter
{
    private readonly IHelperModel _client;
    private readonly Func<IDecisionModel?>? _decider;
    private readonly Func<TimeSpan>? _timeout;

    /// <param name="client">The chat model: it routes when there is no decision model, or when that one fails.</param>
    /// <param name="decider">The native decision model Settings picks, read afresh for every message.</param>
    /// <param name="timeout">How long the chat model may take before the message is routed without it.</param>
    public MessageRouter(IHelperModel client, Func<IDecisionModel?>? decider = null, Func<TimeSpan>? timeout = null)
    {
        _client = client;
        _decider = decider;
        _timeout = timeout;
    }

    /// <summary>True when a background model is set up, so Automatic mode can do more than pass through.</summary>
    public bool IsAvailable => _client.IsAvailable || _decider?.Invoke() is not null;

    /// <summary>The user is typing a message this router will read: get the model ready.</summary>
    public void Prewarm(FeatureService? features)
    {
        _client.Prewarm(MessageRouting.SystemPrompt, [MessageRouting.RouteTool]);
    }

    public async Task<RouteDecision> RouteAsync(string text, ChatViewModel chat, CancellationToken ct = default)
    {
        return await MessageRouting.RouteAsync(_decider?.Invoke(), _client, text, chat.LastRoutingReply, ct, _timeout?.Invoke());
    }
}
