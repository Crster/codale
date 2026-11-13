using Codale.Core.Text;
using Codale.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Windows.UI;

namespace Codale.App.Views;

/// <summary>
/// One look for attachment pills wherever they show - drawn into the composer as an inline image,
/// built from XAML in sent messages - so a pill reads the same before and after sending.
/// </summary>
internal static class PromptPillVisuals
{
    /// <summary>Pill height in DIPs; sized to sit in a 14px text line without stretching it much.</summary>
    public const double Height = 20;

    /// <summary>Longest title before it is cut with an ellipsis.</summary>
    public const double MaxTitleWidth = 200;

    public const double TitleSize = 12;

    public const double GlyphSize = 11;

    public static string Glyph(PromptPillKind kind) => kind switch
    {
        PromptPillKind.Image => "",
        PromptPillKind.Pdf => "",
        _ => "",
    };

    public static Color Fill(ElementTheme theme) => theme == ElementTheme.Dark
        ? Color.FromArgb(0xFF, 0x34, 0x37, 0x3E)
        : Color.FromArgb(0xFF, 0xEE, 0xF0, 0xF4);

    public static Color Stroke(ElementTheme theme) => theme == ElementTheme.Dark
        ? Color.FromArgb(0xFF, 0x4A, 0x4E, 0x57)
        : Color.FromArgb(0xFF, 0xD3, 0xD7, 0xDE);

    public static Color Foreground(ElementTheme theme) => theme == ElementTheme.Dark
        ? Color.FromArgb(0xFF, 0xF1, 0xF2, 0xF5)
        : Color.FromArgb(0xFF, 0x1C, 0x1D, 0x22);

    /// <summary>The icon carries the kind's color; the title stays neutral.</summary>
    public static Color GlyphColor(PromptPillKind kind, ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        return kind switch
        {
            PromptPillKind.Image => dark ? Color.FromArgb(0xFF, 0x6C, 0xCB, 0x8E) : Color.FromArgb(0xFF, 0x1E, 0x7D, 0x46),
            PromptPillKind.Pdf => dark ? Color.FromArgb(0xFF, 0xF2, 0x7D, 0x72) : Color.FromArgb(0xFF, 0xB4, 0x2B, 0x22),
            _ => dark ? Color.FromArgb(0xFF, 0x7A, 0xB4, 0xFF) : Color.FromArgb(0xFF, 0x1F, 0x5F, 0xC9),
        };
    }

    /// <summary>
    /// Previews an attachment, as its chip did: images in the in-app lightbox, PDFs and text in
    /// their default app, and a note when the file has gone from disk.
    /// </summary>
    public static async Task OpenAsync(XamlRoot? root, PromptPillKind kind, string title, string? path)
    {
        try
        {
            if (path is null || !File.Exists(path))
            {
                if (root is not null)
                {
                    var dialog = AppDialog.Apply(new ContentDialog
                    {
                        XamlRoot = root,
                        Title = title,
                        Content = new TextBlock { Text = "The file is no longer on disk.", TextWrapping = TextWrapping.Wrap },
                        CloseButtonText = "Close",
                        DefaultButton = ContentDialogButton.Close,
                    }, DialogTone.Info, AppDialog.Glyphs.Document);
                    await dialog.ShowAsync();
                }

                return;
            }

            if (kind == PromptPillKind.Image && root is not null)
            {
                ImageLightbox.Show(root, path);
                return;
            }

            await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            // Another dialog is already open (WinUI allows one), or the file could not be launched.
        }
    }
}
