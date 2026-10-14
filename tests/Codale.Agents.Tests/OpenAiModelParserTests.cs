using System.Text.Json;

using Codale.Agents.OpenAi;

namespace Codale.Agents.Tests;

public sealed class OpenAiModelParserTests
{
    private static List<(string Id, int? ContextWindow)> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return OpenAiModelParser.ReadModels(doc.RootElement);
    }

    [Fact]
    public void Parses_bare_array_of_string_ids()
    {
        var models = Parse("""["gpt-4o", "claude-3-5-sonnet", "o1"]""");

        Assert.Equal(3, models.Count);
        Assert.Equal("claude-3-5-sonnet", models[0].Id);
        Assert.Null(models[0].ContextWindow);
        Assert.Equal("gpt-4o", models[1].Id);
        Assert.Null(models[1].ContextWindow);
        Assert.Equal("o1", models[2].Id);
        Assert.Null(models[2].ContextWindow);
    }

    [Fact]
    public void Parses_data_envelope_with_id_and_context_window()
    {
        var models = Parse("""
        {
            "data": [
                { "id": "gpt-4o", "context_window": 128000 },
                { "id": "o1", "context_window": 200000 }
            ]
        }
        """);

        Assert.Equal(2, models.Count);
        Assert.Equal("gpt-4o", models[0].Id);
        Assert.Equal(128000, models[0].ContextWindow);
        Assert.Equal("o1", models[1].Id);
        Assert.Equal(200000, models[1].ContextWindow);
    }

    [Fact]
    public void Parses_models_envelope_with_max_tokens()
    {
        var models = Parse("""
        {
            "models": [
                { "id": "anthropic/claude-3.5-sonnet", "max_tokens": 200000 }
            ]
        }
        """);

        Assert.Single(models);
        Assert.Equal("anthropic/claude-3.5-sonnet", models[0].Id);
        Assert.Equal(200000, models[0].ContextWindow);
    }

    [Fact]
    public void Parses_result_envelope_with_context_length()
    {
        var models = Parse("""
        {
            "result": [
                { "name": "qwen2.5-coder", "context_length": 32768 }
            ]
        }
        """);

        Assert.Single(models);
        Assert.Equal("qwen2.5-coder", models[0].Id);
        Assert.Equal(32768, models[0].ContextWindow);
    }

    [Fact]
    public void Parses_data_data_nested_envelope_with_slug_and_max_input_tokens()
    {
        var models = Parse("""
        {
            "data": {
                "data": [
                    { "slug": "deepseek-v3", "max_input_tokens": 64000 }
                ]
            }
        }
        """);

        Assert.Single(models);
        Assert.Equal("deepseek-v3", models[0].Id);
        Assert.Equal(64000, models[0].ContextWindow);
    }

    [Fact]
    public void Context_metadata_priority_order_is_respected()
    {
        var models = Parse("""
        {
            "data": [
                {
                    "id": "model-1",
                    "context_length": 1000,
                    "context_window": 2000,
                    "max_tokens": 3000
                },
                {
                    "id": "model-2",
                    "context_window": 2000,
                    "max_tokens": 3000
                },
                {
                    "id": "model-3",
                    "max_tokens": 3000,
                    "max_input_tokens": 4000
                },
                {
                    "id": "model-4",
                    "max_input_tokens": 4000,
                    "max_completion_tokens": 5000
                },
                {
                    "id": "model-5",
                    "max_completion_tokens": 5000,
                    "max_output_tokens": 6000
                },
                {
                    "id": "model-6",
                    "max_output_tokens": 6000
                }
            ]
        }
        """);

        Assert.Equal(6, models.Count);
        Assert.Equal(1000, models.First(m => m.Id == "model-1").ContextWindow);
        Assert.Equal(2000, models.First(m => m.Id == "model-2").ContextWindow);
        Assert.Equal(3000, models.First(m => m.Id == "model-3").ContextWindow);
        Assert.Equal(4000, models.First(m => m.Id == "model-4").ContextWindow);
        Assert.Equal(5000, models.First(m => m.Id == "model-5").ContextWindow);
        Assert.Equal(6000, models.First(m => m.Id == "model-6").ContextWindow);
    }

    [Fact]
    public void Id_resolution_priority_order_is_respected()
    {
        var models = Parse("""
        {
            "data": [
                { "id": "by-id", "slug": "by-slug", "model": "by-model", "name": "by-name" },
                { "slug": "by-slug-2", "model": "by-model-2", "name": "by-name-2" },
                { "model": "by-model-3", "name": "by-name-3" },
                { "name": "by-name-4" }
            ]
        }
        """);

        Assert.Equal(4, models.Count);
        Assert.Contains(models, m => m.Id == "by-id");
        Assert.Contains(models, m => m.Id == "by-slug-2");
        Assert.Contains(models, m => m.Id == "by-model-3");
        Assert.Contains(models, m => m.Id == "by-name-4");
    }

    [Fact]
    public void Parses_string_encoded_numbers()
    {
        var models = Parse("""
        {
            "data": [
                { "id": "ollama-model", "context_length": "131072" }
            ]
        }
        """);

        Assert.Single(models);
        Assert.Equal("ollama-model", models[0].Id);
        Assert.Equal(131072, models[0].ContextWindow);
    }

    [Fact]
    public void Deduplicates_case_insensitively_and_sorts()
    {
        var models = Parse("""
        {
            "data": [
                { "id": "Zeta", "context_window": 100 },
                { "id": "alpha", "context_window": 200 },
                { "id": "ZETA", "context_window": 999 }
            ]
        }
        """);

        Assert.Equal(2, models.Count);
        Assert.Equal("alpha", models[0].Id);
        Assert.Equal(200, models[0].ContextWindow);
        Assert.Equal("Zeta", models[1].Id);
        Assert.Equal(100, models[1].ContextWindow); // First entry preserved
    }

    [Fact]
    public void Filters_empty_and_whitespace_ids_and_non_positive_context_windows()
    {
        var models = Parse("""
        {
            "data": [
                { "id": "" },
                { "id": "   " },
                { "id": "zero-ctx", "context_window": 0 },
                { "id": "neg-ctx", "context_window": -100 },
                { "id": "valid", "context_window": 8192 }
            ]
        }
        """);

        Assert.Equal(3, models.Count);
        Assert.Equal("neg-ctx", models[0].Id);
        Assert.Null(models[0].ContextWindow);
        Assert.Equal("valid", models[1].Id);
        Assert.Equal(8192, models[1].ContextWindow);
        Assert.Equal("zero-ctx", models[2].Id);
        Assert.Null(models[2].ContextWindow);
    }

    [Fact]
    public void Returns_empty_for_missing_array_or_non_array_envelope()
    {
        Assert.Empty(Parse("""{}"""));
        Assert.Empty(Parse("""{"code": 401, "msg": "unauthorized"}"""));
        Assert.Empty(Parse("""{"data": "not an array"}"""));
    }
}
