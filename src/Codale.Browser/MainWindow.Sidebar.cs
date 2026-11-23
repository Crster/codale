using System.Collections;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Codale.Browser;

/// <summary>
/// The sidebar beside the page: bookmarks, history and this session's downloads. It is built in code inside the
/// SideGrid placeholder in MainWindow.xaml, and its list templates are parsed at runtime (the XAML compiler in this
/// project cannot take them in markup). Opening an entry loads it in the current tab.
/// </summary>
public sealed partial class MainWindow
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private enum SideSection { Bookmarks, History, Downloads }

    private SideSection _section = SideSection.Bookmarks;

    private readonly TextBlock _sidebarHint = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(28),
        Opacity = 0.6,
    };

    private readonly Button SideBookmarksTab = SectionChip("Bookmarks");
    private readonly Button SideHistoryTab = SectionChip("History");
    private readonly Button SideDownloadsTab = SectionChip("Downloads");
    private readonly TextBox SideSearch = new()
    {
        PlaceholderText = "Search bookmarks",
        Margin = new Thickness(12, 0, 12, 8),
        Height = 32,
    };
    private readonly ListView SideBookmarks = ListFor(itemClick: true);
    private readonly ListView SideHistory = ListFor(itemClick: true);
    private readonly ListView SideDownloads = ListFor(itemClick: false);
    private readonly Button SideOpen = ActionButton("Open");
    private readonly Button SideRemove = ActionButton("Remove");
    private readonly Button SideShowFile = ActionButton("Show in folder");
    private readonly Button SideClearHistory = ActionButton("Clear history");
    private bool _sidebarBuilt;

    private static Button SectionChip(string label) => new()
    {
        Content = label,
        Padding = new Thickness(10, 3, 10, 3),
        MinHeight = 0,
        CornerRadius = new CornerRadius(14),
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    private static Button ActionButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(12, 4, 12, 4),
        MinHeight = 0,
        FontSize = 12,
    };

    private static ListView ListFor(bool itemClick) => new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        IsItemClickEnabled = itemClick,
        Padding = new Thickness(6, 4, 6, 4),
    };

    private void AttachSidebar()
    {
        if (!_sidebarBuilt) BuildSidebar();
        _bookmarks.Items.CollectionChanged += (_, _) => RefreshBookmarks();
        SideDownloads.ItemsSource = _downloads;
        RefreshBookmarks();
        RefreshHistory();
        UpdateSidebarView();
    }

    /// <summary>Builds the sidebar's controls into the SideGrid placeholder. Runs once.</summary>
    private void BuildSidebar()
    {
        _sidebarBuilt = true;
        SideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: the three sections as chips, then the close button.
        var header = new Grid { Padding = new Thickness(10, 10, 6, 8), ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(SideHistoryTab, 1);
        Grid.SetColumn(SideDownloadsTab, 2);
        AutomationProperties.SetName(SideBookmarksTab, "Bookmarks section");
        AutomationProperties.SetName(SideHistoryTab, "History section");
        AutomationProperties.SetName(SideDownloadsTab, "Downloads section");
        SideBookmarksTab.Click += (_, _) => ShowSidebarSection("bookmarks");
        SideHistoryTab.Click += (_, _) => ShowSidebarSection("history");
        SideDownloadsTab.Click += (_, _) => ShowSidebarSection("downloads");
        header.Children.Add(SideBookmarksTab);
        header.Children.Add(SideHistoryTab);
        header.Children.Add(SideDownloadsTab);

        var close = IconButton("", "Close sidebar");
        Grid.SetColumn(close, 3);
        close.Click += SidebarClose_Click;
        header.Children.Add(close);
        Grid.SetRow(header, 0);
        SideGrid.Children.Add(header);

        // Row 1: search (bookmarks and history).
        SideSearch.TextChanged += SearchBox_TextChanged;
        Grid.SetRow(SideSearch, 1);
        SideGrid.Children.Add(SideSearch);

        // Row 2: the lists, one visible at a time, with the empty-state hint.
        SideBookmarks.ItemTemplate = Template(
            "<Grid ColumnSpacing='10' Padding='6,6'>"
            + "<Grid.ColumnDefinitions><ColumnDefinition Width='Auto' /><ColumnDefinition Width='*' /></Grid.ColumnDefinitions>"
            + "<FontIcon Glyph='&#xE734;' FontSize='13' VerticalAlignment='Center' Opacity='0.8' />"
            + "<StackPanel Grid.Column='1' Spacing='2'>"
            + "<TextBlock Text='{Binding Label}' TextTrimming='CharacterEllipsis' />"
            + "<TextBlock Text='{Binding Url}' FontSize='11' Opacity='0.55' TextTrimming='CharacterEllipsis' />"
            + "</StackPanel></Grid>");
        SideHistory.ItemTemplate = Template(
            "<Grid ColumnSpacing='10' Padding='6,6'>"
            + "<Grid.ColumnDefinitions><ColumnDefinition Width='Auto' /><ColumnDefinition Width='*' /></Grid.ColumnDefinitions>"
            + "<FontIcon Glyph='&#xE81C;' FontSize='13' VerticalAlignment='Center' Opacity='0.8' />"
            + "<StackPanel Grid.Column='1' Spacing='2'>"
            + "<TextBlock Text='{Binding Label}' TextTrimming='CharacterEllipsis' />"
            + "<TextBlock Text='{Binding Detail}' FontSize='11' Opacity='0.55' TextTrimming='CharacterEllipsis' />"
            + "</StackPanel></Grid>");
        SideDownloads.ItemTemplate = Template(
            "<Grid ColumnSpacing='10' Padding='6,6'>"
            + "<Grid.ColumnDefinitions><ColumnDefinition Width='Auto' /><ColumnDefinition Width='*' /></Grid.ColumnDefinitions>"
            + "<FontIcon Glyph='&#xE896;' FontSize='13' VerticalAlignment='Center' Opacity='0.8' />"
            + "<StackPanel Grid.Column='1' Spacing='3'>"
            + "<TextBlock Text='{Binding Name}' TextTrimming='CharacterEllipsis' />"
            + "<TextBlock Text='{Binding StatusText}' FontSize='11' Opacity='0.55' TextTrimming='CharacterEllipsis' />"
            + "<ProgressBar Value='{Binding Progress}' Maximum='100' Height='3' MinHeight='3' />"
            + "</StackPanel></Grid>");

        SideBookmarks.ItemClick += BookmarkList_ItemClick;
        SideHistory.ItemClick += HistoryList_ItemClick;
        SideBookmarks.SelectionChanged += (_, _) => UpdateSidebarView();
        SideHistory.SelectionChanged += (_, _) => UpdateSidebarView();
        SideDownloads.SelectionChanged += (_, _) => UpdateSidebarView();
        SideBookmarks.KeyDown += BookmarkList_KeyDown;

        var lists = new Grid();
        lists.Children.Add(SideBookmarks);
        lists.Children.Add(SideHistory);
        lists.Children.Add(SideDownloads);
        lists.Children.Add(_sidebarHint);
        Grid.SetRow(lists, 2);
        SideGrid.Children.Add(lists);

        // Row 3: actions for the selected item, in one row.
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Thickness(12, 8, 12, 12),
        };
        SideOpen.Click += Open_Click;
        SideRemove.Click += Remove_Click;
        SideShowFile.Click += ShowFile_Click;
        SideClearHistory.Click += ClearHistory_Click;
        actions.Children.Add(SideOpen);
        actions.Children.Add(SideRemove);
        actions.Children.Add(SideShowFile);
        actions.Children.Add(SideClearHistory);
        Grid.SetRow(actions, 3);
        SideGrid.Children.Add(actions);
    }

    private static Button IconButton(string glyph, string label)
    {
        var button = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        return button;
    }

    private static DataTemplate Template(string body) =>
        (DataTemplate)XamlReader.Load("<DataTemplate xmlns='" + XamlNamespace + "'>" + body + "</DataTemplate>");

    /// <summary>Opens one section: "bookmarks", "history" or "downloads".</summary>
    private void ShowSidebarSection(string section)
    {
        _section = section switch
        {
            "history" => SideSection.History,
            "downloads" => SideSection.Downloads,
            _ => SideSection.Bookmarks,
        };
        UpdateSidebarView();
    }

    private void RefreshBookmarks()
    {
        var query = SideSearch.Text.Trim();
        SideBookmarks.ItemsSource = query.Length == 0
            ? _bookmarks.Items
            : _bookmarks.Items.Where(b => Matches(query, b.Label, b.Url)).ToList();
        UpdateSidebarView();
    }

    private void RefreshHistory()
    {
        SideHistory.ItemsSource = _history.Search(SideSearch.Text.Trim());
        UpdateSidebarView();
    }

    private static bool Matches(string query, params string[] fields) =>
        fields.Any(field => field.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static int CountOf(object? source) => source switch
    {
        ICollection collection => collection.Count,
        IEnumerable enumerable => enumerable.Cast<object>().Count(),
        _ => 0,
    };

    private static void SetChipStyle(Button chip, bool selected)
    {
        chip.Style = selected ? Application.Current.Resources["AccentButtonStyle"] as Style : null;
    }

    /// <summary>Refreshes the sections, the counts, the empty states and which actions are available.</summary>
    private void UpdateSidebarView()
    {
        if (!_sidebarBuilt) return;
        var bookmarks = _section == SideSection.Bookmarks;
        var history = _section == SideSection.History;
        var downloads = _section == SideSection.Downloads;

        var historyCount = _history.Search(string.Empty).Count;
        ToolTipService.SetToolTip(SideBookmarksTab, $"{_bookmarks.Items.Count} bookmarks");
        ToolTipService.SetToolTip(SideHistoryTab, $"{historyCount} pages in history");
        ToolTipService.SetToolTip(SideDownloadsTab, $"{_downloads.Count} downloads this session");
        SetChipStyle(SideBookmarksTab, bookmarks);
        SetChipStyle(SideHistoryTab, history);
        SetChipStyle(SideDownloadsTab, downloads);

        SideBookmarks.Visibility = bookmarks ? Visibility.Visible : Visibility.Collapsed;
        SideHistory.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        SideDownloads.Visibility = downloads ? Visibility.Visible : Visibility.Collapsed;
        SideSearch.Visibility = downloads ? Visibility.Collapsed : Visibility.Visible;
        SideSearch.PlaceholderText = history ? "Search history" : "Search bookmarks";

        var selectedBookmark = SideBookmarks.SelectedItem is Bookmark;
        var selectedVisit = SideHistory.SelectedItem is HistoryEntry;
        var selectedDownload = SideDownloads.SelectedItem as DownloadRow;
        SideOpen.Visibility = downloads ? Visibility.Collapsed : Visibility.Visible;
        SideOpen.IsEnabled = bookmarks ? selectedBookmark : selectedVisit;
        SideRemove.Visibility = bookmarks ? Visibility.Visible : Visibility.Collapsed;
        SideRemove.IsEnabled = selectedBookmark;
        SideShowFile.Visibility = downloads ? Visibility.Visible : Visibility.Collapsed;
        SideShowFile.IsEnabled = selectedDownload is { Finished: true, Path.Length: > 0 };
        SideClearHistory.Visibility = history ? Visibility.Visible : Visibility.Collapsed;

        var visible = bookmarks ? CountOf(SideBookmarks.ItemsSource)
            : history ? CountOf(SideHistory.ItemsSource)
            : CountOf(SideDownloads.ItemsSource);
        var query = SideSearch.Text.Trim();
        _sidebarHint.Text = visible > 0 ? string.Empty
            : query.Length > 0 ? $"Nothing matches “{query}”."
            : bookmarks ? "No bookmarks yet. Use the star beside the address to save a page."
            : history ? "No history yet. Pages you visit appear here."
            : "No downloads this session. Files you download appear here.";
        _sidebarHint.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SidebarClose_Click(object sender, RoutedEventArgs e) => SetSidebar(false);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_section == SideSection.History) RefreshHistory();
        else if (_section == SideSection.Bookmarks) RefreshBookmarks();
        UpdateSidebarView();
    }

    private void BookmarkList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Bookmark bookmark) NavigateActive(bookmark.Url);
    }

    private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryEntry entry) NavigateActive(entry.Url);
    }

    private void BookmarkList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || SideBookmarks.SelectedItem is not Bookmark bookmark) return;
        e.Handled = true;
        _bookmarks.Remove(bookmark.Url);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_section == SideSection.Bookmarks && SideBookmarks.SelectedItem is Bookmark bookmark) NavigateActive(bookmark.Url);
        else if (_section == SideSection.History && SideHistory.SelectedItem is HistoryEntry entry) NavigateActive(entry.Url);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SideBookmarks.SelectedItem is Bookmark bookmark) _bookmarks.Remove(bookmark.Url);
    }

    /// <summary>Opens Explorer with the downloaded file selected. The file itself is never run.</summary>
    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if (SideDownloads.SelectedItem is DownloadRow { Finished: true } download && download.Path.Length > 0)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{download.Path}\"") { UseShellExecute = false });
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await Prompts.ConfirmAsync(Root.XamlRoot, "Clear history?",
            "Every page in history is removed from this PC. Bookmarks and downloads are kept.", "Clear history");
        if (!confirmed) return;
        _history.Clear();
        RefreshHistory();
    }
}
