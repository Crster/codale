using Codale.App.ViewModels;
using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.App.Services;

/// <summary>Automatic mode classification. It never selects or changes a history session.</summary>
public sealed class MessageRouter
{
    // A warm CLI answers in ~1s and a cold Claude one in ~3.5s; past this the user has waited enough.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

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
        if (MessageRouting.TryRouteLocally(text) is { } local)
        {
            CrashLog.Trace("Routed locally: go-ahead");
            return local;
        }

        if (!IsAvailable)
        {
            return MessageRouting.RouteWithoutModel(text);
        }

        var conversation = MessageRouting.BuildConversation(text, lastAssistantText: null);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var call = await _client.CallToolAsync(
                MessageRouting.SystemPrompt,
                conversation,
                [MessageRouting.RouteTool],
                timeout.Token);

            var decision = MessageRouting.Interpret(call, text, canMoveTopic: false);
            CrashLog.Trace(
                $"Routed in {started.ElapsedMilliseconds}ms: intent={decision.Intent} newTopic={decision.NewTopic}");
            return decision;
        }
        catch (Exception ex)
        {
            // No CLI, a failed call, a slow reply: the message goes out as typed.
            CrashLog.Trace($"Routing skipped after {started.ElapsedMilliseconds}ms: {ex.Message}");
            return MessageRouting.RouteWithoutModel(text);
        }
    }
}
