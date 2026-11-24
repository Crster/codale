using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.Browser;

/// <summary>The browser's native dialogs. WinUI shows one ContentDialog at a time, so they queue behind <see cref="Gate"/>.</summary>
internal static class Prompts
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>A short message the user has to acknowledge.</summary>
    public static async Task MessageAsync(XamlRoot root, string title, string text)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        };
        await ShowAsync(dialog, root);
    }

    /// <summary>Shows any dialog once the one before it has closed.</summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, XamlRoot root)
    {
        await Gate.WaitAsync();
        try
        {
            dialog.XamlRoot = root;
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }
}
