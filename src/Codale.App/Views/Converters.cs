using Codale.App.ViewModels;
using Codale.Core.Agents;
using Codale.Core.Shell;
using Codale.Git;

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Codale.App.Views;

public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed partial class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Collapsed;
}

public sealed partial class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// ToggleButton.IsChecked - a nullable bool - to visibility, for the sidebar's
/// collapsible section headers: an expanded header shows the section under it.
/// </summary>
public sealed partial class NullableBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>The inverse: visible only when there is nothing, for "no diff" placeholders.</summary>
public sealed partial class NullToInverseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is null or "" ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Segoe Fluent glyph for the editor tab's state: preview (\uE8FF), untitled new
/// file (\uF56E), edited (\uE8A5), saved (\uE729).
/// </summary>
public sealed partial class TabStateToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        "preview" => "\uE8FF",
        "new" => "\uF56E",
        "edited" => "\uE8A5",
        _ => "\uE729",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Orange icon for an edited (unsaved) tab; every other state keeps the default foreground.</summary>
public sealed partial class TabStateToBrushConverter : IValueConverter
{
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush Orange =
        new(Windows.UI.Color.FromArgb(255, 255, 140, 0));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is "edited" ? Orange : Microsoft.UI.Xaml.DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Preview tabs show their name in italics, the way a preview is marked.</summary>
public sealed partial class PreviewToFontStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// The transcript palette (Themes/ChatPalette.xaml), resolved once per key. Converters
/// cannot use ThemeResource, so they read the app-level dictionary; a missing key -
/// a typo, or a resource dictionary that failed to merge - falls back to grey rather
/// than throwing mid-layout.
/// </summary>
internal static class Palette
{
    private static readonly Dictionary<string, Brush> Cache = [];

    public static Brush Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var brush = Application.Current?.Resources.TryGetValue(key, out var found) == true && found is Brush b
            ? b
            : new SolidColorBrush(Colors.Gray);

        Cache[key] = brush;
        return brush;
    }

    public static Brush Kind(ToolKind kind) => Get($"Kind{kind}Brush");

    public static Brush KindTint(ToolKind kind) => Get($"Kind{kind}TintBrush");
}

/// <summary>
/// Segoe Fluent glyph for what a tool call is - a tool name or a <see cref="ToolKind"/>:
/// page for reads, magnifier for searches, pen for edits, new page for writes, prompt
/// for shell lines, globe for the web.
/// </summary>
public sealed partial class ToolKindGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Glyph(value switch
        {
            ToolKind kind => kind,
            string name => ToolKinds.Of(name),
            _ => ToolKind.Other,
        });

    public static string Glyph(ToolKind kind) => kind switch
    {
        ToolKind.Read => "\uE8A5",      // page
        ToolKind.Search => "\uE721",    // magnifier
        ToolKind.Edit => "\uE70F",      // pen
        ToolKind.Write => "\uE8E5",     // open file / new page
        ToolKind.Shell => "\uE756",     // command prompt
        ToolKind.Web => "\uE774",       // globe
        ToolKind.Agent => "\uE99A",     // robot
        ToolKind.Ask => "\uE9CE",       // question bubble
        ToolKind.Plan => "\uE8FD",      // bulleted list
        ToolKind.Todo => "\uE9D5",      // checklist
        _ => "\uE90F",                  // gear
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>A tool kind's hue: the verb, the rail dot and the icon on its disc.</summary>
public sealed partial class ToolKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Palette.Kind(value is ToolKind kind ? kind : ToolKind.Other);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>The same hue at ~14%: the icon disc and pill backgrounds.</summary>
public sealed partial class ToolKindTintConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Palette.KindTint(value is ToolKind kind ? kind : ToolKind.Other);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class ToolStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        ToolCallStatus.Succeeded => Palette.Get("StatusSuccessBrush"),
        ToolCallStatus.Failed => Palette.Get("StatusFailedBrush"),
        ToolCallStatus.Denied or ToolCallStatus.AwaitingApproval => Palette.Get("StatusWarnBrush"),
        _ => Palette.Get("StatusRunningBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Safety rating hue for a shell command: green for Safe, amber for Caution, red for Danger.</summary>
public sealed partial class ShellRiskBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        ShellRisk.Danger => Palette.Get("StatusFailedBrush"),
        ShellRisk.Caution => Palette.Get("StatusWarnBrush"),
        _ => Palette.Get("StatusSuccessBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>A session file change's hue: created green, edited amber, deleted red.</summary>
public sealed partial class FileChangeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        FileChangeKind.Create => Palette.Get("KindWriteBrush"),
        FileChangeKind.Delete => Palette.Get("StatusFailedBrush"),
        _ => Palette.Get("KindEditBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>A plan's status pill colour: accepted green, revised amber, the rest indigo.</summary>
public sealed partial class ArtifactStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        SessionArtifactStatus.Accepted => Palette.Get("StatusSuccessBrush"),
        SessionArtifactStatus.Revised or SessionArtifactStatus.Pending => Palette.Get("StatusWarnBrush"),
        SessionArtifactStatus.Saved => Palette.Get("KindReadBrush"),
        _ => Palette.Get("KindPlanBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Accent when true, a neutral stroke when false: the selected option row's border.</summary>
public sealed partial class SelectedStrokeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Palette.Get("KindAskBrush") : Palette.Get("DetailStrokeBrush");

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Tinted when true, clear when false: the selected option row's fill.</summary>
public sealed partial class SelectedFillConverter : IValueConverter
{
    private static readonly SolidColorBrush Transparent = new(Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Palette.Get("OptionSelectedBrush") : Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>A progress dot: lit for the question on screen, dim for the rest.</summary>
public sealed partial class DotBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Palette.Get("KindAskBrush") : Palette.Get("TimelineRailBrush");

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Collapsed for 0/false/null/"", visible otherwise - one converter for "has anything".</summary>
public sealed partial class AnyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        null or false or "" or 0 => Visibility.Collapsed,
        System.Collections.ICollection { Count: 0 } => Visibility.Collapsed,
        _ => Visibility.Visible,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>A todo's text: finished items step back so what is left reads first.</summary>
public sealed partial class TodoTextBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is TodoStatus.Completed ? Palette.Get("TodoDoneTextBrush") : Palette.Get("TodoTextBrush");

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Segoe Fluent icon for a collapsible row: chevron down when open, chevron right when shut.</summary>
public sealed partial class BoolToChevronConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "\uE70D" : "\uE76C";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Colour for a turn header's outcome mark, in the transcript's palette: green for
/// work that landed, red for failures, grey for turns that were stopped short.
/// </summary>
public sealed partial class TurnOutcomeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        TurnOutcome.Completed => Palette.Get("StatusSuccessBrush"),
        TurnOutcome.Stopped => Palette.Get("StatusMutedBrush"),
        TurnOutcome.Failed or TurnOutcome.NotSent => Palette.Get("StatusFailedBrush"),
        _ => Palette.Get("StatusRunningBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class TurnOutcomeTintConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        TurnOutcome.Completed => Palette.Get("StatusSuccessTintBrush"),
        TurnOutcome.Stopped => Palette.Get("PillBackgroundBrush"),
        TurnOutcome.Failed or TurnOutcome.NotSent => Palette.Get("StatusFailedTintBrush"),
        _ => Palette.Get("StatusRunningTintBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class TimestampTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DateTimeOffset dto ? dto.ToLocalTime().ToString("t") : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class NoticeSeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        NoticeSeverity.Error => Palette.Get("StatusFailedBrush"),
        NoticeSeverity.Warning => Palette.Get("StatusWarnBrush"),
        _ => Palette.Get("StatusMutedBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class FileChangeGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        FileChangeKind.Create => "",
        FileChangeKind.Delete => "",
        FileChangeKind.Update => "",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>A file path as a decoded BitmapImage, for attachment chip thumbnails; null when it cannot be shown.</summary>
public sealed partial class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string { Length: > 0 } path || !File.Exists(path)) return null;

        // These render as ~32px chips; decoding at 64px keeps a 4K paste from materialising
        // as a ~33MB bitmap per chip. DecodePixelWidth must be set before UriSource.
        // A throw here escapes through XAML binding as an unhandled exception and takes the app down.
        try
        {
            return new BitmapImage { DecodePixelWidth = 64, UriSource = new Uri(path) };
        }
        catch (Exception ex)
        {
            CrashLog.Error("chat", $"attachment thumbnail failed for {path}", ex);
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Segoe Fluent icon for an attachment's kind.</summary>
public sealed partial class AttachmentGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        TurnAttachmentKind.Image => "",
        TurnAttachmentKind.Pdf => "",
        TurnAttachmentKind.Text => "",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Segoe Fluent icon for a composer suggestion row: command prompt for slash commands, page for files.</summary>
public sealed partial class SuggestionGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>File name only, for chips and transcript rows.</summary>
public sealed partial class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string path ? Path.GetFileName(path) : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class TodoGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        TodoStatus.Completed => "",
        TodoStatus.InProgress => "",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class TodoBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        TodoStatus.Completed => Palette.Get("StatusSuccessBrush"),
        TodoStatus.InProgress => Palette.Get("StatusRunningBrush"),
        _ => Palette.Get("StatusMutedBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Tints an added or removed diff line, faintly enough to stay readable on Mica.</summary>
public sealed partial class DiffBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Clear = new(Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        DiffLineKind.Added => Palette.Get("DiffAddedBackgroundBrush"),
        DiffLineKind.Removed => Palette.Get("DiffRemovedBackgroundBrush"),
        _ => Clear,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class DiffForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        DiffLineKind.Added => Palette.Get("DiffAddedForegroundBrush"),
        DiffLineKind.Removed => Palette.Get("DiffRemovedForegroundBrush"),
        _ => Palette.Get("DiffContextForegroundBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>A file's git status letter as its hue: added green, deleted red, renamed blue, modified amber.</summary>
public sealed partial class ChangeLetterBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var tint = parameter is "tint";
        return value switch
        {
            "A" => Palette.Get(tint ? "KindWriteTintBrush" : "KindWriteBrush"),
            "D" => Palette.Get(tint ? "DiffRemovedBackgroundBrush" : "StatusFailedBrush"),
            "R" => Palette.Get(tint ? "KindReadTintBrush" : "KindReadBrush"),
            _ => Palette.Get(tint ? "KindEditTintBrush" : "KindEditBrush"),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Visible only when a collection is empty, for "nothing here yet" placeholders.</summary>
public sealed partial class CountToInverseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int and > 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Visible only when a count is positive, e.g. the status bar's task item.
/// A "2" parameter raises the bar: visible from two upward, so a single-file picker stays hidden.</summary>
public sealed partial class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var minimum = parameter is string text && int.TryParse(text, out var parsed) ? parsed : 1;
        return value is int count && count >= minimum ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed partial class FileNodeGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// DateTimeOffset rendered as distance from now - "just now", "5m ago", "3h ago",
/// "2d ago" - falling back to a date once a week has passed.
/// </summary>
public sealed partial class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTimeOffset time)
        {
            return "";
        }

        var delta = DateTimeOffset.Now - time;

        return delta.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)delta.TotalMinutes}m ago",
            < 60 * 24 => $"{(int)delta.TotalHours}h ago",
            < 60 * 24 * 7 => $"{(int)delta.TotalDays}d ago",
            _ => time.ToString("MMM d"),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a bool, for IsEnabled bound to an "is busy" flag.</summary>
public sealed partial class NotBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is not true;
}

/// <summary>
/// Colour for a git working-tree state, in the palette the rest of the app uses:
/// green for work git has never seen, amber for edits, red for removals and conflicts.
/// Clean nodes come back as null so the theme's own text colour stands. UnsetValue is
/// not an option here: the compiled binding pipeline casts the converter's result
/// straight to Brush, so an UnsetValue throws InvalidCastException mid-layout - which
/// is the crash that used to take the app down whenever the tree re-arranged.
/// </summary>
public sealed partial class GitStatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush AddedBrush = new(Colors.SeaGreen);
    private static readonly SolidColorBrush ModifiedBrush = new(Colors.Goldenrod);
    private static readonly SolidColorBrush DeletedBrush = new(Colors.IndianRed);
    private static readonly SolidColorBrush ConflictedBrush = new(Colors.OrangeRed);

    /// <summary>The state's colour, or null for a clean file (callers keep their own text colour).</summary>
    public static SolidColorBrush? ForChange(GitChangeKind? kind) => kind switch
    {
        GitChangeKind.Added or GitChangeKind.Untracked => AddedBrush,
        GitChangeKind.Modified or GitChangeKind.Renamed or GitChangeKind.Copied => ModifiedBrush,
        GitChangeKind.Deleted => DeletedBrush,
        GitChangeKind.Conflicted => ConflictedBrush,
        _ => null,
    };

    public object? Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        GitChangeKind.Added or GitChangeKind.Untracked => AddedBrush,
        GitChangeKind.Modified or GitChangeKind.Renamed or GitChangeKind.Copied => ModifiedBrush,
        GitChangeKind.Deleted => DeletedBrush,
        GitChangeKind.Conflicted => ConflictedBrush,

        // Unchanged files use the palette's text colour, which follows the resolved theme;
        // the inherited default stayed light-on-light in the Light theme.
        _ => Palette.Get("TodoTextBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Git-ignored entries render faint; everything else stays at full strength.</summary>
public sealed partial class IgnoredMuteConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? 0.45 : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>The checked-out branch's name reads bold; the rest stay regular.</summary>
public sealed partial class CurrentToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Tints the terminal tab's icon by state: working blue, idle grey, done green, failed red.</summary>
public sealed partial class TerminalActivityBrushConverter : IValueConverter
{
    private static readonly Dictionary<TerminalActivity, SolidColorBrush> BrushesByActivity = new()
    {
        [TerminalActivity.Busy] = new SolidColorBrush(Colors.DodgerBlue),
        [TerminalActivity.Terminated] = new SolidColorBrush(Colors.SeaGreen),
        [TerminalActivity.Error] = new SolidColorBrush(Colors.IndianRed),
        [TerminalActivity.Idle] = new SolidColorBrush(Colors.Gray),
    };

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is TerminalActivity activity && BrushesByActivity.TryGetValue(activity, out var brush)
            ? brush
            : BrushesByActivity[TerminalActivity.Idle];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Appended after the title once the shell is gone; empty while it lives.</summary>
public sealed partial class TerminalExitSuffixConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        TerminalActivity.Terminated => " (exited)",
        TerminalActivity.Error => " (failed)",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// A usage percentage (0-100) to its meter colour: the accent while there is headroom,
/// amber from 75%, red from 90% - so a window about to run out stands out at a glance.
/// </summary>
public sealed partial class UsageToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var percent = value is double d ? d : 0;
        var key = percent >= 90 ? "SystemFillColorCriticalBrush"
            : percent >= 75 ? "SystemFillColorCautionBrush"
            : "AccentFillColorDefaultBrush";
        return ChatPaletteTheme.Resource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Icon glyph and colour for a file-tree node, keyed on the file name first, then its extension.</summary>
public static class FileIcons
{
    private const string FolderGlyph = "\uE8B7";
    private const string PageGlyph = "\uE7C3";
    private const string CodeGlyph = "\uE943";
    private const string TerminalGlyph = "\uE756";
    private const string DocGlyph = "\uE8A5";
    private const string SettingsGlyph = "\uE713";
    private const string DataGlyph = "\uE8FD";
    private const string ProjectGlyph = "\uE8F1";
    private const string PackageGlyph = "\uE7B8";
    private const string ImageGlyph = "\uEB9F";
    private const string AudioGlyph = "\uE8D6";
    private const string VideoGlyph = "\uE714";
    private const string FontGlyph = "\uE8E9";
    private const string LockGlyph = "\uE72E";
    private const string GitGlyph = "\uE8EC";
    private const string TableGlyph = "\uE80A";
    private const string DatabaseGlyph = "\uEA86";
    private const string BinaryGlyph = "\uE950";

    private static readonly Windows.UI.Color Purple = Rgb(0x9B, 0x4F, 0xC8);
    private static readonly Windows.UI.Color Yellow = Rgb(0xF5, 0xC5, 0x18);
    private static readonly Windows.UI.Color Blue = Rgb(0x31, 0x78, 0xC6);
    private static readonly Windows.UI.Color Green = Rgb(0x4E, 0xAA, 0x25);
    private static readonly Windows.UI.Color Teal = Rgb(0x26, 0xA6, 0x9A);
    private static readonly Windows.UI.Color Grey = Rgb(0x9E, 0x9E, 0x9E);
    private static readonly Windows.UI.Color Orange = Rgb(0xE3, 0x8C, 0x00);
    private static readonly Windows.UI.Color Red = Rgb(0xCB, 0x17, 0x1E);
    private static readonly Windows.UI.Color Folder = Rgb(0xE8, 0xB3, 0x4A);
    private static readonly Windows.UI.Color Default = Rgb(0x8A, 0x9B, 0xB0);

    // Whole-name rules win over extension rules, so global.json is not just "some json".
    private static readonly Dictionary<string, (string Glyph, Windows.UI.Color Color)> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["global.json"] = (SettingsGlyph, Rgb(0x9B, 0x4F, 0xC8)),
        ["nuget.config"] = (PackageGlyph, Rgb(0x00, 0x7A, 0xCC)),
        ["launchSettings.json"] = (SettingsGlyph, Yellow),
        ["appsettings.json"] = (SettingsGlyph, Yellow),
        ["package.json"] = (PackageGlyph, Rgb(0xCB, 0x38, 0x37)),
        ["package-lock.json"] = (LockGlyph, Rgb(0xCB, 0x38, 0x37)),
        ["tsconfig.json"] = (SettingsGlyph, Blue),
        ["Directory.Build.props"] = (SettingsGlyph, Purple),
        ["Directory.Build.targets"] = (SettingsGlyph, Purple),
        ["Directory.Packages.props"] = (PackageGlyph, Purple),
        ["Dockerfile"] = (PackageGlyph, Rgb(0x24, 0x96, 0xED)),
        ["docker-compose.yml"] = (PackageGlyph, Rgb(0x24, 0x96, 0xED)),
        ["docker-compose.yaml"] = (PackageGlyph, Rgb(0x24, 0x96, 0xED)),
        ["Makefile"] = (TerminalGlyph, Green),
        ["README"] = (DocGlyph, Rgb(0x42, 0xA5, 0xF5)),
        ["README.md"] = (DocGlyph, Rgb(0x42, 0xA5, 0xF5)),
        ["LICENSE"] = (DocGlyph, Orange),
        ["LICENSE.md"] = (DocGlyph, Orange),
        ["LICENSE.txt"] = (DocGlyph, Orange),
        ["CLAUDE.md"] = (DocGlyph, Rgb(0xD9, 0x77, 0x57)),
        [".editorconfig"] = (SettingsGlyph, Rgb(0xA0, 0xA0, 0xA0)),
        [".gitignore"] = (GitGlyph, Rgb(0xF1, 0x50, 0x2F)),
        [".gitattributes"] = (GitGlyph, Rgb(0xF1, 0x50, 0x2F)),
        [".gitmodules"] = (GitGlyph, Rgb(0xF1, 0x50, 0x2F)),
    };

    private static readonly Dictionary<string, (string Glyph, Windows.UI.Color Color)> Extensions = BuildExtensions();

    private static readonly Dictionary<Windows.UI.Color, SolidColorBrush> BrushCache = new();

    private static Dictionary<string, (string, Windows.UI.Color)> BuildExtensions()
    {
        var d = new Dictionary<string, (string, Windows.UI.Color)>(StringComparer.OrdinalIgnoreCase);

        void Add(string glyph, Windows.UI.Color color, params string[] exts)
        {
            foreach (var e in exts)
            {
                d[e] = (glyph, color);
            }
        }

        // Languages
        Add(CodeGlyph, Purple, ".cs", ".csx");
        Add(CodeGlyph, Rgb(0x00, 0x9F, 0xFF), ".xaml", ".axaml");
        Add(CodeGlyph, Rgb(0x51, 0x2B, 0xD4), ".razor", ".cshtml");
        Add(CodeGlyph, Rgb(0xF1, 0xDD, 0x35), ".js", ".mjs", ".cjs");
        Add(CodeGlyph, Blue, ".ts", ".tsx");
        Add(CodeGlyph, Rgb(0x61, 0xDA, 0xFB), ".jsx");
        Add(CodeGlyph, Rgb(0x37, 0x76, 0xAB), ".py", ".pyw");
        Add(CodeGlyph, Rgb(0xDE, 0xA5, 0x84), ".rs");
        Add(CodeGlyph, Rgb(0x00, 0xAD, 0xD8), ".go");
        Add(CodeGlyph, Rgb(0xE7, 0x6F, 0x00), ".java");
        Add(CodeGlyph, Rgb(0x7F, 0x52, 0xFF), ".kt", ".kts");
        Add(CodeGlyph, Rgb(0x55, 0x9B, 0xD4), ".c", ".h");
        Add(CodeGlyph, Rgb(0x00, 0x59, 0x9C), ".cpp", ".cc", ".cxx", ".hpp");
        Add(CodeGlyph, Rgb(0x77, 0x7B, 0xB4), ".php");
        Add(CodeGlyph, Rgb(0x41, 0xB8, 0x83), ".vue");
        Add(CodeGlyph, Rgb(0xFF, 0x3E, 0x00), ".svelte");

        // Markup and styles
        Add(CodeGlyph, Rgb(0xE4, 0x4D, 0x26), ".html", ".htm");
        Add(CodeGlyph, Rgb(0x56, 0x4C, 0xE0), ".css", ".less");
        Add(CodeGlyph, Rgb(0xCD, 0x67, 0x99), ".scss", ".sass");

        // Shell
        Add(TerminalGlyph, Rgb(0x30, 0x6F, 0xC8), ".ps1", ".psm1");
        Add(TerminalGlyph, Green, ".sh", ".bash", ".zsh", ".bat", ".cmd");

        // Data
        Add(DataGlyph, Yellow, ".json", ".jsonc");
        Add(DataGlyph, Orange, ".xml");
        Add(DataGlyph, Red, ".yml", ".yaml");
        Add(DataGlyph, Rgb(0x9C, 0x4A, 0x1A), ".toml");
        Add(TableGlyph, Teal, ".csv", ".tsv", ".xls", ".xlsx");
        Add(DatabaseGlyph, Rgb(0xDA, 0xD8, 0xD8), ".sql", ".db", ".sqlite", ".mdf");

        // Projects and settings
        Add(ProjectGlyph, Purple, ".csproj", ".sln", ".slnx", ".vbproj", ".fsproj");
        Add(SettingsGlyph, Purple, ".props", ".targets");
        Add(SettingsGlyph, Grey, ".ini", ".cfg", ".config", ".env");

        // Docs
        Add(DocGlyph, Rgb(0x42, 0xA5, 0xF5), ".md", ".markdown");
        Add(DocGlyph, Grey, ".txt", ".rtf", ".log");
        Add(DocGlyph, Rgb(0xE5, 0x39, 0x35), ".pdf");
        Add(DocGlyph, Rgb(0x2B, 0x57, 0x9A), ".doc", ".docx");

        // Media
        Add(ImageGlyph, Teal, ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".bmp", ".tiff");
        Add(ImageGlyph, Rgb(0xFF, 0xB1, 0x3B), ".svg");
        Add(AudioGlyph, Rgb(0xAB, 0x47, 0xBC), ".mp3", ".wav", ".flac", ".ogg", ".m4a");
        Add(VideoGlyph, Rgb(0xEC, 0x40, 0x7A), ".mp4", ".mkv", ".mov", ".avi", ".webm");
        Add(FontGlyph, Grey, ".ttf", ".otf", ".woff", ".woff2");

        // Packages, binaries, security
        Add(PackageGlyph, Rgb(0x00, 0x7A, 0xCC), ".nupkg", ".zip", ".7z", ".tar", ".gz", ".rar");
        Add(BinaryGlyph, Rgb(0x78, 0x90, 0x9C), ".dll", ".exe", ".pdb", ".bin");
        Add(LockGlyph, Grey, ".lock", ".key", ".pem", ".cer", ".crt", ".pfx");

        return d;
    }

    private static Windows.UI.Color Rgb(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);

    private static bool TryLookup(string name, out (string Glyph, Windows.UI.Color Color) icon)
    {
        if (Names.TryGetValue(name, out icon))
        {
            return true;
        }

        // Pattern families: appsettings.Development.json, tsconfig.build.json, ...
        if (name.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            icon = Names["appsettings.json"];
            return true;
        }

        if (name.StartsWith("tsconfig.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            icon = Names["tsconfig.json"];
            return true;
        }

        if (name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase))
        {
            icon = Names["Dockerfile"];
            return true;
        }

        if (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            icon = (SettingsGlyph, Grey);
            return true;
        }

        // Dotfiles like .gitkeep have no Path extension; real extensions are looked up by suffix.
        var ext = Path.GetExtension(name);
        return (ext.Length > 0 && Extensions.TryGetValue(ext, out icon)) || Extensions.TryGetValue(name, out icon);
    }

    public static string Glyph(bool isDirectory) => isDirectory ? FolderGlyph : PageGlyph;

    /// <summary>Returns a Segoe Fluent glyph for a folder, or for a file based on its name and type.</summary>
    public static string NodeGlyph(bool isDirectory, string? name)
    {
        if (isDirectory)
        {
            return FolderGlyph;
        }

        return !string.IsNullOrEmpty(name) && TryLookup(name, out var icon) ? icon.Glyph : PageGlyph;
    }

    public static string FileGlyph(string? name) => NodeGlyph(false, name);

    public static Brush Brush(bool isDirectory, string name)
    {
        var color = Default;
        if (isDirectory)
        {
            color = Folder;
        }
        else if (!string.IsNullOrEmpty(name) && TryLookup(name, out var icon))
        {
            color = icon.Color;
        }

        if (!BrushCache.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            BrushCache[color] = brush;
        }

        return brush;
    }
}
