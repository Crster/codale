using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Controls;

/// <summary>The "this will stop what is running" confirmation shared by every way of ending work.</summary>
public static class RunningWorkPrompt
{
    /// <summary>True to go ahead (also when there is nothing running, or the prompt cannot show), false to stay.</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot? xamlRoot, IReadOnlyList<string> running, string action)
    {
        if (running.Count == 0 || xamlRoot is null)
        {
            return true;
        }

        try
        {
            var dialog = AppDialog.Apply(new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Still running",
                Content = AppDialog.List($"{action} will stop:", running, AppDialog.Glyphs.Stop),
                PrimaryButtonText = "Stop and continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            }, DialogTone.Warning, AppDialog.Glyphs.Stop);

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // Another dialog is already up: stay put rather than kill work unasked.
            CrashLog.Error("RunningWorkPrompt", "prompt failed", ex);
            return false;
        }
    }
}
