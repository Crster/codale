using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.Browser;

public enum SaveChoice { NotNow, Save, Never }

public sealed record PermissionAnswer(bool Allow, bool Remember);

/// <summary>
/// The browser's native dialogs. WinUI shows one ContentDialog at a time, so every prompt queues behind
/// <see cref="Gate"/>; a page asking for two things at once gets two dialogs, one after the other.
/// </summary>
internal static class Prompts
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Offers to save a login the user just submitted on a page.</summary>
    public static async Task<SaveChoice> SavePasswordAsync(XamlRoot root, string origin, string username, bool updating)
    {
        var dialog = new ContentDialog
        {
            Title = updating ? "Update the saved password?" : "Save this password?",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = Site(origin), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = username.Length > 0 ? username : "(no user name)", Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                },
            },
            PrimaryButtonText = updating ? "Update" : "Save",
            SecondaryButtonText = "Never for this site",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await ShowAsync(root, dialog);
        return result switch
        {
            ContentDialogResult.Primary => SaveChoice.Save,
            ContentDialogResult.Secondary => SaveChoice.Never,
            _ => SaveChoice.NotNow,
        };
    }

    /// <summary>Asks whether a site may do something it asked for. Closing the dialog blocks; the box says whether to remember the answer.</summary>
    public static async Task<PermissionAnswer> PermissionAsync(XamlRoot root, string origin, string what)
    {
        var remember = new CheckBox { Content = "Remember this choice for this site", IsChecked = true };
        var dialog = new ContentDialog
        {
            Title = $"Allow {Site(origin)}?",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"{Site(origin)} wants to {what}.", TextWrapping = TextWrapping.Wrap },
                    remember,
                },
            },
            PrimaryButtonText = "Allow",
            CloseButtonText = "Block",
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await ShowAsync(root, dialog);
        // Only an answer counts for memory; dismissing the dialog some other way blocks this one time.
        var answered = result is ContentDialogResult.Primary or ContentDialogResult.None;
        return new PermissionAnswer(result == ContentDialogResult.Primary, answered && remember.IsChecked == true);
    }

    /// <summary>Lets the user pick which saved account to fill on the page. Null when cancelled.</summary>
    public static async Task<CredentialEntry?> ChooseAccountAsync(XamlRoot root, IReadOnlyList<CredentialEntry> logins)
    {
        var choices = new RadioButtons { MaxColumns = 1 };
        foreach (var login in logins) choices.Items.Add($"{login.Username}  ·  {login.Site}");
        choices.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            Title = "Fill which account?",
            Content = choices,
            PrimaryButtonText = "Fill",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await ShowAsync(root, dialog);
        var index = choices.SelectedIndex;
        return result == ContentDialogResult.Primary && index >= 0 && index < logins.Count ? logins[index] : null;
    }

    /// <summary>A site, user name and password, for adding or changing a login. Null when cancelled or incomplete.</summary>
    public static async Task<(string Origin, string Username, string Password)?> CredentialAsync(XamlRoot root, string title, string origin = "", string username = "", bool fixedSite = false)
    {
        var site = new TextBox { Header = "Site", PlaceholderText = "https://example.com", Text = origin, IsReadOnly = fixedSite };
        var user = new TextBox { Header = "User name", Text = username };
        var password = new PasswordBox { Header = "Password" };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel { Spacing = 12, MinWidth = 340, Children = { site, user, password } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await ShowAsync(root, dialog);
        if (result != ContentDialogResult.Primary || password.Password.Length == 0) return null;
        var normalized = NormalizeOrigin(site.Text);
        return normalized is null ? null : (normalized, user.Text.Trim(), password.Password);
    }

    /// <summary>A yes or no question with a destructive action on the primary button.</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string text, string action)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(root, dialog) == ContentDialogResult.Primary;
    }

    /// <summary>A short message the user has to acknowledge.</summary>
    public static async Task MessageAsync(XamlRoot root, string title, string text)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        };
        await ShowAsync(root, dialog);
    }

    /// <summary>"https://host[:port]" for a site address the user typed, or null when it is not an http(s) address.</summary>
    public static string? NormalizeOrigin(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return null;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }

    /// <summary>The host of a site's origin, for headings: "example.com" rather than "https://example.com".</summary>
    public static string Site(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : origin;

    private static async Task<ContentDialogResult> ShowAsync(XamlRoot root, ContentDialog dialog)
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
