using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Controls;

/// <summary>What a dialog is about, which picks its disc colour and (for Danger) a red primary button.</summary>
public enum DialogTone
{
    /// <summary>A question or a neutral notice (indigo).</summary>
    Info,

    /// <summary>Something to think twice about, but not destructive (amber).</summary>
    Warning,

    /// <summary>A destructive confirmation, or a failure (red). The primary button turns red.</summary>
    Danger,

    /// <summary>Something finished (green).</summary>
    Success,
}

/// <summary>
/// The look every dialog shares: a header with a tinted disc and glyph, body text in the dialog
/// palette, and helpers for file lists and code. Call sites keep building a plain
/// <see cref="ContentDialog"/> with an initializer and pass it through <see cref="Apply"/>, which turns a
/// string Title and Content into the styled versions. The colours live in Themes/Dialogs.xaml.
/// </summary>
public static class AppDialog
{
    /// <summary>Segoe Fluent Icons code points used by the dialogs.</summary>
    public static class Glyphs
    {
        public const string Info = "";
        public const string Help = "";
        public const string Warning = "";
        public const string Error = "";
        public const string Success = "";
        public const string Delete = "";
        public const string Save = "";
        public const string Terminal = "";
        public const string Download = "";
        public const string Import = "";
        public const string Rename = "";
        public const string Account = "";
        public const string Undo = "";
        public const string Stop = "";
        public const string Sparkle = "";
        public const string Document = "";
        public const string Search = "";
        public const string History = "";
        public const string Clear = "";
    }

    /// <summary>
    /// Applies the shared look to <paramref name="dialog"/>: theme from its XamlRoot, the tone header
    /// (a string Title becomes the header), string Content becomes body text, and a Danger tone gets
    /// the red primary button. Returns the same dialog so it can wrap an initializer.
    /// </summary>
    public static ContentDialog Apply(ContentDialog dialog, DialogTone tone = DialogTone.Info, string? glyph = null)
    {
        if (dialog.XamlRoot is { } root)
        {
            dialog.RequestedTheme = DialogTheme.Of(root);
        }

        SetHeader(dialog, dialog.Title as string ?? "", tone, glyph);
        if (dialog.Content is string text)
        {
            dialog.Content = Message(text);
        }

        if (tone == DialogTone.Danger && dialog.PrimaryButtonText is { Length: > 0 })
        {
            dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["DialogDangerButtonStyle"];
        }

        return dialog;
    }

    /// <summary>Replaces the header, e.g. when an assessment that arrives later changes the dialog's tone.</summary>
    public static void SetHeader(ContentDialog dialog, string title, DialogTone tone, string? glyph = null)
    {
        var key = tone.ToString();
        var badge = new Border
        {
            Style = Resource<Style>($"DialogBadge{key}Style"),
            Child = new FontIcon { Glyph = glyph ?? DefaultGlyph(tone), Style = Resource<Style>($"DialogGlyph{key}Style") },
        };
        var text = new TextBlock { Text = title, Style = Resource<Style>("DialogTitleTextStyle") };
        Grid.SetColumn(text, 1);

        var header = new Grid { ColumnSpacing = 12, MinHeight = 34 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(badge);
        header.Children.Add(text);

        // The template's title host is left-aligned and sized to its content; long titles must wrap.
        header.MaxWidth = 500;
        dialog.Title = header;
    }

    /// <summary>Body text in the dialog palette; selectable so error details can be copied.</summary>
    public static TextBlock Message(string text) => new() { Text = text, Style = Resource<Style>("DialogBodyTextStyle") };

    /// <summary>Smaller, quieter text for hints and status lines.</summary>
    public static TextBlock Note(string text = "") => new() { Text = text, Style = Resource<Style>("DialogMutedTextStyle") };

    /// <summary>A command or other literal text on a code surface.</summary>
    public static Border Code(string text) => new()
    {
        Style = Resource<Style>("DialogCodeSurfaceStyle"),
        Child = new TextBlock { Text = text, Style = Resource<Style>("DialogCodeTextStyle") },
    };

    /// <summary>
    /// An optional lead-in followed by a framed list (file names, running jobs), capped at
    /// <paramref name="max"/> rows with an "…and N more" line.
    /// </summary>
    public static StackPanel List(string? intro, IReadOnlyCollection<string> items, string glyph = Glyphs.Document, int max = 8)
    {
        var rows = new StackPanel { Spacing = 6 };
        foreach (var item in items.Take(max))
        {
            var label = new TextBlock { Text = item, Style = Resource<Style>("DialogListItemTextStyle") };
            ToolTipService.SetToolTip(label, item);
            rows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new FontIcon { Glyph = glyph, Style = Resource<Style>("DialogListGlyphStyle") }, label },
            });
        }

        if (items.Count > max)
        {
            rows.Children.Add(Note($"…and {items.Count - max} more"));
        }

        var panel = new StackPanel { Spacing = 10 };
        if (intro is { Length: > 0 })
        {
            panel.Children.Add(Message(intro));
        }

        // Short lists skip the ScrollViewer so the surface hugs its rows instead of stretching to the dialog's height.
        UIElement body = items.Count > 5
            ? new ScrollViewer
            {
                MaxHeight = 220,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rows,
            }
            : rows;
        panel.Children.Add(new Border
        {
            Style = Resource<Style>("DialogSurfaceStyle"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = body,
        });
        return panel;
    }

    /// <summary>Stacks body parts with the dialog's spacing.</summary>
    public static StackPanel Stack(params UIElement[] parts)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var part in parts)
        {
            panel.Children.Add(part);
        }

        return panel;
    }

    /// <summary>A one-button notice (errors, "done" messages).</summary>
    public static async Task AlertAsync(XamlRoot root, string title, string message, DialogTone tone = DialogTone.Danger, string? glyph = null)
    {
        await Apply(new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        }, tone, glyph).ShowAsync();
    }

    private static string DefaultGlyph(DialogTone tone) => tone switch
    {
        DialogTone.Warning => Glyphs.Warning,
        DialogTone.Danger => Glyphs.Error,
        DialogTone.Success => Glyphs.Success,
        _ => Glyphs.Info,
    };

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];
}
