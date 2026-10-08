using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// The session card shows real model names, a real default, and an ask mode that
/// reaches the model as an instruction but never the transcript the user reads back.
/// </summary>
public sealed class ModelCatalogueTests
{
    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-haiku-5-5", "Haiku")]
    [InlineData("claude-sonnet-5", "Sonnet 5")]
    [InlineData("claude-opus-5-5[1m]", "Opus 5.5 · 1M")]
    [InlineData("claude-sonnet-5-5-20260101", "Sonnet 5.5")]
    [InlineData("opus", "Opus")]
    public void Model_ids_read_as_names(string id, string expected) =>
        Assert.Equal(expected, ClaudeAgentSession.FriendlyModelName(id));

    [Fact]
    public void Sonnet_at_medium_is_the_documented_default()
    {
        var model = Assert.Single(ClaudeAgentSession.DocumentedModels, m => m.IsDefault);

        Assert.Equal("claude-sonnet-5-5", model.Id);
        Assert.Equal("medium", model.DefaultEffort);
    }

    [Fact]
    public void Sessions_start_automatic_and_foreign_modes_fall_back_to_it()
    {
        Assert.Equal("auto", new ClaudeSessionOptions { WorkingDirectory = "." }.PermissionMode);
        Assert.Equal("auto", ClaudeSessionOptions.ClampPermissionMode("on-request"));
    }

    [Fact]
    public void Ask_turns_carry_the_instruction_and_history_strips_it()
    {
        var turn = new UserTurn("How does the parser work?") { AskOnly = true };

        Assert.StartsWith(UserTurn.AskInstruction, turn.ProviderText);
        Assert.Equal("How does the parser work?", UserTurn.StripHostInstructions(turn.ProviderText));
        Assert.Equal("plain", new UserTurn("plain").ProviderText);
    }

    [Fact]
    public void An_older_ask_instruction_is_stripped_too()
    {
        const string recorded = "[Ask mode] Answer this with a detailed explanation only. Older wording.\r\n\r\nwhy is it slow?";

        Assert.Equal("why is it slow?", UserTurn.StripHostInstructions(recorded));
        Assert.Equal("[Ask mode] alone", UserTurn.StripHostInstructions("[Ask mode] alone"));
    }
}
