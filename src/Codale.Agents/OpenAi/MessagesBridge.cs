using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Codale.Agents.Claude;

namespace Codale.Agents.OpenAi;

/// <summary>
/// The OpenAI-compatible provider a Claude session talks to through the bridge: where to
/// send, with which key, and the model names to answer Claude's own model ids with.
/// </summary>
public sealed record OpenAiRoute(string BaseUrl, string ApiKey, string LiteModel, string SmartModel);

/// <summary>
/// A loopback Anthropic Messages API in front of OpenAI-compatible providers, so the Claude
/// CLI - which only speaks the Messages API - can use them. Each <see cref="Register"/>ed
/// route gets its own token; the CLI sends it as its API key, and the bridge sends the
/// provider's real key, which never reaches the CLI's environment.
/// </summary>
/// <remarks>
/// Serves <c>POST /v1/messages</c> (streamed or not) and <c>POST /v1/messages/count_tokens</c>
/// (an estimate). Started on first use, on a free port of 127.0.0.1, for the life of the app.
/// </remarks>
public sealed class MessagesBridge : IDisposable
{
    private static readonly Lazy<MessagesBridge> SharedBridge = new(() => new MessagesBridge());

    /// <summary>The app's bridge.</summary>
    public static MessagesBridge Shared => SharedBridge.Value;

    // No overall timeout: a streamed reply lasts as long as the model writes. A client
    // that gives up cancels through the request's token instead.
    private static readonly HttpClient Upstream = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, OpenAiRoute> _routes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private HttpListener? _listener;
    private string _baseUrl = "";

    /// <summary>
    /// The bridge's base URL and the token that selects <paramref name="route"/>, starting the
    /// bridge if it is not running. The same route always gets the same token.
    /// </summary>
    /// <exception cref="InvalidOperationException">No loopback port could be opened.</exception>
    public (string BaseUrl, string Token) Register(OpenAiRoute route)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
            EnsureStarted();

            var token = _routes.FirstOrDefault(pair => pair.Value == route).Key;
            if (token is null)
            {
                token = "codale-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
                _routes[token] = route;
            }

            return (_baseUrl, token);
        }
    }

    private void EnsureStarted()
    {
        if (_listener is not null)
        {
            return;
        }

        HttpListenerException? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                // Taken between the probe and the bind: try another.
                last = ex;
                listener.Close();
                continue;
            }

            _listener = listener;
            _baseUrl = $"http://127.0.0.1:{port}";
            _ = Task.Run(() => AcceptLoopAsync(listener));
            return;
        }

        throw new InvalidOperationException($"The OpenAI bridge could not open a local port: {last?.Message}", last);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            if (Route(context.Request) is not { } route)
            {
                await WriteErrorAsync(response, 401, "Unknown Codale bridge token; restart the session.").ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (context.Request.HttpMethod != "POST" || (path != "/v1/messages" && path != "/v1/messages/count_tokens"))
            {
                await WriteErrorAsync(response, 404, $"The Codale bridge does not serve {context.Request.HttpMethod} {path}.").ConfigureAwait(false);
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(_stopping.Token).ConfigureAwait(false);
            }

            JsonDocument request;
            try
            {
                request = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                await WriteErrorAsync(response, 400, $"The request is not JSON: {ex.Message}").ConfigureAwait(false);
                return;
            }

            using (request)
            {
                if (path.EndsWith("/count_tokens", StringComparison.Ordinal))
                {
                    var count = MessagesTranslator.EstimateTokens(request.RootElement);
                    await WriteJsonAsync(response, 200, $"{{\"input_tokens\":{count}}}").ConfigureAwait(false);
                    return;
                }

                await ForwardAsync(request.RootElement, route, response).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The CLI hung up or the app is closing: nobody is left to answer.
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"OpenAI bridge request failed: {ex}");
            try
            {
                await WriteErrorAsync(response, 500, $"The Codale bridge failed: {ex.Message}").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The response had already begun.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // Already closed by a hang-up.
            }
        }
    }

    /// <summary>The route the request's token selects: the CLI sends it as <c>x-api-key</c>, or as a bearer token.</summary>
    private OpenAiRoute? Route(HttpListenerRequest request)
    {
        var token = request.Headers["x-api-key"];
        if (string.IsNullOrEmpty(token) &&
            request.Headers["Authorization"] is { } authorization &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authorization["Bearer ".Length..].Trim();
        }

        return !string.IsNullOrEmpty(token) && _routes.TryGetValue(token, out var route) ? route : null;
    }

    /// <summary>
    /// The model to ask the provider for: Claude's own ids (the CLI's defaults and background
    /// calls) map to the route's smart model for opus and its default model otherwise.
    /// </summary>
    internal static string ProviderModel(string requested, OpenAiRoute route)
    {
        var smart = route.SmartModel.Length > 0 ? route.SmartModel : route.LiteModel;
        var normal = route.LiteModel.Length > 0 ? route.LiteModel : route.SmartModel;
        if (requested.Length == 0 || requested.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ||
            requested is "opus" or "sonnet" or "haiku" or "fable")
        {
            var isSmart = requested.Contains("opus", StringComparison.OrdinalIgnoreCase) ||
                requested.Contains("fable", StringComparison.OrdinalIgnoreCase);
            var mapped = isSmart ? smart : normal;
            return mapped.Length > 0 ? mapped : requested;
        }

        return requested;
    }

    private async Task ForwardAsync(JsonElement request, OpenAiRoute route, HttpListenerResponse response)
    {
        var requestedModel = request.Str("model") ?? "";
        var model = ProviderModel(requestedModel, route);
        var stream = request.Bool("stream");

        Uri url;
        try
        {
            url = new Uri(OpenAiApiClient.ChatCompletionsUrl(route.BaseUrl), UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            await WriteErrorAsync(response, 400, $"The provider's base URL is not a URL: {route.BaseUrl}").ConfigureAwait(false);
            return;
        }

        if (route.ApiKey.Length > 0 && !OpenAiApiClient.IsSafeToSendKey(url))
        {
            await WriteErrorAsync(response, 400,
                $"Refusing to send an API key to {url.Scheme}://{url.Authority}: use an https URL, or a local server.").ConfigureAwait(false);
            return;
        }

        var omit = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; ; attempt++)
        {
            using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(MessagesTranslator.ToChatRequest(request, model, omit), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            if (route.ApiKey.Length > 0)
            {
                upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.ApiKey);
            }

            HttpResponseMessage upstream;
            try
            {
                upstream = await Upstream.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, _stopping.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                await WriteErrorAsync(response, 502, $"Could not reach {url.Authority}: {ex.Message}").ConfigureAwait(false);
                return;
            }

            using (upstream)
            {
                if (!upstream.IsSuccessStatusCode)
                {
                    var error = OpenAiApiClient.ErrorMessage(await upstream.Content.ReadAsStringAsync(_stopping.Token).ConfigureAwait(false));
                    var status = (int)upstream.StatusCode;

                    // A provider that rejects an optional field by name gets one more try without it.
                    var named = MessagesTranslator.FieldsNamedIn(error).Where(f => !omit.Contains(f)).ToList();
                    if (attempt == 0 && status is 400 or 422 && named.Count > 0)
                    {
                        omit.UnionWith(named);
                        continue;
                    }

                    await WriteErrorAsync(response, status, $"{url.Authority} returned {status}: {error}").ConfigureAwait(false);
                    return;
                }

                var isEventStream = upstream.Content.Headers.ContentType?.MediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;
                if (stream)
                {
                    await RelayStreamAsync(upstream, isEventStream, requestedModel, response).ConfigureAwait(false);
                }
                else
                {
                    var text = await upstream.Content.ReadAsStringAsync(_stopping.Token).ConfigureAwait(false);
                    using var reply = JsonDocument.Parse(text);
                    await WriteJsonAsync(response, 200, MessagesTranslator.ToMessagesResponse(reply.RootElement, requestedModel)).ConfigureAwait(false);
                }

                return;
            }
        }
    }

    private async Task RelayStreamAsync(HttpResponseMessage upstream, bool isEventStream, string model, HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.SendChunked = true;
        response.Headers["Cache-Control"] = "no-cache";
        var output = response.OutputStream;
        var translator = new MessagesStream(model);

        async Task SendAsync(string events)
        {
            if (events.Length > 0)
            {
                await output.WriteAsync(Encoding.UTF8.GetBytes(events), _stopping.Token).ConfigureAwait(false);
                await output.FlushAsync(_stopping.Token).ConfigureAwait(false);
            }
        }

        await SendAsync(translator.Start()).ConfigureAwait(false);
        try
        {
            if (!isEventStream)
            {
                // Asked to stream, the provider answered whole: replay it as one chunk.
                using var whole = JsonDocument.Parse(await upstream.Content.ReadAsStringAsync(_stopping.Token).ConfigureAwait(false));
                await SendAsync(translator.Feed(AsChunk(whole.RootElement))).ConfigureAwait(false);
            }
            else
            {
                using var reader = new StreamReader(await upstream.Content.ReadAsStreamAsync(_stopping.Token).ConfigureAwait(false), Encoding.UTF8);
                while (await reader.ReadLineAsync(_stopping.Token).ConfigureAwait(false) is { } line)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var data = line["data:".Length..].Trim();
                    if (data == "[DONE]")
                    {
                        break;
                    }

                    if (data.Length > 0)
                    {
                        await SendAsync(translator.Feed(data)).ConfigureAwait(false);
                    }
                }
            }

            await SendAsync(translator.Finish()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MessagesStreamException or JsonException || (ex is IOException or HttpRequestException && !_stopping.IsCancellationRequested))
        {
            // The status line is gone already: the failure travels as an error event.
            await SendAsync(MessagesStream.Error($"The provider's stream failed: {ex.Message}")).ConfigureAwait(false);
        }
    }

    /// <summary>A whole Chat Completions reply in the shape of a stream chunk: its message as the delta.</summary>
    internal static string AsChunk(JsonElement reply)
    {
        var choice = reply.Prop("choices") is { ValueKind: JsonValueKind.Array } choices && choices.GetArrayLength() > 0 ? choices[0] : default;
        return OpenAiApiClient.WriteJson(json =>
        {
            json.WriteStartObject();
            json.WriteStartArray("choices");
            json.WriteStartObject();
            if (choice.Prop("message") is { ValueKind: JsonValueKind.Object } message)
            {
                json.WritePropertyName("delta");
                message.WriteTo(json);
            }

            if (choice.Str("finish_reason") is { } finish)
            {
                json.WriteString("finish_reason", finish);
            }

            json.WriteEndObject();
            json.WriteEndArray();
            if (reply.Prop("usage") is { ValueKind: JsonValueKind.Object } usage)
            {
                json.WritePropertyName("usage");
                usage.WriteTo(json);
            }

            json.WriteEndObject();
        });
    }

    private static Task WriteErrorAsync(HttpListenerResponse response, int status, string message) =>
        WriteJsonAsync(response, status, MessagesTranslator.ToAnthropicError(status, message));

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            _stopping.Cancel();
            _listener?.Close();
            _listener = null;
        }
    }
}
