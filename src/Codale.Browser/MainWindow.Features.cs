using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Windows.UI.Core;

namespace Codale.Browser;

/// <summary>
/// The toolbar's essentials: downloads, history and bookmarks, the sidebar, find in page, print, clearing
/// browsing data, and the More menu. Kept apart from the tab and page plumbing in <see cref="MainWindow"/>.
/// </summary>
public sealed partial class MainWindow
{
    // ---- Downloads ------------------------------------------------------------------------

    private void OnDownloadStarting(CoreWebView2DownloadStartingEventArgs e)
    {
        var operation = e.DownloadOperation;
        var row = new DownloadRow(operation.Uri, FileNameOf(operation));
        _downloads.Insert(0, row);
        _notifications.Post("Download started", row.Name);
        operation.StateChanged += (_, _) =>
        {
            row.Name = FileNameOf(operation);
            row.Path = operation.ResultFilePath;
            row.Received = operation.BytesReceived;
            row.Total = operation.TotalBytesToReceive;
            row.State = operation.State switch
            {
                CoreWebView2DownloadState.Completed => "Completed",
                CoreWebView2DownloadState.Interrupted => "Failed",
                _ => "In progress",
            };
            row.Changed();
            if (operation.State == CoreWebView2DownloadState.Completed) _notifications.Post("Download complete", row.Path, toast: !_windowActive);
            else if (operation.State == CoreWebView2DownloadState.Interrupted) _notifications.Post("Download failed", row.Name, toast: !_windowActive);
        };
    }

    private static string FileNameOf(CoreWebView2DownloadOperation operation) =>
        Path.GetFileName(operation.ResultFilePath) is { Length: > 0 } file ? file : operation.Uri;

    // ---- History and bookmarks -----------------------------------------------------------

    /// <summary>Web pages and local HTML files can be bookmarked and listed in history; about: pages cannot.</summary>
    private static bool IsPageUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file";

    /// <summary>The address field stopped being edited by the user (Enter or Escape): it follows the page again.</summary>
    private void Address_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingAddress && Address.FocusState != FocusState.Unfocused) _addressEdited = true;
    }

    /// <summary>Records a finished visit to a web page in the history.</summary>
    private void RememberVisit(CoreWebView2 core)
    {
        if (!IsPageUrl(core.Source)) return;
        _history.Add(core.Source, core.DocumentTitle);
        RefreshHistory();
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e) => ToggleBookmark();

    private void Bookmark_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleBookmark();
    }

    private void ToggleBookmark()
    {
        if (_active?.View.CoreWebView2 is not { } core || !IsPageUrl(core.Source))
        {
            _notifications.Post("Nothing to bookmark", "Open a web page first.");
            return;
        }

        var saved = _bookmarks.Toggle(core.Source, core.DocumentTitle);
        UpdateBookmarkStar();
        _notifications.Post(saved ? "Bookmarked" : "Bookmark removed", core.DocumentTitle.Length > 0 ? core.DocumentTitle : core.Source);
    }

    /// <summary>The star beside the address: filled when the page is bookmarked.</summary>
    private void UpdateBookmarkStar()
    {
        var url = _active?.Url ?? "";
        var saved = IsPageUrl(url) && _bookmarks.Contains(url);
        BookmarkButton.Content = saved ? "" : "";
        ToolTipService.SetToolTip(BookmarkButton, saved ? "Remove bookmark (Ctrl+D)" : "Bookmark this page (Ctrl+D)");
    }

    // ---- Sidebar ----------------------------------------------------------------------------

    private void Sidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebar(null);

    private void Sidebar_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleSidebar(null);
    }

    private void History_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetSidebar(true, "history");
    }

    private void Downloads_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetSidebar(true, "downloads");
    }

    /// <summary>Shows or hides the sidebar. With no section it toggles; with one it opens that section.</summary>
    private void ToggleSidebar(string? section)
    {
        if (section is null && SideGrid.Visibility == Visibility.Visible)
        {
            SetSidebar(false);
            return;
        }

        SetSidebar(true, section);
    }

    private void SetSidebar(bool show, string? section = null)
    {
        SideGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && section is not null) ShowSidebarSection(section);
        if (show) RefreshHistory();
    }

    // ---- Find in page -------------------------------------------------------------------------

    private void Find_Click(object sender, RoutedEventArgs e) => ShowFind();

    private void Find_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ShowFind();
    }

    private void ShowFind()
    {
        FindRow.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Keyboard);
        FindBox.SelectAll();
    }

    private void CloseFind()
    {
        FindRow.Visibility = Visibility.Collapsed;
        FindStatus.Text = "";
        RunSafe(ClearFindSelectionAsync);
        _active?.View.Focus(FocusState.Programmatic);
    }

    private async Task ClearFindSelectionAsync()
    {
        if (_active?.View.CoreWebView2 is { } core) await core.ExecuteScriptAsync("window.getSelection().removeAllRanges()");
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => RunSafe(() => FindAsync(forward: true));

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                var backwards = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
                RunSafe(() => FindAsync(forward: !backwards));
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                CloseFind();
                break;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => RunSafe(() => FindAsync(forward: true));

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => RunSafe(() => FindAsync(forward: false));

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    /// <summary>Selects the next (or previous) match in the page. The page reports only whether it found one, not a count.</summary>
    private async Task FindAsync(bool forward)
    {
        if (_active?.View.CoreWebView2 is not { } core) return;
        var text = FindBox.Text;
        if (text.Length == 0)
        {
            FindStatus.Text = "";
            await core.ExecuteScriptAsync("window.getSelection().removeAllRanges()");
            return;
        }

        var found = await core.ExecuteScriptAsync($"window.find({JsonSerializer.Serialize(text)}, false, {(forward ? "false" : "true")}, true)");
        FindStatus.Text = found == "true" ? "" : "No matches";
    }

    // ---- Print, browsing data and the More menu -------------------------------------------------

    private void Print_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        PrintPage();
    }

    private void PrintPage() => _active?.View.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);

    /// <summary>Deletes cookies, cache and site storage for this browser's profile, and this browser's history.</summary>
    private async Task ClearBrowsingDataAsync()
    {
        if (_active?.View.CoreWebView2 is not { } core) return;
        var confirmed = await Prompts.ConfirmAsync(Root.XamlRoot, "Clear browsing data?",
            "Cookies, cached files and site storage are deleted, which signs you out of sites. History is cleared too. Saved passwords, bookmarks and downloads are kept.",
            "Clear data");
        if (!confirmed) return;
        await core.Profile.ClearBrowsingDataAsync();
        _history.Clear();
        RefreshHistory();
        _notifications.Post("Browsing data cleared", "Cookies, cache and history were removed.");
    }

    private MenuFlyout BuildMoreMenu()
    {
        // The More button sits at the window's right edge, so the menu opens leftward from it.
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(MenuItem("New tab  (Ctrl+T)", () => RunSafe(() => OpenUserTabAsync(null, focusAddress: true))));
        menu.Items.Add(MenuItem("Find in page  (Ctrl+F)", ShowFind));
        menu.Items.Add(MenuItem("Bookmark this page  (Ctrl+D)", ToggleBookmark));
        menu.Items.Add(MenuItem("Sidebar  (Ctrl+B)", () => ToggleSidebar(null)));
        menu.Items.Add(MenuItem("History  (Ctrl+H)", () => SetSidebar(true, "history")));
        menu.Items.Add(MenuItem("Downloads  (Ctrl+J)", () => SetSidebar(true, "downloads")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Print…  (Ctrl+P)", PrintPage));
        menu.Items.Add(MenuItem("Reload without cache  (Ctrl+Shift+R)", () => RunSafe(HardReloadAsync)));
        menu.Items.Add(MenuItem("Open in default browser", () => RunSafe(OpenInDefaultBrowserAsync)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Clear browsing data…", () => RunSafe(ClearBrowsingDataAsync)));
        return menu;
    }

    private static MenuFlyoutItem MenuItem(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }

    private async Task OpenInDefaultBrowserAsync()
    {
        var url = _active?.Url ?? "";
        if (OriginOf(url) is null) return;
        await Launcher.LaunchUriAsync(new Uri(url));
    }
}
