using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// The session card shows real model names, a real default, and an ask mode that
/// reaches the model as an instruction but never the transcript the user reads back.
/// </summary>
public sealed class ModelCatalogueTests
{
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