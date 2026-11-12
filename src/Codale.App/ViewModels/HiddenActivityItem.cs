using Microsoft.UI.Xaml;

namespace Codale.App.ViewModels;

/// <summary>
/// Stands where a finished turn's tool activity was ("Worked for 1m 11s", its edits, commands, searches).
/// Old turns keep their prompt and answer on screen, but not the tool rows and their output, which are what
/// fills memory; this holds only the first tool id, a count and the summary. Expanding reads the activity
/// back from the workspace database's history and puts the block in its place.
/// </summary>
public sealed class HiddenActivityItem : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Tool;

    protected override Thickness NaturalMargin => new(0, 4, 40, 6);

    /// <summary>The first tool call of the turn: where its events begin in the history.</summary>
    public required string FirstToolUseId { get; init; }

    /// <summary>The tool calls the turn made that its block would show.</summary>
    public required int Calls { get; init; }

    /// <summary>The finished turn summary, retained without its tool rows or outputs.</summary>
    public string Summary { get; init; } = "Worked";

    public string Title => Calls == 1 ? "1 tool call" : $"{Calls} tool calls";
}
