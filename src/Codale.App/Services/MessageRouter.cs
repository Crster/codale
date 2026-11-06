using Codale.App.ViewModels;
using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.App.Services;

/// <summary>Automatic mode classification. It never selects or changes a history session.</summary>
public sealed class MessageRouter
{
    private readonly IHelperModel _client;

    public MessageRouter(IHelperModel client)
    {
        _client = client;
    }

    /// <summary>True when an agent CLI is installed, so Automatic mode can do more than pass through.</summary>
    public bool IsAvailable => _client.IsAvailable;

    /// <summary>The user is typing a message this router will read: get the model ready.</summary>
    public void Prewarm(FeatureService? features)
    {
        _client.Prewarm(MessageRouting.SystemPrompt, [MessageRouting.RouteTool]);
    }

    public async Task<RouteDecision> RouteAsync(string text, ChatViewModel chat, CancellationToken ct = default)
    {
        return await MessageRouting.RouteAsync(_client, text, chat.LastRoutingReply, ct);
    }
}
