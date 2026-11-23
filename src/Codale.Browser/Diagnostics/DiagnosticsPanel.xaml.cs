using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Codale.Browser;

/// <summary>
/// The Console and Requests panel under the page. It shows the selected tab's <see cref="DiagnosticsFeed"/>.
/// Chips above the list filter it by category (with counts); a request opens its request, response and timing
/// beside the list, and a console message shows its full text and where it was logged from.
/// </summary>
public sealed partial class DiagnosticsPanel : UserControl
{
    private static readonly string[] NetworkChips = ["All", "Doc", "JS", "CSS", "Img", "Font", "XHR", "Other", "Failed"];
    private static readonly string[] ConsoleChips = ["All", "Errors", "Warnings", "Info", "Logs"];

    private readonly Dictionary<string, Button> _chipButtons = [];
    private DiagnosticsFeed? _feed;
    private NetworkRow? _selected;
    private string _mode = "requests";
    private string _chip = "All";
    private int _tab;
    private string _requestText = "";
    private string _responseText = "";
    private string _timingText = "";

    public DiagnosticsPanel()
    {
        InitializeComponent();
        BuildChips();
    }

    /// <summary>Raised when the close button is pressed; the window hides the panel.</summary>
    public event Action? CloseRequested;

    /// <summary>Shows the given tab's history. A null feed empties the panel.</summary>
    public void Bind(DiagnosticsFeed? feed)
    {
        if (_feed is not null)
        {
            _feed.Network.Changed -= OnFeedChanged;
            _feed.Console.Changed -= OnFeedChanged;
            _feed.RequestUpdated -= OnRequestUpdated;
        }

        _feed = feed;
        NetworkList.ItemsSource = feed?.Network.Visible;
        ConsoleList.ItemsSource = feed?.Console.Visible;
        if (feed is not null)
        {
            feed.Network.Changed += OnFeedChanged;
            feed.Console.Changed += OnFeedChanged;
            feed.RequestUpdated += OnRequestUpdated;
            feed.SetFilter(FilterBox.Text.Trim());
            ApplyChip();
        }

        CloseDetails();
        UpdateHeader();
    }

    /// <summary>Shows the Requests or Console view, or hides the panel when <paramref name="mode"/> is null.</summary>
    public void Show(string? mode)
    {
        if (mode is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        if (mode != _mode)
        {
            _mode = mode;
            _chip = "All";
            ApplyChip();
        }

        Visibility = Visibility.Visible;
        NetworkPane.Visibility = mode == "requests" ? Visibility.Visible : Visibility.Collapsed;
        ConsolePane.Visibility = mode == "console" ? Visibility.Visible : Visibility.Collapsed;
        if (mode != "requests") CloseDetails();
        TitleText.Text = mode == "console" ? "CONSOLE" : "REQUESTS";
        BuildChips();
        UpdateHeader();
    }

    // ---- Chips -----------------------------------------------------------------------

    private static bool NetworkMatches(string chip, NetworkRow row) => chip switch
    {
        "All" => true,
        "Failed" => row.Failed,
        _ => row.Kind == chip,
    };

    private static bool ConsoleMatches(string chip, ConsoleRow row) => chip switch
    {
        "All" => true,
        "Errors" => row.Level is "error" or "assert",
        "Warnings" => row.Level == "warning",
        "Info" => row.Level == "info",
        _ => row.Level is not ("error" or "assert" or "warning" or "info"),
    };

    private void ApplyChip()
    {
        if (_feed is null) return;
        var chip = _chip;
        _feed.Network.SetPredicate(row => NetworkMatches(chip, row));
        _feed.Console.SetPredicate(row => ConsoleMatches(chip, row));
    }

    private void BuildChips()
    {
        Chips.Children.Clear();
        _chipButtons.Clear();
        foreach (var label in _mode == "console" ? ConsoleChips : NetworkChips)
        {
            var button = new Button { Padding = new Microsoft.UI.Xaml.Thickness(10, 2, 10, 2), MinHeight = 0, CornerRadius = new Microsoft.UI.Xaml.CornerRadius(12), FontSize = 12 };
            button.Click += (_, _) =>
            {
                _chip = label;
                ApplyChip();
                UpdateHeader();
            };
            Chips.Children.Add(button);
            _chipButtons[label] = button;
        }

        UpdateChips();
    }

    private void UpdateChips()
    {
        var accent = Application.Current.Resources["AccentButtonStyle"] as Style;
        foreach (var (label, button) in _chipButtons)
        {
            var count = _feed is null ? 0
                : _mode == "console" ? _feed.Console.All.Count(row => ConsoleMatches(label, row))
                : _feed.Network.All.Count(row => NetworkMatches(label, row));
            button.Content = $"{label}  {count}";
            button.Style = label == _chip ? accent : null;
        }
    }

    // ---- Header and summary ---------------------------------------------------------------

    private void OnFeedChanged() => UpdateHeader();

    private void UpdateHeader()
    {
        var total = _mode == "console" ? _feed?.Console.Total ?? 0 : _feed?.Network.Total ?? 0;
        var shown = _mode == "console" ? _feed?.Console.Visible.Count ?? 0 : _feed?.Network.Visible.Count ?? 0;
        CountText.Text = total == 0 ? "" : shown == total ? total.ToString() : $"{shown} of {total}";
        NetworkEmpty.Visibility = (_feed?.Network.Total ?? 0) == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConsoleEmpty.Visibility = (_feed?.Console.Total ?? 0) == 0 ? Visibility.Visible : Visibility.Collapsed;
        NetworkSummary.Text = Summary(_feed);
        UpdateChips();
    }

    /// <summary>Totals for every request the tab has made: count, bytes, time from the first request to the last to finish.</summary>
    private static string Summary(DiagnosticsFeed? feed)
    {
        if (feed is null || feed.Network.Total == 0) return "";
        var rows = feed.Network.All;
        var bytes = rows.Sum(r => r.Size ?? 0);
        var started = rows.Min(r => r.Start);
        var ended = rows.Where(r => r.Finished).Select(r => r.End).DefaultIfEmpty(started).Max();
        var failed = rows.Count(r => r.Failed);
        return $"{(rows.Count == 1 ? "1 request" : rows.Count + " requests")} · {FormatBytes(bytes)} transferred · {Math.Max(0, ended - started):0.00} s"
            + (failed > 0 ? $" · {failed} failed" : "");
    }

    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB"
        : $"{bytes / 1048576d:0.0} MB";

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => _feed?.SetFilter(FilterBox.Text.Trim());

    private void FilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        FilterBox.Text = "";
        e.Handled = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _feed?.Clear();
        NetworkList.SelectedItem = null;
        ConsoleList.SelectedItem = null;
        CloseDetails();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // ---- Console ---------------------------------------------------------------------

    private void ConsoleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConsoleList.SelectedItem is ConsoleRow log)
        {
            ConsoleDetail.Text = log.DetailText;
            ConsoleDetail.Visibility = Visibility.Visible;
        }
        else
        {
            ConsoleDetail.Visibility = Visibility.Collapsed;
        }
    }

    // ---- Requests ---------------------------------------------------------------------

    private void NetworkList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = NetworkList.SelectedItem as NetworkRow;
        if (_selected is null)
        {
            CloseDetails();
            return;
        }

        DetailColumn.Width = new Microsoft.UI.Xaml.GridLength(400);
        DetailPane.Visibility = Visibility.Visible;
        _ = ShowSelectedAsync();
    }

    private void CloseDetails_Click(object sender, RoutedEventArgs e)
    {
        NetworkList.SelectedItem = null;
        CloseDetails();
    }

    private void CloseDetails()
    {
        _selected = null;
        _requestText = _responseText = _timingText = "";
        DetailColumn.Width = new Microsoft.UI.Xaml.GridLength(0);
        DetailPane.Visibility = Visibility.Collapsed;
    }

    private void OnRequestUpdated(NetworkRow entry)
    {
        if (ReferenceEquals(entry, _selected)) _ = ShowSelectedAsync();
    }

    private async Task ShowSelectedAsync()
    {
        var entry = _selected;
        var feed = _feed;
        if (entry is null || feed is null) return;
        RenderDetail(entry, feed);
        await feed.LoadResponseBodyAsync(entry);
        if (ReferenceEquals(_selected, entry)) RenderDetail(entry, feed);
    }

    private void RenderDetail(NetworkRow entry, DiagnosticsFeed feed)
    {
        var query = Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) && uri.Query.Length > 1
            ? string.Join(Environment.NewLine, uri.Query[1..].Split('&').Select(p => Uri.UnescapeDataString(p.Replace('+', ' '))))
            : "(none)";
        _requestText = $"{entry.Method} {entry.Url}\r\n\r\nQUERY PARAMETERS\r\n{query}\r\n\r\nREQUEST HEADERS\r\n{entry.RequestHeaders}\r\n\r\nREQUEST BODY\r\n{(entry.RequestBody.Length > 0 ? entry.RequestBody : "(none or unavailable)")}";
        _responseText = $"STATUS\r\n{entry.StatusText}\r\n\r\nRESPONSE HEADERS\r\n{entry.ResponseHeaders}\r\n\r\nRESPONSE BODY\r\n"
            + (entry.ResponseBody.Length > 0 ? Pretty(entry.ResponseBody, entry.Mime) : entry.Finished ? "(empty, binary, too large, or unavailable)" : "(pending)");
        _timingText = Timing(entry, feed);
        ShowDetailText();
    }

    private static string Timing(NetworkRow entry, DiagnosticsFeed feed)
    {
        var origin = feed.Network.All.Count > 0 ? feed.Network.All.Min(r => r.Start) : entry.Start;
        var duration = entry.Finished && entry.End >= entry.Start ? $"{(entry.End - entry.Start) * 1000:0} ms" : "still loading";
        return $"Method       {entry.Method}\r\n"
            + $"Status       {entry.StatusText}\r\n"
            + $"Type         {entry.TypeText}\r\n"
            + $"Kind         {entry.Kind}\r\n"
            + $"Started      +{entry.Start - origin:0.000} s after the first request\r\n"
            + $"Duration     {duration}\r\n"
            + $"Transferred  {entry.SizeText}";
    }

    /// <summary>JSON bodies are indented so they read; anything else, or anything that does not parse, is shown as it arrived.</summary>
    private static string Pretty(string body, string mime)
    {
        if (!mime.Contains("json", StringComparison.OrdinalIgnoreCase)) return body;
        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private void ShowDetailText()
    {
        if (DetailText is null) return;
        var text = _tab switch
        {
            1 => _responseText,
            2 => _timingText,
            _ => _requestText,
        };
        if (DetailText.Text != text) DetailText.Text = text;
    }

    private void DetailTab_Checked(object sender, RoutedEventArgs e)
    {
        _tab = ResponseTab?.IsChecked == true ? 1 : TimingTab?.IsChecked == true ? 2 : 0;
        ShowDetailText();
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } entry) CopyText(entry.Url);
    }

    private void CopyCurl_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } entry) CopyText(Curl(entry));
    }

    /// <summary>The request as a curl command, with its method, headers and body, so it can be replayed from a terminal.</summary>
    private static string Curl(NetworkRow entry)
    {
        var command = new StringBuilder($"curl {Quote(entry.Url)}");
        if (entry.Method.Length > 0 && entry.Method != "GET") command.Append($" -X {entry.Method}");
        foreach (var line in entry.RequestHeaders.Split(Environment.NewLine))
        {
            if (line.Contains(':')) command.Append($" -H {Quote(line)}");
        }

        if (entry.RequestBody.Length > 0) command.Append($" --data-raw {Quote(entry.RequestBody)}");
        return command.ToString();
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
