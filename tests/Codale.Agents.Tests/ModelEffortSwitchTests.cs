using System.Text.Json;

using Codale.Agents.Claude;

namespace Codale.Agents.Tests;

/// <summary>
/// Pins the frames that switch model and effort on a live session: Claude uses the
/// set_model and apply_flag_settings control requests.
/// </summary>
public sealed class ModelEffortSwitchTests
{
    private static JsonElement Parse(object frame) =>
        JsonDocument.Parse(ClaudeAgentSession.SerializeFrame(frame)).RootElement;

    [Fact]
    public async Task Claude_live_switch_declines_spawn_only_efforts()
    {
        // "max" exists only as a --effort spawn flag; apply_flag_settings' schema
        // accepts low/medium/high/xhigh, so the caller must restart instead.
        await using var session = new ClaudeAgentSession(new ClaudeSessionOptions
        {
            WorkingDirectory = ".",
        });

        Assert.False(await session.TrySetModelEffortAsync(null, "max"));
    }

    [Fact]
    public void Claude_set_model_names_the_model()
    {
        var root = Parse(ClaudeAgentSession.BuildSetModelFrame("req-1", "opus"));

        var request = root.GetProperty("request");
        Assert.Equal("set_model", request.GetProperty("subtype").GetString());
        Assert.Equal("opus", request.GetProperty("model").GetString());
    }

    [Fact]
    public void Claude_set_model_omits_the_field_to_reset_to_default()
    {
        var root = Parse(ClaudeAgentSession.BuildSetModelFrame("req-1", null));

        Assert.False(root.GetProperty("request").TryGetProperty("model", out _));
    }

    [Fact]
    public void Claude_apply_effort_sets_effortLevel()
    {
        var root = Parse(ClaudeAgentSession.BuildApplyEffortFrame("req-1", "xhigh"));

        var request = root.GetProperty("request");
        Assert.Equal("apply_flag_settings", request.GetProperty("subtype").GetString());
        Assert.Equal("xhigh", request.GetProperty("settings").GetProperty("effortLevel").GetString());
    }

    [Fact]
    public void Claude_catalog_parses_the_documented_spelling_and_renames()
    {
        var catalog = JsonDocument.Parse("""
            {
              "models": [
                {
                  "id": "claude-opus-5-5",
                  "displayName": "Opus 5.5",
                  "isDefault": true,
                  "defaultReasoningEffort": "medium",
                  "supportedReasoningEfforts": [
                    { "reasoningEffort": "low" },
                    { "reasoningEffort": "high" }
                  ]
                },
                { "model": "custom-proxy-model", "name": "Custom" }
              ]
            }
            """).RootElement;

        var models = ClaudeAgentSession.ParseModelCatalog(catalog);

        Assert.Equal(2, models.Count);

        var opus = models[0];
        Assert.Equal("claude-opus-5-5", opus.Id);
        Assert.Equal("Opus 5.5", opus.DisplayName);
        Assert.True(opus.IsDefault);
        Assert.Equal("medium", opus.DefaultEffort);
        Assert.Equal(["low", "high"], opus.SupportedEfforts);

        var custom = models[1];
        Assert.Equal("custom-proxy-model", custom.Id);
        Assert.Equal("Custom", custom.DisplayName);
    }

    [Fact]
    public void Claude_catalog_returns_empty_for_an_unrecognized_shape()
    {
        var catalog = JsonDocument.Parse("""{"unrelated": true}""").RootElement;

        Assert.Empty(ClaudeAgentSession.ParseModelCatalog(catalog));
    }
}
