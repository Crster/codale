using Codale.App.Controls;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Codale.App.Views;

/// <summary>The terminal's own right-click menu, shared by every tab that hosts a <see cref="TerminalView"/>.</summary>
internal static class TerminalContextMenu
{
    /// <summary>Copy, Paste, Select All and Clear, with Refresh for a project command.</summary>
    public static void Show(TerminalView terminal, ContextRequestedEventArgs args, Func<Task>? refresh = null)
    {
        args.Handled = true;

        var menu = new MenuFlyout
        {
            // Rounded to match every other popup in the app.
            MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter))
            {
                Setters = { new Setter(MenuFlyoutPresenter.CornerRadiusProperty, 8) },
            },
        };

        var copy = new MenuFlyoutItem { Text = "Copy", IsEnabled = terminal.HasSelection };
        copy.Click += (_, _) => terminal.CopySelection();

        var paste = new MenuFlyoutItem { Text = "Paste" };
        paste.Click += (_, _) => _ = terminal.PasteAsync();

        var selectAll = new MenuFlyoutItem { Text = "Select All" };
        selectAll.Click += (_, _) => terminal.SelectAll();

        var clear = new MenuFlyoutItem { Text = "Clear" };
        clear.Click += (_, _) => terminal.Clear();

        menu.Items.Add(copy);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(paste);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(selectAll);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(clear);

        if (refresh is not null)
        {
            var refreshItem = new MenuFlyoutItem { Text = "Refresh", Icon = new SymbolIcon(Symbol.Refresh) };
            refreshItem.Click += async (_, _) => await refresh();
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(refreshItem);
        }

        menu.ShowAt(terminal, new FlyoutShowOptions { Position = args.TryGetPosition(terminal, out var position) ? position : default });
    }
}
