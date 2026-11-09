using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Codale.Agents.Claude;
using Codale.Agents.OpenAi;
using Codale.Core.Agents;
using Codale.Core.Helper;

namespace Codale.Agents.OpenRouter;

/// <summary>A model OpenRouter lists, with the request features it supports.</summary>
public sealed record OpenRouterModel(string Id, string Name, IReadOnlySet<string> SupportedParameters)
{
    public bool CallsTools => SupportedParameters.Contains("tools");
    public bool StructuredOutputs => SupportedParameters.Contains("structured_outputs") || SupportedParameters.Contains("response_format");
    public bool Reasons => SupportedParameters.Contains("reasoning");
}

/// <summary>One chat request to <c>/chat/completions</c>.</summary>
/// <param name="Format">A JSON schema the reply must match (structured output), or null.</param>
/// <param name="Reasoning">True or false to switch reasoning, or null to send nothing.</param>
internal sealed record OpenRouterChatRequest(
    string Model,
    string SystemPrompt,
    string Prompt,
    int MaxTokens,
    IReadOnlyList<ToolDefinition>? Tools = null,
    Action<Utf8JsonWriter>? Format = null,
    bool? Reasoning = null,
    double Temperature = 0);

/// <summary>
/// OpenRouter's OpenAI-compatible API: chat with tools and structured output, embeddings, and the model
/// lists. Hosted, so a request needs the account's key and answers in a second or two instead of the
/// minutes a local model takes on a modest machine.
/// </summary>
public sealed class OpenRouterClient
{
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    /// <summary>A request that has not answered in this long is hung, not slow.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The model list by server: it changes rarely and is large.</summary>
    private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<OpenRouterModel>>> ModelCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public OpenRouterClient(string? apiKey, string? baseUrl = null, HttpClient? http = null)
    {
        _apiKey = (apiKey ?? "").Trim();
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
        _http = http ?? SharedHttp;
    }

    public string BaseUrl { get; }

    public bool HasKey => _apiKey.Length > 0;

    /// <summary>Chat models, by id. Needs no key. Cached; a failed lookup is not, so it is tried again.</summary>
    public Task<IReadOnlyList<OpenRouterModel>> ListModelsAsync(CancellationToken ct = default) =>
        CachedModelsAsync("/models", ct);

    /// <summary>Embedding models, by id.</summary>
    public Task<IReadOnlyList<OpenRouterModel>> ListEmbeddingModelsAsync(CancellationToken ct = default) =>
        CachedModelsAsync("/embeddings/models", ct);

    public static void ForgetModels() => ModelCache.Clear();

    private Task<IReadOnlyList<OpenRouterModel>> CachedModelsAsync(string path, CancellationToken ct)
    {
        var key = BaseUrl + path;
        var task = ModelCache.GetOrAdd(key, _ => FetchModelsAsync(path));
        if (task.IsFaulted || task.IsCanceled)
        {
            ModelCache.TryRemove(new KeyValuePair<string, Task<IReadOnlyList<OpenRouterModel>>>(key, task));
            task = ModelCache.GetOrAdd(key, _ => FetchModelsAsync(path));
        }

        return task.WaitAsync(ct);
    }

    private async Task<IReadOnlyList<OpenRouterModel>> FetchModelsAsync(string path)
    {
        using var reply = await SendAsync(HttpMethod.Get, path, null, CancellationToken.None, CancellationToken.None, DefaultRequestTimeout)
            .ConfigureAwait(false);
        return ReadModels(reply.RootElement);
    }

    internal static IReadOnlyList<OpenRouterModel> ReadModels(JsonElement root) =>
        root.Items("data")
            .Where(m => !string.IsNullOrWhiteSpace(m.Str("id")))
            .Select(m => new OpenRouterModel(
                m.Str("id")!,
                m.Str("name") ?? m.Str("id")!,
                m.StrArray("supported_parameters").ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The model's entry, or null when OpenRouter does not list it (or the list cannot be read).</summary>
    internal async Task<OpenRouterModel?> FindModelAsync(string model, CancellationToken ct)
    {
        try
        {
            var models = await ListModelsAsync(ct).ConfigureAwait(false);
            return models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
        }
        catch (HelperModelException)
        {
            return null;
        }
    }

    /// <summary>A chat turn: the reply with <c>choices[0].message</c>.</summary>
    internal async Task<JsonDocument> ChatAsync(OpenRouterChatRequest request, CancellationToken ct, Action<UsageSnapshot>? onUsage = null)
    {
        var reply = await PostAsync("/chat/completions", BuildChatRequest(request), ct).ConfigureAwait(false);
        onUsage?.Invoke(ReadUsage(reply.RootElement));
        return reply;
    }

    /// <summary>
    /// One fill-in-the-middle completion on the native <c>/completions</c> endpoint: the code before the
    /// caret goes in as <paramref name="prompt"/>, the code after it as <paramref name="suffix"/>, and the
    /// text for the gap comes back.
    /// </summary>
    internal async Task<string> FillAsync(string model, string prompt, string suffix, int maxTokens, double temperature, CancellationToken ct)
    {
        using var reply = await PostAsync("/completions", BuildCompletionRequest(model, prompt, suffix, maxTokens, temperature), ct)
            .ConfigureAwait(false);
        return reply.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            ? choices[0].Str("text") ?? ""
            : "";
    }

    /// <summary>The body of a <c>/completions</c> request. An empty suffix is left out: nothing follows the caret.</summary>
    internal static string BuildCompletionRequest(string model, string prompt, string suffix, int maxTokens, double temperature) =>
        OpenAiApiClient.WriteJson(json =>
        {
            json.WriteStartObject();
            json.WriteString("model", model);
            json.WriteString("prompt", prompt);
            if (suffix.Length > 0)
            {
                json.WriteString("suffix", suffix);
            }

            json.WriteNumber("temperature", temperature);
            json.WriteNumber("max_tokens", maxTokens);
            json.WriteEndObject();
        });

    /// <summary>One vector per input, in order.</summary>
    internal async Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> input, CancellationToken ct)
    {
        using var reply = await PostAsync("/embeddings", BuildEmbedRequest(model, input), ct).ConfigureAwait(false);
        var vectors = reply.RootElement.Items("data")
            .OrderBy(d => d.Int("index") ?? 0)
            .Select(d => ReadVector(d.Prop("embedding") ?? default))
            .ToList();
        if (vectors.Count != input.Count)
        {
            throw new HelperModelException($"OpenRouter returned {vectors.Count} embeddings for {input.Count} texts.");
        }

        return vectors;
    }

    internal static string BuildChatRequest(OpenRouterChatRequest request) => OpenAiApiClient.WriteJson(json =>
    {
        json.WriteStartObject();
        json.WriteString("model", request.Model);
        json.WriteBoolean("stream", false);
        json.WriteNumber("temperature", request.Temperature);
        json.WriteNumber("max_tokens", request.MaxTokens);
        if (request.Reasoning is { } reasoning)
        {
            json.WriteStartObject("reasoning");
            json.WriteBoolean("enabled", reasoning);
            json.WriteEndObject();
        }

        json.WriteStartArray("messages");
        json.WriteStartObject();
        json.WriteString("role", "system");
        json.WriteString("content", request.SystemPrompt);
        json.WriteEndObject();
        json.WriteStartObject();
        json.WriteString("role", "user");
        json.WriteString("content", request.Prompt);
        json.WriteEndObject();
        json.WriteEndArray();

        if (request.Tools is { Count: > 0 } tools)
        {
            json.WriteStartArray("tools");
            foreach (var tool in tools)
            {
                OpenAiApiClient.WriteTool(json, tool);
            }

            json.WriteEndArray();
        }

        if (request.Format is { } format)
        {
            json.WriteStartObject("response_format");
            json.WriteString("type", "json_schema");
            json.WriteStartObject("json_schema");
            json.WriteString("name", "tool_call");
            json.WritePropertyName("schema");
            format(json);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndObject();
    });

    internal static string BuildEmbedRequest(string model, IReadOnlyList<string> input) => OpenAiApiClient.WriteJson(json =>
    {
        json.WriteStartObject();
        json.WriteString("model", model);
        json.WriteStartArray("input");
        foreach (var text in input)
        {
            json.WriteStringValue(text);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    });

    internal static UsageSnapshot ReadUsage(JsonElement reply) => new()
    {
        InputTokens = reply.Prop("usage") is { } usage ? usage.Int("prompt_tokens") ?? 0 : 0,
        OutputTokens = reply.Prop("usage") is { } used ? used.Int("completion_tokens") ?? 0 : 0,
    };

    /// <summary>A vector, L2-normalised: retrieval scores are dot products of unit vectors, and not every model returns one.</summary>
    internal static float[] ReadVector(JsonElement values)
    {
        var vector = new float[values.ValueKind == JsonValueKind.Array ? values.GetArrayLength() : 0];
        var i = 0;
        if (values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                vector[i++] = value.ValueKind == JsonValueKind.Number ? value.GetSingle() : 0f;
            }
        }

        double norm = 0;
        foreach (var v in vector)
        {
            norm += v * v;
        }

        if (norm > 0)
        {
            var scale = (float)(1 / Math.Sqrt(norm));
            for (var j = 0; j < vector.Length; j++)
            {
                vector[j] *= scale;
            }
        }

        return vector;
    }

    /// <summary>The first choice's message, or null.</summary>
    internal static JsonElement? Message(JsonElement reply) =>
        reply.Items("choices").FirstOrDefault().Prop("message") is { ValueKind: JsonValueKind.Object } message ? message : null;

    /// <summary>
    /// The error text, with the upstream provider's own words (<c>error.metadata.raw</c>, and which provider)
    /// when OpenRouter only says "Provider returned error".
    /// </summary>
    internal static string DescribeError(string body)
    {
        var message = OpenAiApiClient.ErrorMessage(body);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.Prop("error") is { ValueKind: JsonValueKind.Object } error &&
                error.Prop("metadata") is { ValueKind: JsonValueKind.Object } metadata)
            {
                var raw = metadata.Prop("raw") is { ValueKind: JsonValueKind.String } r ? r.GetString() : null;
                var provider = metadata.Str("provider_name");
                var detail = string.IsNullOrWhiteSpace(raw) ? "" : $" {OpenAiApiClient.ErrorMessage(raw!)}";
                var from = string.IsNullOrWhiteSpace(provider) ? "" : $" ({provider})";
                return $"{message}{from}{detail}";
            }
        }
        catch (JsonException)
        {
        }

        return message;
    }

    private Task<JsonDocument> PostAsync(string path, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, body, ct, ct, DefaultRequestTimeout);

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, string? body, CancellationToken ct, CancellationToken callerToken, TimeSpan limit)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            // A 429 is the upstream provider's rate limit (common on shared and free models): it clears in
            // seconds, so wait and ask again before giving up.
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(method, new Uri(BaseUrl + path, UriKind.Absolute));
                if (_apiKey.Length > 0)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                }

                request.Headers.TryAddWithoutValidation("X-Title", "Codale");
                if (body is not null)
                {
                    request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
                }

                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return JsonDocument.Parse(text);
                }

                if ((int)response.StatusCode == 429 && attempt < 2)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                    await Task.Delay(wait > TimeSpan.FromSeconds(8) ? TimeSpan.FromSeconds(8) : wait, timeout.Token).ConfigureAwait(false);
                    continue;
                }

                throw new HelperModelException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "OpenRouter rejected the API key. Check it in Settings."
                    : $"OpenRouter returned {(int)response.StatusCode}: {DescribeError(text)}")
                {
                    StatusCode = (int)response.StatusCode,
                };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or ArgumentException or InvalidOperationException ||
                                   (ex is OperationCanceledException && !callerToken.IsCancellationRequested))
        {
            throw new HelperModelException(ex is HttpRequestException
                ? $"OpenRouter is not reachable: {ex.Message}"
                : $"The OpenRouter request failed: {ex.Message}", ex);
        }
    }
}
