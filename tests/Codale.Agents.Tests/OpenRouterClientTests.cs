using System.Text.Json;

using Codale.Agents.OpenRouter;
using Codale.Core.Helper;

namespace Codale.Agents.Tests;

public sealed class OpenRouterClientTests
{
    private static readonly ToolDefinition Pick = new()
    {
        Name = "pick",
        Description = "Pick one.",
        Parameters = [new ToolParameter { Name = "choice", Description = "The pick.", Required = true, AllowedValues = ["a", "b"] }],
    };

    [Fact]
    public void A_chat_request_is_unstreamed_and_sends_reasoning_only_when_asked()
    {
        using var plain = JsonDocument.Parse(OpenRouterClient.BuildChatRequest(new OpenRouterChatRequest("m", "sys", "hi", 64)));
        var root = plain.RootElement;
        Assert.Equal("m", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(64, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());

        using var off = JsonDocument.Parse(OpenRouterClient.BuildChatRequest(new OpenRouterChatRequest("m", "s", "p", 8, Reasoning: false)));
        Assert.False(off.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Tools_and_structured_output_are_written_in_the_openai_shape()
    {
        using var doc = JsonDocument.Parse(OpenRouterClient.BuildChatRequest(new OpenRouterChatRequest(
            "m", "s", "p", 8, Tools: [Pick], Format: json => OpenRouterHelperModel.WriteCallSchema(json, [Pick]))));
        var root = doc.RootElement;
        Assert.Equal("pick", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("json_schema", root.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public void Tool_calls_are_read_from_string_arguments()
    {
        using var doc = JsonDocument.Parse("""
            {"choices":[{"message":{"content":null,"tool_calls":[{"function":{"name":"pick","arguments":"{\"choice\":\"a\"}"}}]}}]}
            """);
        var calls = OpenRouterHelperModel.ReadCalls(doc.RootElement, [Pick]);
        Assert.Single(calls);
        Assert.Equal("pick", calls[0].Tool);
    }

    [Fact]
    public void A_json_reply_without_tool_calls_is_read_as_one_call()
    {
        using var doc = JsonDocument.Parse("""
            {"choices":[{"message":{"content":"{\"tool\":\"pick\",\"arguments\":{\"choice\":\"b\"}}"}}]}
            """);
        Assert.Single(OpenRouterHelperModel.ReadCalls(doc.RootElement, [Pick]));
    }

    [Fact]
    public void Models_are_read_with_their_supported_parameters()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[{"id":"b/two","name":"Two","supported_parameters":["tools","reasoning"]},{"id":"a/one","supported_parameters":[]}]}
            """);
        var models = OpenRouterClient.ReadModels(doc.RootElement);
        Assert.Equal(["a/one", "b/two"], models.Select(m => m.Id));
        Assert.True(models[1].CallsTools);
        Assert.True(models[1].Reasons);
        Assert.False(models[0].CallsTools);
    }

    [Fact]
    public void Usage_is_read_from_the_openai_fields()
    {
        using var doc = JsonDocument.Parse("""{"usage":{"prompt_tokens":12,"completion_tokens":3}}""");
        var usage = OpenRouterClient.ReadUsage(doc.RootElement);
        Assert.Equal(12, usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
    }

    [Fact]
    public void Embeddings_are_normalised_to_unit_length()
    {
        using var doc = JsonDocument.Parse("[3,4]");
        var vector = OpenRouterClient.ReadVector(doc.RootElement);
        Assert.Equal(0.6f, vector[0], 3);
        Assert.Equal(0.8f, vector[1], 3);
    }

    [Fact]
    public void A_helper_without_a_key_is_not_available()
    {
        var helper = new OpenRouterHelperModel(new OpenRouterClient(""), "m", false);
        Assert.False(helper.IsAvailable);
        Assert.True(new OpenRouterHelperModel(new OpenRouterClient("k"), "m", false).IsAvailable);
    }
}
