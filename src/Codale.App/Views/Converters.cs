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

/// <summary>Icon glyph and colour for a file-tree node, keyed on the file's extension.</summary>
public static class FileIcons
{
    private static readonly Dictionary<string, Windows.UI.Color> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = Rgb(0x68, 0x21, 0x7A),
        [".csx"] = Rgb(0x68, 0x21, 0x7A),
        [".xaml"] = Rgb(0x00, 0x9F, 0xFF),
        [".axaml"] = Rgb(0x00, 0x9F, 0xFF),
        [".csproj"] = Rgb(0x9B, 0x4F, 0xC8),
        [".sln"] = Rgb(0x9B, 0x4F, 0xC8),
        [".slnx"] = Rgb(0x9B, 0x4F, 0xC8),
        [".props"] = Rgb(0x9B, 0x4F, 0xC8),
        [".targets"] = Rgb(0x9B, 0x4F, 0xC8),
        [".json"] = Rgb(0xF5, 0xC5, 0x18),
        [".jsonc"] = Rgb(0xF5, 0xC5, 0x18),
        [".js"] = Rgb(0xF1, 0xDD, 0x35),
        [".mjs"] = Rgb(0xF1, 0xDD, 0x35),
        [".ts"] = Rgb(0x31, 0x78, 0xC6),
        [".tsx"] = Rgb(0x31, 0x78, 0xC6),
        [".html"] = Rgb(0xE4, 0x4D, 0x26),
        [".htm"] = Rgb(0xE4, 0x4D, 0x26),
        [".css"] = Rgb(0x56, 0x4C, 0xE0),
        [".scss"] = Rgb(0xCD, 0x67, 0x99),
        [".md"] = Rgb(0x42, 0xA5, 0xF5),
        [".txt"] = Rgb(0x9E, 0x9E, 0x9E),
        [".py"] = Rgb(0x37, 0x76, 0xAB),
        [".rs"] = Rgb(0xDE, 0xA5, 0x84),
        [".go"] = Rgb(0x00, 0xAD, 0xD8),
        [".java"] = Rgb(0xE7, 0x6F, 0x00),
        [".ps1"] = Rgb(0x30, 0x6F, 0xC8),
        [".psm1"] = Rgb(0x30, 0x6F, 0xC8),
        [".sh"] = Rgb(0x4E, 0xAA, 0x25),
        [".bat"] = Rgb(0x4E, 0xAA, 0x25),
        [".cmd"] = Rgb(0x4E, 0xAA, 0x25),
        [".xml"] = Rgb(0xE3, 0x8C, 0x00),
        [".yml"] = Rgb(0xCB, 0x17, 0x1E),
        [".yaml"] = Rgb(0xCB, 0x17, 0x1E),
        [".toml"] = Rgb(0x9C, 0x4A, 0x1A),
        [".sql"] = Rgb(0xDA, 0xD8, 0xD8),
        [".png"] = Rgb(0x26, 0xA6, 0x9A),
        [".jpg"] = Rgb(0x26, 0xA6, 0x9A),
        [".jpeg"] = Rgb(0x26, 0xA6, 0x9A),
        [".gif"] = Rgb(0x26, 0xA6, 0x9A),
        [".svg"] = Rgb(0xFF, 0xB1, 0x3B),
        [".ico"] = Rgb(0x26, 0xA6, 0x9A),
        [".editorconfig"] = Rgb(0xA0, 0xA0, 0xA0),
        [".gitignore"] = Rgb(0xF1, 0x50, 0x2F),
        [".gitattributes"] = Rgb(0xF1, 0x50, 0x2F),
        [".lock"] = Rgb(0x9E, 0x9E, 0x9E),
    };

    private static readonly Windows.UI.Color Folder = Rgb(0xE8, 0xB3, 0x4A);
    private static readonly Windows.UI.Color Default = Rgb(0x8A, 0x9B, 0xB0);

    private static Windows.UI.Color Rgb(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);

    public static string Glyph(bool isDirectory) => isDirectory ? "\uE8B7" : "\uE7C3";

    /// <summary>Extension shown inside the file icon: ".cs" gives "CS", ".gitignore" gives "GIT".</summary>
    public static string Label(bool isDirectory, string name)
    {
        if (isDirectory)
        {
            return "";
        }

        var ext = Path.GetExtension(name).TrimStart('.');
        if (ext.Length == 0)
        {
            ext = name.TrimStart('.');
        }

        return ext.Length > 4 ? ext[..4].ToUpperInvariant() : ext.ToUpperInvariant();
    }

    public static double LabelSize(bool isDirectory, string name) => Label(isDirectory, name).Length > 3 ? 6 : 7.5;

    public static Visibility FileVisibility(bool isDirectory) => isDirectory ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility FolderVisibility(bool isDirectory) => isDirectory ? Visibility.Visible : Visibility.Collapsed;

    public static Brush Brush(bool isDirectory, string name)
    {
        if (isDirectory)
        {
            return new SolidColorBrush(Folder);
        }

        // Dotfiles like .gitignore have no Path extension, so match the whole name too.
        var ext = Path.GetExtension(name);
        if (Colors.TryGetValue(ext, out var c) || Colors.TryGetValue(name, out c))
        {
            return new SolidColorBrush(c);
        }

        return new SolidColorBrush(Default);
    }
}