using System.Collections.ObjectModel;
using System.Globalization;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Codale.Browser;

/// <summary>
/// The Network and Console panel under the page: the active tab's requests or console messages, filtered by text and
/// by request type or message level, resizable from its top edge. Clicking a request opens its details.
/// </summary>
public sealed partial class MainWindow
{
    private enum PanelMode { None, Network, Console }

    private const double DefaultPanelHeight = 300;
    private const double MinPanelHeight = 140;

    private static readonly (RequestCategory Category, string Label)[] Categories =
    [
        (RequestCategory.All, "All"), (RequestCategory.FetchXhr, "Fetch/XHR"), (RequestCategory.Document, "Doc"),
        (RequestCategory.Css, "CSS"), (RequestCategory.Script, "JS"), (RequestCategory.Font, "Font"),
        (RequestCategory.Image, "Img"), (RequestCategory.Media, "Media"), (RequestCategory.WebSocket, "WS"),
        (RequestCategory.Other, "Other"),
    ];

    private static readonly (ConsoleLevel Level, string Label)[] Levels =
    [
        (ConsoleLevel.Error, "Errors"), (ConsoleLevel.Warning, "Warnings"), (ConsoleLevel.Info, "Info"),
        (ConsoleLevel.Log, "Logs"), (ConsoleLevel.Debug, "Debug"),
    ];

    private readonly Dictionary<RequestCategory, ToggleButton> _categoryChips = [];
    private readonly Dictionary<ConsoleLevel, TextBlock> _levelCounts = [];
    private readonly HashSet<ConsoleLevel> _levels = [.. Enum.GetValues<ConsoleLevel>()];
    private ObservableCollection<NetworkRequest> _networkRows = [];
    private ObservableCollection<ConsoleLine> _consoleRows = [];
    private StickyScroll? _networkScroll;
    private StickyScroll? _consoleScroll;
    private PanelMode _panel;
    private RequestCategory _category = RequestCategory.All;
    private string[] _networkTerms = [];
    private string[] _consoleTerms = [];
    private string _networkFilterText = "";
    private string _consoleFilterText = "";
    private bool _preserveLog;
    private bool _panelRefreshQueued;
    private bool _syncingPanel;
    private double? _dragStartY;
    private double _dragStartHeight;

    private void InitializePanel()
    {
        NetworkList.ItemsSource = _networkRows;
        ConsoleList.ItemsSource = _consoleRows;
        _networkScroll = new StickyScroll(NetworkList);
        _consoleScroll = new StickyScroll(ConsoleList);
        BuildFilterChips();

        DevToolsPalette.IsDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        Root.Loaded += (_, _) => DevToolsPalette.IsDark = Root.ActualTheme == ElementTheme.Dark;
        Root.ActualThemeChanged += (_, _) =>
        {
            // Row colours are read when a row is bound, so a theme change rebinds every row.
            DevToolsPalette.IsDark = Root.ActualTheme == ElementTheme.Dark;
            RebuildRows();
        };
        Root.SizeChanged += (_, _) =>
        {
            if (DevToolsPanel.Visibility == Visibility.Visible) DevToolsPanel.Height = ClampPanelHeight(DevToolsPanel.Height);
        };
    }

    private void BuildFilterChips()
    {
        foreach (var (category, label) in Categories)
        {
            var chip = CreateChip(label, category == _category);
            chip.Click += (_, _) => SelectCategory(category);
            _categoryChips[category] = chip;
            NetworkFilters.Children.Add(chip);
        }

        foreach (var (level, label) in Levels)
        {
            var count = new TextBlock { Opacity = 0.7, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon
            {
                Glyph = level switch
                {
                    ConsoleLevel.Error => "",
                    ConsoleLevel.Warning => "",
                    ConsoleLevel.Info => "",
                    ConsoleLevel.Debug => "",
                    _ => "",
                },
                FontSize = 11,
                Foreground = DevToolsPalette.ConsoleIcon(level),
            });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(count);
            _levelCounts[level] = count;

            var chip = CreateChip(content, isChecked: true);
            chip.Click += (_, _) =>
            {
                if (chip.IsChecked == true) _levels.Add(level);
                else _levels.Remove(level);
                RebuildRows();
            };
            ConsoleFilters.Children.Add(chip);
        }
    }

    /// <summary>
    /// A filter chip: outlined when off, a light accent tint with an accent outline when on. The default checked look
    /// (solid accent) is too loud for a row of filters and hides the coloured level icons.
    /// </summary>
    private static ToggleButton CreateChip(object content, bool isChecked)
    {
        var chip = new ToggleButton { Content = content, Style = (Style)Application.Current.Resources["Chip"], IsChecked = isChecked };
        var resources = Application.Current.Resources;
        var accent = resources.TryGetValue("SystemAccentColorLight2", out var light) && light is Windows.UI.Color color ? color : DevToolsPalette.FromArgb(0xFF60A5FA);
        Brush Tint(double opacity) => new SolidColorBrush(accent) { Opacity = opacity };
        Brush Theme(string key) => (Brush)resources[key];

        chip.Resources["ToggleButtonBackground"] = DevToolsPalette.Transparent;
        chip.Resources["ToggleButtonBackgroundPointerOver"] = Theme("SubtleFillColorSecondaryBrush");
        chip.Resources["ToggleButtonBackgroundPressed"] = Theme("SubtleFillColorTertiaryBrush");
        chip.Resources["ToggleButtonBorderBrush"] = Theme("ControlStrokeColorDefaultBrush");
        chip.Resources["ToggleButtonBorderBrushPointerOver"] = Theme("ControlStrokeColorDefaultBrush");
        chip.Resources["ToggleButtonBorderBrushPressed"] = Theme("ControlStrokeColorDefaultBrush");
        chip.Resources["ToggleButtonBackgroundChecked"] = Tint(0.18);
        chip.Resources["ToggleButtonBackgroundCheckedPointerOver"] = Tint(0.26);
        chip.Resources["ToggleButtonBackgroundCheckedPressed"] = Tint(0.34);
        chip.Resources["ToggleButtonBorderBrushChecked"] = Tint(0.7);
        chip.Resources["ToggleButtonBorderBrushCheckedPointerOver"] = Tint(0.85);
        chip.Resources["ToggleButtonBorderBrushCheckedPressed"] = Tint(0.85);
        chip.Resources["ToggleButtonForegroundChecked"] = Theme("TextFillColorPrimaryBrush");
        chip.Resources["ToggleButtonForegroundCheckedPointerOver"] = Theme("TextFillColorPrimaryBrush");
        chip.Resources["ToggleButtonForegroundCheckedPressed"] = Theme("TextFillColorSecondaryBrush");
        return chip;
    }

    // ---- Opening, closing and switching ----------------------------------------------

    private void NetworkToggle_Click(object sender, RoutedEventArgs e) => TogglePanel(PanelMode.Network);

    private void ConsoleToggle_Click(object sender, RoutedEventArgs e) => TogglePanel(PanelMode.Console);

    private void PanelClose_Click(object sender, RoutedEventArgs e) => SetPanel(PanelMode.None);

    private void PanelTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingPanel || _panel == PanelMode.None) return;
        SetPanel(ReferenceEquals(sender.SelectedItem, ConsoleTabItem) ? PanelMode.Console : PanelMode.Network);
    }

    private void PanelClear_Click(object sender, RoutedEventArgs e)
    {
        if (_panel == PanelMode.Console) _active?.Log.ClearConsole();
        else _active?.Log.ClearNetwork();
    }

    private void PreserveToggle_Click(object sender, RoutedEventArgs e) => _preserveLog = PreserveToggle.IsChecked == true;

    /// <summary>Opens the panel in a mode, or closes it when that mode is already showing.</summary>
    private void TogglePanel(PanelMode mode) => SetPanel(_panel == mode ? PanelMode.None : mode);

    /// <summary>Shows the panel in a mode (or hides it) and keeps the toolbar buttons, the view tabs and the filters in step.</summary>
    private void SetPanel(PanelMode mode)
    {
        var changed = mode != _panel;
        _panel = mode;
        var network = mode == PanelMode.Network;
        var console = mode == PanelMode.Console;

        DevToolsPanel.Visibility = mode == PanelMode.None ? Visibility.Collapsed : Visibility.Visible;
        if (mode != PanelMode.None) DevToolsPanel.Height = ClampPanelHeight(DevToolsPanel.Height);
        NetworkToggle.IsChecked = network;
        ConsoleToggle.IsChecked = console;

        _syncingPanel = true;
        PanelTabs.SelectedItem = console ? ConsoleTabItem : NetworkTabItem;
        PanelFilter.Text = console ? _consoleFilterText : _networkFilterText;
        PanelFilter.PlaceholderText = console ? "Filter messages" : "Filter requests";
        _syncingPanel = false;

        NetworkFilters.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
        ConsoleFilters.Visibility = console ? Visibility.Visible : Visibility.Collapsed;
        NetworkColumns.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
        NetworkList.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
        ConsoleList.Visibility = console ? Visibility.Visible : Visibility.Collapsed;

        RebuildRows();
        if (changed) RelayoutAll();
    }

    // ---- Rows --------------------------------------------------------------------------

    /// <summary>Refills the visible list from the active tab's log with the current filter.</summary>
    private void RebuildRows()
    {
        var log = _active?.Log;
        if (_panel == PanelMode.Network)
        {
            _networkRows = new ObservableCollection<NetworkRequest>(log?.NetworkRequests.Where(MatchesNetwork) ?? []);
            NetworkList.ItemsSource = _networkRows;
            _networkScroll?.JumpToEnd();
        }
        else if (_panel == PanelMode.Console)
        {
            _consoleRows = new ObservableCollection<ConsoleLine>(log?.ConsoleLines.Where(MatchesConsole) ?? []);
            ConsoleList.ItemsSource = _consoleRows;
            _consoleScroll?.JumpToEnd();
        }

        RefreshPanelSummary();
    }

    private void OnLogAdded(object item)
    {
        switch (item)
        {
            case NetworkRequest request when _panel == PanelMode.Network && MatchesNetwork(request):
                _networkRows.Add(request);
                _networkScroll?.Follow();
                break;
            case ConsoleLine line when _panel == PanelMode.Console && MatchesConsole(line):
                _consoleRows.Add(line);
                _consoleScroll?.Follow();
                break;
        }
    }

    private void OnLogRemoved(object item)
    {
        if (item is NetworkRequest request) _networkRows.Remove(request);
        else if (item is ConsoleLine line) _consoleRows.Remove(line);
    }

    private bool MatchesNetwork(NetworkRequest request) =>
        (_category == RequestCategory.All || request.Category == _category)
        && Matches(_networkTerms, request.Url, request.Method, request.StatusLabel, request.TypeLabel, request.MimeType);

    private bool MatchesConsole(ConsoleLine line) =>
        _levels.Contains(line.Level) && Matches(_consoleTerms, line.Text, line.Source);

    /// <summary>Every term must appear in one of the fields; a term starting with '-' must appear in none.</summary>
    private static bool Matches(string[] terms, params string[] fields)
    {
        foreach (var term in terms)
        {
            var exclude = term.Length > 1 && term[0] == '-';
            var word = exclude ? term[1..] : term;
            var found = fields.Any(field => field.Contains(word, StringComparison.OrdinalIgnoreCase));
            if (found == exclude) return false;
        }

        return true;
    }

    private void SelectCategory(RequestCategory category)
    {
        _category = category;
        foreach (var (key, chip) in _categoryChips) chip.IsChecked = key == category;
        RebuildRows();
    }

    private void PanelFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPanel) return;
        var text = PanelFilter.Text;
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_panel == PanelMode.Console)
        {
            _consoleFilterText = text;
            _consoleTerms = terms;
        }
        else
        {
            _networkFilterText = text;
            _networkTerms = terms;
        }

        RebuildRows();
    }

    private void NetworkList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not NetworkRequest request) return;
        var core = _active is { IsClosed: false } tab ? tab.View.CoreWebView2 : null;
        RunSafe(() => RequestDialog.ShowAsync(Root.XamlRoot, request, core));
    }

    // ---- Summary, counts and empty state ---------------------------------------------

    private void QueuePanelRefresh()
    {
        if (_panelRefreshQueued) return;
        _panelRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _panelRefreshQueued = false;
            RefreshPanelSummary();
        });
    }

    /// <summary>Updates the header summary, the level counts, the console error badge, and the empty-list message.</summary>
    private void RefreshPanelSummary()
    {
        var log = _active?.Log;
        var errors = log?.ErrorCount ?? 0;
        ConsoleBadge.Value = errors;
        ConsoleBadge.Visibility = errors > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(ConsoleToggle, errors > 0 ? $"Console ({errors} error{(errors == 1 ? "" : "s")})" : "Console");
        ConsoleTabItem.Text = errors > 0 ? $"Console ({errors})" : "Console";

        if (_panel == PanelMode.None) return;

        if (_panel == PanelMode.Network)
        {
            var all = log?.NetworkRequests ?? [];
            var shown = _networkRows.Count;
            var bytes = _networkRows.Sum(r => r.TransferSize ?? 0);
            var failed = _networkRows.Count(r => r.IsFailed || r.Status >= 400);
            var count = shown == all.Count ? $"{shown} request{(shown == 1 ? "" : "s")}" : $"{shown} / {all.Count} requests";
            PanelSummary.Text = count + (bytes > 0 ? $" · {NetworkRequest.FormatSize(bytes)} transferred" : "") + (failed > 0 ? $" · {failed} failed" : "");
            ShowEmpty(shown == 0,
                "",
                all.Count == 0 ? "Recording network activity.\nReload the page to capture every request." : "No requests match the filter.",
                reload: all.Count == 0);
        }
        else
        {
            var lines = log?.ConsoleLines ?? [];
            foreach (var (level, text) in _levelCounts)
            {
                var n = lines.Where(line => line.Level == level).Sum(line => line.Count);
                text.Text = n > 0 ? n.ToString(CultureInfo.InvariantCulture) : "";
            }

            var shown = _consoleRows.Count;
            var warnings = log?.WarningCount ?? 0;
            var parts = new List<string> { shown == lines.Count ? $"{shown} message{(shown == 1 ? "" : "s")}" : $"{shown} / {lines.Count} messages" };
            if (errors > 0) parts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
            if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
            PanelSummary.Text = string.Join(" · ", parts);
            ShowEmpty(shown == 0, "", lines.Count == 0 ? "No console messages yet." : "No messages match the filter.", reload: false);
        }
    }

    private void ShowEmpty(bool empty, string glyph, string text, bool reload)
    {
        PanelEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        PanelEmptyIcon.Glyph = glyph;
        PanelEmptyText.Text = text;
        PanelEmptyReload.Visibility = reload && _active is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Resizing ------------------------------------------------------------------

    private double ClampPanelHeight(double height)
    {
        // The page keeps at least 120px; the panel at least its header, filters and a few rows.
        var max = Root.ActualHeight - TitleRow.ActualHeight - 44 - 120;
        return Math.Clamp(double.IsNaN(height) ? DefaultPanelHeight : height, MinPanelHeight, Math.Max(MinPanelHeight, max));
    }

    private void PanelSizer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!PanelSizer.CapturePointer(e.Pointer)) return;
        _dragStartY = e.GetCurrentPoint(Root).Position.Y;
        _dragStartHeight = DevToolsPanel.ActualHeight;
        e.Handled = true;
    }

    private void PanelSizer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStartY is not { } start) return;
        DevToolsPanel.Height = ClampPanelHeight(_dragStartHeight + start - e.GetCurrentPoint(Root).Position.Y);
        e.Handled = true;
    }

    private void PanelSizer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragStartY = null;
        PanelSizer.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void PanelSizer_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragStartY = null;

    private void PanelSizer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => DevToolsPanel.Height = ClampPanelHeight(DefaultPanelHeight);
}
