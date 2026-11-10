using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Xaml;

namespace Codale.App.ViewModels;

/// <summary>
/// Stands in for every turn older than the latest two. It holds a count and nothing else - no row,
/// text or tool call of the collapsed turns stays in memory - and Load reads them back from the
/// chat's history log as ordinary You / Codale entries.
/// </summary>
public sealed partial class EarlierTurnsItem : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Notice;

    protected override Thickness NaturalMargin => new(0, 4, 40, 6);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial int Turns { get; set; }

    /// <summary>True when the chat has a history log to read the turns back from.</summary>
    [ObservableProperty]
    public partial bool CanLoad { get; set; }

    public string Title => Turns == 1 ? "1 earlier turn" : $"{Turns} earlier turns";

    /// <summary>Prompts among <paramref name="rows"/>; steers join the turn they interrupted.</summary>
    public static int CountTurns(IEnumerable<ChatItem> rows) => rows.Count(row => row is UserMessageItem { IsSteer: false });
}
