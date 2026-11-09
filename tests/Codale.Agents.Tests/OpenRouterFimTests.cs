using System.Net;
using System.Text;
using System.Text.Json;

using Codale.Agents.OpenRouter;
using Codale.Core.Helper;

namespace Codale.Agents.Tests;

public sealed class OpenRouterFimTests
{
    private const string Codestral = "mistralai/codestral-2501";

    /// <summary>Answers each request by its path; records what was asked.</summary>
    private sealed class Route(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            Seen.Add((path, body));
            return respond(path);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OpenRouterClient Client(Route route) =>
        new("key", "https://openrouter.ai/api/v1", new HttpClient(route));

    [Fact]
    public void The_native_request_carries_the_prompt_and_the_suffix()
    {
        using var json = JsonDocument.Parse(OpenRouterClient.BuildCompletionRequest(Codestral, "def f():\n    ", "\n    return 1", 64, 0.2));
        var root = json.RootElement;

        Assert.Equal(Codestral, root.GetProperty("model").GetString());
        Assert.Equal("def f():\n    ", root.GetProperty("prompt").GetString());
        Assert.Equal("\n    return 1", root.GetProperty("suffix").GetString());
        Assert.Equal(64, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("messages", out _));
    }

    [Fact]
    public void An_empty_suffix_is_left_out_of_the_request()
    {
        using var json = JsonDocument.Parse(OpenRouterClient.BuildCompletionRequest(Codestral, "x = ", "", 64, 0));

        Assert.False(json.RootElement.TryGetProperty("suffix", out _));
    }

    [Fact]
    public async Task Fill_posts_to_completions_and_returns_the_text_for_the_gap()
    {
        var route = new Route(_ => Json(HttpStatusCode.OK, """{"choices":[{"text":"total / len(numbers)"}]}"""));

        var text = await Client(route).FillAsync(Codestral, "return ", "\n", 64, 0.2, CancellationToken.None);

        Assert.Equal("total / len(numbers)", text);
        Assert.Equal("/api/v1/completions", route.Seen.Single().Path);
    }

    [Fact]
    public async Task A_model_without_an_infill_route_gets_its_infill_tokens_over_chat()
    {
        var route = new Route(path => path == "/api/v1/completions"
            ? Json(HttpStatusCode.NotFound, """{"error":{"message":"no infill route"}}""")
            : Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"total"}}]}"""));
        var completer = new OpenRouterFimCompleter(Client(route), Codestral);

        var line = await completer.CompleteAsync("return ", ";", null);

        Assert.Equal("total", line);
        var chat = route.Seen.Last();
        Assert.Equal("/api/v1/chat/completions", chat.Path);
        Assert.Contains("[PREFIX]return [SUFFIX];[MIDDLE]", chat.Body);
    }

    [Fact]
    public async Task A_refusal_from_a_model_with_no_known_infill_tokens_is_not_retried_over_chat()
    {
        var route = new Route(_ => Json(HttpStatusCode.NotFound, """{"error":{"message":"no route"}}"""));
        var completer = new OpenRouterFimCompleter(Client(route), "some/chat-model");

        var error = await Assert.ThrowsAsync<HelperModelException>(() => completer.CompleteAsync("x", "", null));

        Assert.Equal(404, error.StatusCode);
        Assert.Single(route.Seen);
    }

    [Theory]
    [InlineData("mistralai/codestral-2501", "[PREFIX]a[SUFFIX]b[MIDDLE]")]
    [InlineData("qwen/qwen-2.5-coder-32b-instruct", "<|fim_prefix|>a<|fim_suffix|>b<|fim_middle|>")]
    [InlineData("bigcode/starcoder2-15b", "<fim_prefix>a<fim_suffix>b<fim_middle>")]
    public void Each_family_gets_its_own_infill_tokens(string model, string expected) =>
        Assert.Equal(expected, FimTokens.Wrap(model, "a", "b"));

    [Fact]
    public void A_family_without_known_tokens_has_none()
    {
        Assert.Null(FimTokens.Wrap("openai/gpt-4o", "a", "b"));
    }
}
