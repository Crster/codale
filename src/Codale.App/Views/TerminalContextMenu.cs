using Codale.App.Controls;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Codale.App.Views;

/// <summary>The terminal's own right-click menu, shared by every tab that hosts a <see cref="TerminalView"/>.</summary>
internal static class TerminalContextMenu
{
    /// <summary>Clipboard and selection actions, followed by terminal and project command actions.</summary>
    public static void Show(TerminalView terminal, ContextRequestedEventArgs args, Func<Task>? refresh = null, UIElement? anchor = null)
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

        var copy = new MenuFlyoutItem
        {
            Text = "Copy",
            Icon = new SymbolIcon(Symbol.Copy),
            KeyboardAcceleratorTextOverride = "Ctrl+C",
            IsEnabled = terminal.HasSelection,
        };
        copy.Click += (_, _) => terminal.CopySelection();

        var paste = new MenuFlyoutItem
        {
            Text = "Paste",
            Icon = new SymbolIcon(Symbol.Paste),
            KeyboardAcceleratorTextOverride = "Ctrl+V",
        };
        paste.Click += (_, _) => _ = terminal.PasteAsync();

        var selectAll = new MenuFlyoutItem { Text = "Select All", Icon = new SymbolIcon(Symbol.SelectAll) };
        selectAll.Click += (_, _) => terminal.SelectAll();

        var clear = new MenuFlyoutItem { Text = "Clear Terminal", Icon = new SymbolIcon(Symbol.Clear) };
        ToolTipService.SetToolTip(clear, "Clear the screen and scrollback. The running process continues.");
        clear.Click += (_, _) => terminal.Clear();

        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(selectAll);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(clear);

        if (refresh is not null)
        {
            var refreshItem = new MenuFlyoutItem { Text = "Restart Command", Icon = new SymbolIcon(Symbol.Refresh) };
            ToolTipService.SetToolTip(refreshItem, "Stop the current process and rerun the original project command.");
            refreshItem.Click += async (_, _) => await refresh();
            menu.Items.Add(refreshItem);
        }

        var target = anchor ?? terminal;
        menu.ShowAt(target, new FlyoutShowOptions { Position = args.TryGetPosition(target, out var position) ? position : default });
    }
}
