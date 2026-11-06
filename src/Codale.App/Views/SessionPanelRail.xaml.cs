using System.Collections.ObjectModel;
using System.ComponentModel;

using Codale.App.Services;
using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Codale.App.Views;

/// <summary>
/// The collapsed form shared by <see cref="AgentSessionPanel"/> and <see cref="TerminalAgentSessionPanel"/>:
/// a narrow summary of the expanded panel. Whether the panels are collapsed is one app
/// setting, so every session panel follows the same choice and it survives a restart.
/// </summary>
public sealed partial class SessionPanelRail : UserControl, INotifyPropertyChanged
{
    /// <summary>The whole panel's width when collapsed / expanded (the card's 8px right margin included).</summary>
    public const double CollapsedWidth = 62;
    public const double ExpandedWidth = 380;

    private ChatViewModel? _session;
    private StatusViewModel? _status;
    private CliUpdateViewModel? _cliUpdate;
    private ObservableCollection<UsageMeter> _meters = [];
    private bool _metersFromStatus;

    // The chat panel's two vitals, kept in step with the session's status.
    private readonly UsageMeter _contextMeter = new("Context", "", 0);
    private readonly UsageMeter _usageMeter = new("Usage", "", 0);

    public SessionPanelRail()
    {
        InitializeComponent();

        // Only listen to the status while on screen, so a closed tab's rail is not kept alive by its chat.
        Loaded += (_, _) => WatchStatus(_session?.Status);
        Unloaded += (_, _) => WatchStatus(null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The expand button or a badge was clicked.</summary>
    public event EventHandler? ExpandRequested;

    /// <summary>The CLI update indicator was clicked.</summary>
    public event RoutedEventHandler? UpdateRequested;

    public ChatViewModel? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value))
            {
                return;
            }

            _session = value;
            WatchStatus(value?.Status);
            Raise(nameof(Session));
        }
    }

    public CliUpdateViewModel? CliUpdate
    {
        get => _cliUpdate;
        set
        {
            if (!ReferenceEquals(_cliUpdate, value))
            {
                _cliUpdate = value;
                Raise(nameof(CliUpdate));
            }
        }
    }

    /// <summary>The vitals shown as rings. A terminal panel passes its own; a chat panel's come from the session status.</summary>
    public ObservableCollection<UsageMeter> Meters
    {
        get => _meters;
        set
        {
            _metersFromStatus = false;
            _meters = value;
            Raise(nameof(Meters));
        }
    }

    /// <summary>Fills <see cref="Meters"/> with context and usage from the session's status (the chat panel's vitals).</summary>
    public void UseStatusMeters()
    {
        _metersFromStatus = true;
        _meters = [_contextMeter, _usageMeter];
        RefreshStatusMeters();
        Raise(nameof(Meters));
    }

    /// <summary>The same glyph as the expanded panel's header icon.</summary>
    public string Glyph
    {
        get => RailIcon.Glyph;
        set => RailIcon.Glyph = value;
    }

    /// <summary>Whether the connection dot applies to this kind of session.</summary>
    public bool ShowConnection
    {
        get => ConnectionDot.Visibility == Visibility.Visible;
        set => ConnectionDot.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the session has a model and mode to show (chat sessions; a terminal CLI picks its own).</summary>
    public bool ShowModel
    {
        get => ModelBlock.Visibility == Visibility.Visible;
        set => ModelBlock.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public static bool IsCollapsed => AppSettings.SessionPanelCollapsed;

    public static void Toggle() => AppSettings.SessionPanelCollapsed = !AppSettings.SessionPanelCollapsed;

    /// <summary>Shows the rail or the expanded content of <paramref name="panel"/>, and sizes the panel to match.</summary>
    public void Apply(UserControl panel, UIElement expanded)
    {
        var collapsed = IsCollapsed;
        expanded.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        panel.Width = collapsed ? CollapsedWidth : ExpandedWidth;
    }

    // ---- x:Bind helpers -------------------------------------------------------------------

    public static double Dim(int count) => count > 0 ? 1 : 0.4;

    public static string PercentText(double percent) => $"{percent:0}%";

    /// <summary>"Context" → "CTX", "5-hour" → "5H", "Weekly" → "WEEK", "Usage" stays: a caption that fits the rail.</summary>
    public static string ShortLabel(string? label)
    {
        var text = (label ?? "").Trim();
        if (text.Equals("Context", StringComparison.OrdinalIgnoreCase))
        {
            return "CTX";
        }

        var compact = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (compact.Length > 1 && char.IsDigit(compact[0]))
        {
            // "5HOUR" → "5H", "7DAY" → "7D"
            var digits = new string(compact.TakeWhile(char.IsDigit).ToArray());
            return digits + compact[digits.Length..].FirstOrDefault();
        }

        return compact.Length <= 5 ? compact : compact[..4];
    }

    public static string MeterTip(string? label, string? summary) =>
        string.IsNullOrWhiteSpace(summary) ? label ?? "" : $"{label}: {summary}";

    public static string ModelTip(string? model, string? mode) => $"Model: {model}\nMode: {mode}";

    // ---- Status meters --------------------------------------------------------------------

    private void WatchStatus(StatusViewModel? status)
    {
        if (_status is not null)
        {
            _status.PropertyChanged -= OnStatusChanged;
        }

        _status = status;
        if (_status is not null)
        {
            _status.PropertyChanged += OnStatusChanged;
        }

        RefreshStatusMeters();
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e) => RefreshStatusMeters();

    private void RefreshStatusMeters()
    {
        if (!_metersFromStatus)
        {
            return;
        }

        var s = _status;
        _contextMeter.Percent = s?.ContextPercent ?? 0;
        _contextMeter.Summary = s?.ContextSummary ?? "";

        // The same usage window the expanded panel shows.
        if (s is null)
        {
            (_usageMeter.Label, _usageMeter.Percent, _usageMeter.Summary) = ("Usage", 0, "");
        }
        else if (s.HasFiveHour)
        {
            (_usageMeter.Label, _usageMeter.Percent, _usageMeter.Summary) = ("5-hour", s.FiveHourPercent, s.FiveHourSummary);
        }
        else if (s.HasLongWindowOnly)
        {
            (_usageMeter.Label, _usageMeter.Percent, _usageMeter.Summary) = ("7-day", s.SevenDayPercent, s.SevenDaySummary);
        }
        else if (s.HasTokenUsage)
        {
            (_usageMeter.Label, _usageMeter.Percent, _usageMeter.Summary) = ("Tokens", s.TokenUsagePercent, s.TokenUsageSummary);
        }
        else
        {
            (_usageMeter.Label, _usageMeter.Percent, _usageMeter.Summary) = ("Usage", 0, "No usage yet");
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnExpandClick(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke(this, EventArgs.Empty);

    private void OnUpdateClick(object sender, RoutedEventArgs e) => UpdateRequested?.Invoke(sender, e);
}
