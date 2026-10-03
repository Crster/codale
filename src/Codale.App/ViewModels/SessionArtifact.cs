using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

public enum SessionArtifactKind
{
    /// <summary>A plan the agent proposed through ExitPlanMode: it lives only in the conversation.</summary>
    Plan,

    /// <summary>A plan document the agent wrote to disk (a markdown file under a plans folder).</summary>
    PlanFile,

    /// <summary>An image a tool returned, e.g. a browser screenshot, saved under the app data folder.</summary>
    Image,

    /// <summary>A file the agent wrote outside the project or of a document type (report, pdf, csv...).</summary>
    File,
}

public enum SessionArtifactStatus
{
    Proposed,
    Accepted,
    Revised,
    Saved,
}

/// <summary>
/// Something the agent produced this session that outlives the turn it came from: plans,
/// and the images and documents it registered. The session panel lists them so a plan
/// agreed three turns ago is one click away instead of a scroll through folded timelines.
/// </summary>
public sealed partial class SessionArtifact : ObservableObject
{
    public required SessionArtifactKind Kind { get; init; }

    /// <summary>The on-disk file for a plan document; null for a conversation-only plan.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The tool call that produced it, so later answers find it again.</summary>
    public string? ToolUseId { get; init; }

    [ObservableProperty]
    public partial string Title { get; set; } = "Plan";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Markdown { get; set; } = "";

    [ObservableProperty]
    public partial DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial SessionArtifactStatus Status { get; set; }

    public string StatusText => Status switch
    {
        SessionArtifactStatus.Accepted => "accepted",
        SessionArtifactStatus.Revised => "revised",
        SessionArtifactStatus.Saved => "saved",
        _ => "proposed",
    };

    /// <summary>A plan proposed in chat is titled by its first line (the heading), the way the old panel did.</summary>
    partial void OnMarkdownChanged(string value)
    {
        if (Kind != SessionArtifactKind.Plan)
        {
            return;
        }

        var headline = ToolCallItem.PlanHeadline(value);
        Title = headline is { Length: > 0 }
            ? headline.Length > 80 ? headline[..80] + "…" : headline
            : "Plan";
    }

    public bool IsFile => Kind == SessionArtifactKind.PlanFile;

    public bool IsImage => Kind == SessionArtifactKind.Image;

    /// <summary>Segoe Fluent glyph for the panel row.</summary>
    public string Glyph => Kind switch
    {
        SessionArtifactKind.Image => "",
        SessionArtifactKind.File => "",
        _ => "",
    };

    public string Subtitle => Kind == SessionArtifactKind.Plan
        ? "Proposed in chat"
        : SourcePath is { } path ? Path.GetFileName(path) : "";

    /// <summary>A markdown file under a folder named "plans" (.claude/plans, docs/plans...) reads as a plan document.</summary>
    public static bool IsPlanPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
               normalized.Split('/').Any(part => part.Equals("plans", StringComparison.OrdinalIgnoreCase));
    }
}
