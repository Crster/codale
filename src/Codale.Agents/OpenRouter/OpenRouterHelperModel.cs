using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Agents.OpenAi;
using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Text;

namespace Codale.Agents.OpenRouter;

/// <summary>
/// The helper model's jobs on one OpenRouter chat model. Tool calls use native tools when the model lists
/// them, else structured output, else a plain JSON reply the contract parser reads.
/// </summary>
/// <param name="reasoning">Whether a reasoning model thinks first; faster off.</param>
public sealed class OpenRouterHelperModel(OpenRouterClient client, string model, bool reasoning, Action<UsageSnapshot>? onUsage = null) : IHelperModel
{
    /// <summary>The reply cap for one-line jobs; longer write-ups pass their own.</summary>
    internal const int MaxTokens = 1024;

    /// <summary>Room for the reasoning pass on top of the answer's own cap.</summary>
    internal const int ReasoningBudget = 4096;

    public string Model => model;

    public bool IsAvailable => model.Length > 0 && client.HasKey;

    public Task<string> CompleteAsync(string systemPrompt, string prompt, CancellationToken ct = default) =>
        CompleteAsync(systemPrompt, prompt, MaxTokens, ct);

    public async Task<string> CompleteAsync(string systemPrompt, string prompt, int maxTokens, CancellationToken ct = default)
    {
        Require();
        var cap = reasoning ? maxTokens + ReasoningBudget : maxTokens;
        using var reply = await client.ChatAsync(
            new OpenRouterChatRequest(model, systemPrompt, prompt, cap, Reasoning: reasoning), ct, onUsage).ConfigureAwait(false);
        var text = ModelOutput.Clean(OpenRouterClient.Message(reply.RootElement)?.Str("content"));
        if (text.Length == 0)
        {
            throw new HelperModelException($"OpenRouter's {model} returned no text.");
        }

        return text;
    }

    public async Task<ToolCall?> CallToolAsync(
        string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default) =>
        (await CallAsync(systemPrompt, conversation, tools, ct).ConfigureAwait(false)).FirstOrDefault();

    public Task<IReadOnlyList<ToolCall>> CallToolsAsync(
        string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct = default) =>
        CallAsync(systemPrompt, conversation, tools, ct);

    private async Task<IReadOnlyList<ToolCall>> CallAsync(
        string systemPrompt, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        Require();
        var info = await client.FindModelAsync(model, ct).ConfigureAwait(false);
        var cap = reasoning ? MaxTokens + ReasoningBudget : MaxTokens;
        OpenRouterChatRequest request;
        if (info?.CallsTools == true)
        {
            request = new(model, systemPrompt, conversation, cap, Tools: tools, Reasoning: reasoning);
        }
        else
        {
            var contract = HelperToolCalls.BuildSystemPrompt(systemPrompt, tools);
            request = info?.StructuredOutputs == true
                ? new(model, contract, conversation, cap, Format: json => WriteCallSchema(json, tools), Reasoning: reasoning)
                : new(model, contract, conversation, cap, Reasoning: reasoning);
        }

        using var reply = await client.ChatAsync(request, ct, onUsage).ConfigureAwait(false);
        return ReadCalls(reply.RootElement, tools);
    }

    /// <summary>Every valid call in the reply, in order; else its text read as one call (a model that answered in prose or JSON).</summary>
    internal static IReadOnlyList<ToolCall> ReadCalls(JsonElement reply, IReadOnlyList<ToolDefinition> tools)
    {
        if (OpenRouterClient.Message(reply) is not { } message)
        {
            return [];
        }

        var calls = new List<ToolCall>();
        foreach (var toolCall in message.Items("tool_calls"))
        {
            if (toolCall.Prop("function") is not { ValueKind: JsonValueKind.Object } function || function.Str("name") is not { } name)
            {
                continue;
            }

            // The OpenAI shape sends arguments as a JSON string; an object is taken too.
            var arguments = function.Prop("arguments") switch
            {
                { ValueKind: JsonValueKind.Object } o => o.GetRawText(),
                { ValueKind: JsonValueKind.String } s when s.GetString() is { } raw && raw.TrimStart().StartsWith('{') => raw,
                _ => "{}",
            };
            if (HelperToolCalls.Parse($"{{\"tool\":\"{JsonEncodedText.Encode(name)}\",\"arguments\":{arguments}}}", tools) is { } call)
            {
                calls.Add(call);
            }
        }

        if (calls.Count > 0)
        {
            return calls;
        }

        return HelperToolCalls.Parse(message.Str("content"), tools) is { } single ? [single] : [];
    }

    /// <summary>
    /// The structured-output schema of the JSON contract <see cref="HelperToolCalls.Parse"/> reads: the tool's
    /// name from the list, and for a single tool its exact arguments.
    /// </summary>
    internal static void WriteCallSchema(Utf8JsonWriter json, IReadOnlyList<ToolDefinition> tools)
    {
        json.WriteStartObject();
        json.WriteString("type", "object");
        json.WriteStartObject("properties");
        json.WriteStartObject("tool");
        json.WriteString("type", "string");
        json.WriteStartArray("enum");
        foreach (var tool in tools)
        {
            json.WriteStringValue(tool.Name);
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.WritePropertyName("arguments");
        if (tools.Count == 1)
        {
            OpenAiApiClient.WriteParameters(json, tools[0]);
        }
        else
        {
            json.WriteStartObject();
            json.WriteString("type", "object");
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteStartArray("required");
        json.WriteStringValue("tool");
        json.WriteStringValue("arguments");
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private void Require()
    {
        if (model.Length == 0)
        {
            throw new HelperModelException("No OpenRouter model is set for background tasks.");
        }

        if (!client.HasKey)
        {
            throw new HelperModelException("No OpenRouter API key is set for background tasks.");
        }
    }
}
