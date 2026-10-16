using Microsoft.UI.Xaml;

namespace Codale.App.Controls;

/// <summary>
/// Dialogs open in the popup layer, outside the window's themed root, so they would fall back to
/// the system theme. This reads the theme the window content resolved to, for a dialog's RequestedTheme.
/// </summary>
public static class DialogTheme
{
    public static ElementTheme Of(XamlRoot? root) =>
        root?.Content is FrameworkElement { ActualTheme: var theme } ? theme : ElementTheme.Default;
}
