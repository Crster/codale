using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;

namespace Codale.Browser;

/// <summary>
/// Codale Browser: tabs over one shared WebView2 profile. Each agent session gets a tab of its own,
/// marked with the session's colour (see <see cref="ControlPipe"/>); tabs the user opens are plain.
/// The toolbar - address, breakpoint, passwords and passkeys, notifications, console, requests, element
/// picker - acts on the selected tab.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>The DevTools port agents attach to (McpDefaults.BrowserAttachPort in Codale.Mcp.Common).</summary>
    internal const int DebuggingPort = 9333;

    /// <summary>Session colours, from the app's palette; each open agent tab gets one no other agent tab is using.</summary>
    private static readonly Color[] AgentPalette =
    [
        Hex(0x0EA5E9), Hex(0xF59E0B), Hex(0x10B981), Hex(0x8B5CF6), Hex(0xEC4899), Hex(0xF97316), Hex(0x6366F1), Hex(0x14B8A6),
    ];

    private readonly List<BrowserTab> _tabs = [];
    private readonly Dictionary<string, Color> _agentColors = [];
    private readonly List<(Breakpoint? Choice, ToggleMenuFlyoutItem Item)> _breakpointItems = [];
    private readonly MenuFlyout _breakpointMenu = new();
    private readonly NotificationCenter _notifications = new();
    private readonly CredentialStore _credentials;
    private readonly PasskeySites _passkeys;
    private readonly SitePermissions _permissions;
    private readonly BookmarkStore _bookmarks;
    private readonly HistoryStore _history;
    private readonly ObservableCollection<DownloadRow> _downloads = [];
    private readonly HashSet<string> _declinedSaves = [];
    private readonly HashSet<string> _savePending = [];
    private readonly ControlPipe _pipe;
    private readonly IntPtr _hwnd;
    private CredentialsWindow? _credentialsWindow;
    private Task<CoreWebView2Environment>? _environment;
    private BrowserTab? _active;
    private string? _diagnosticsMode;
    private bool _picking;
    private bool _addressEdited;
    private bool _settingAddress;
    private bool _dark;
    private bool _windowActive = true;
    private bool _started;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Codale Browser";
        AppWindow.Resize(new SizeInt32(1280, 850));

        // The tab strip is the title bar; the caption buttons keep their place on the right.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        _credentials = new CredentialStore(BrowserPaths.Folder);
        _passkeys = new PasskeySites(BrowserPaths.Folder);
        _permissions = new SitePermissions(BrowserPaths.Folder);
        _bookmarks = new BookmarkStore(BrowserPaths.Folder);
        _history = new HistoryStore(BrowserPaths.Folder);
        AttachSidebar();
        MoreButton.Flyout = BuildMoreMenu();

        BuildBreakpointMenu();
        NotificationList.ItemsSource = _notifications.Items;
        _notifications.Items.CollectionChanged += (_, _) => NoNotifications.Visibility = _notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Diagnostics.CloseRequested += () =>
        {
            _diagnosticsMode = null;
            Diagnostics.Show(null);
        };

        ApplyTheme(InitialDarkTheme());
        _pipe = new ControlPipe(HandleControlAsync);

        Activated += (_, e) => _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        Root.SizeChanged += (_, _) => UpdateCaptionInset();
        Root.Loaded += (_, _) =>
        {
            UpdateCaptionInset();
            // TabView's built-in "+" button carries a "(Ctrl+T)" tooltip; replace it.
            if (FindByName(Tabs, "AddButton") is { } addButton)
                ToolTipService.SetToolTip(addButton, "New tab");
            if (!_started)
            {
                _started = true;
                RunSafe(StartAsync);
            }
        };
        Closed += (_, _) => Shutdown();
    }

    private static Color Hex(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private void Shutdown()
    {
        _pipe.Dispose();
        foreach (var tab in _tabs) tab.Dispose();
        _tabs.Clear();
    }

    /// <summary>Runs work started from an event, so a failure shows in the bell instead of crashing the window.</summary>
    private async void RunSafe(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _notifications.Post("Something went wrong", ex.Message);
        }
    }

    private async Task StartAsync()
    {
        try
        {
            // An agent that started this window asks for its own tab right away; the user's blank tab is only for an empty window.
            await Task.Delay(300);
            if (_tabs.Count == 0) await OpenUserTabAsync(null, focusAddress: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Prompts.MessageAsync(Root.XamlRoot, "Could not start WebView2",
                ex.Message + "\n\nWebView2 is part of Windows 11 and Microsoft Edge; install the WebView2 Runtime if it is missing.");
            Close();
        }
    }

    // ---- Window chrome --------------------------------------------------------------------

    private void UpdateCaptionInset()
    {
        var inset = AppWindow.TitleBar.RightInset;
        if (inset > 0) CaptionInset.Width = new GridLength(inset);
    }

    private static string? AppTheme() =>
        Environment.GetEnvironmentVariable("CODALE_APP_THEME") is { Length: > 0 } env ? env : LaunchHandoff.Get("theme");

    private static bool InitialDarkTheme() => AppTheme() switch
    {
        "Light" => false,
        "Dark" => true,
        _ => !IsSystemLightTheme(),
    };

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Light or dark for the window and every page: the pages' prefers-color-scheme follows the toggle
    /// instead of the OS setting, so a site's two styles can be checked from here.
    /// </summary>
    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Stage.Background = new SolidColorBrush(dark ? Hex(0x14151A) : Hex(0xE9EBEE));

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = dark ? Hex(0x3A3F48) : Hex(0xE5E7EB);
        titleBar.ButtonPressedBackgroundColor = dark ? Hex(0x454B55) : Hex(0xD1D5DB);
        titleBar.ButtonForegroundColor = dark ? Hex(0xF3F4F6) : Hex(0x1F2328);
        titleBar.ButtonHoverForegroundColor = dark ? Hex(0xFFFFFF) : Hex(0x111827);
        titleBar.ButtonPressedForegroundColor = dark ? Hex(0xFFFFFF) : Hex(0x111827);
        titleBar.ButtonInactiveForegroundColor = dark ? Hex(0x6B7280) : Hex(0x9CA3AF);

        ThemeButton.Content = dark ? "" : "";
        ToolTipService.SetToolTip(ThemeButton, dark ? "Switch to light mode" : "Switch to dark mode");
        foreach (var tab in _tabs)
        {
            if (tab.View.CoreWebView2 is { } core) core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e) => ApplyTheme(!_dark);

    private void SetFullScreen(bool on)
    {
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        TitleRow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Toolbar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateTitle()
    {
        Title = _active is null ? "Codale Browser"
            : $"{(_picking ? "Select an element — " : "")}{_active.Title} — Codale Browser";
    }

    // ---- Tabs ----------------------------------------------------------------------------

    private Color ColorFor(string agentKey)
    {
        if (_agentColors.TryGetValue(agentKey, out var known)) return known;
        var inUse = _tabs.Where(t => t.AgentColor is not null).Select(t => t.AgentColor!.Value).ToHashSet();
        var color = AgentPalette.FirstOrDefault(c => !inUse.Contains(c));
        if (color.A == 0) color = AgentPalette[_agentColors.Count % AgentPalette.Length];
        return _agentColors[agentKey] = color;
    }

    private Task<BrowserTab> OpenUserTabAsync(string? url, bool focusAddress) => CreateTabAsync(null, null, activate: true, url, focusAddress);

    private async Task<BrowserTab> CreateTabAsync(string? agentKey, string? agentLabel, bool activate, string? url = null, bool focusAddress = false)
    {
        var tab = new BrowserTab(agentKey, agentLabel, agentKey is null ? null : ColorFor(agentKey));
        _tabs.Add(tab);
        Tabs.TabItems.Add(tab.Item);
        Stage.Children.Add(tab.View);
        if (activate || _active is null) SelectTab(tab);
        else LayoutTab(tab);

        try
        {
            await tab.View.EnsureCoreWebView2Async(await EnvironmentAsync());
        }
        catch
        {
            RemoveTab(tab);
            throw;
        }

        var core = tab.View.CoreWebView2;
        core.Profile.PreferredColorScheme = _dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        WireCore(tab, core);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.Init);
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        if (tab.Breakpoint is not null) await ApplyBreakpointAsync(tab, reload: false);
        using (var info = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}")))
        {
            tab.TargetId = info.RootElement.GetProperty("targetInfo").GetProperty("targetId").GetString();
        }

        tab.RefreshHeader();
        if (url is not null) core.Navigate(url);
        if (focusAddress && ReferenceEquals(tab, _active))
        {
            Address.Focus(FocusState.Programmatic);
            Address.SelectAll();
        }

        return tab;
    }

    /// <summary>
    /// The one WebView2 environment every tab shares: the profile (logins, cookies) and the DevTools port that
    /// agents attach to. Created on first use; a failed attempt is forgotten so the next tab can try again.
    /// </summary>
    private async Task<CoreWebView2Environment> EnvironmentAsync()
    {
        _environment ??= CoreWebView2Environment.CreateWithOptionsAsync(null, BrowserPaths.Profile,
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--remote-debugging-port={DebuggingPort}" }).AsTask();
        try
        {
            return await _environment;
        }
        catch
        {
            _environment = null;
            throw;
        }
    }

    private void WireCore(BrowserTab tab, CoreWebView2 core)
    {
        core.SourceChanged += (_, _) =>
        {
            tab.RefreshHeader();
            if (ReferenceEquals(tab, _active)) SyncAddress();
            UpdateNavigationButtons();
        };
        core.HistoryChanged += (_, _) => UpdateNavigationButtons();
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.RefreshHeader();
            if (ReferenceEquals(tab, _active)) UpdateTitle();
        };
        core.NavigationStarting += (_, _) =>
        {
            if (ReferenceEquals(tab, _active))
            {
                _picking = false;
                UpdateTitle();
            }
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) return;
            RememberVisit(core);
            RunSafe(() => AutofillAsync(tab));
        };
        core.WebMessageReceived += (_, e) => OnPageMessage(tab, e);
        core.NewWindowRequested += (_, e) => OnNewWindow(tab, e);
        core.PermissionRequested += (_, e) => OnPermissionRequested(e);
        core.DownloadStarting += (_, e) => OnDownloadStarting(e);
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        tab.Diagnostics.Attach(core);
    }

    private void SelectTab(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        _active = tab;
        _picking = false;
        if (!ReferenceEquals(Tabs.SelectedItem, tab.Item)) Tabs.SelectedItem = tab.Item;
        Diagnostics.Bind(tab.Diagnostics);
        Diagnostics.Show(_diagnosticsMode);
        SyncAddress();
        ShowBreakpoint(tab.Breakpoint);
        RelayoutAll();
        UpdateTitle();
        UpdateNavigationButtons();
    }

    private void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        RemoveTab(tab);
        if (ReferenceEquals(tab, _active))
        {
            _active = null;
            if (_tabs.Count > 0) SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }

        if (_tabs.Count == 0) RunSafe(() => OpenUserTabAsync(null, focusAddress: true));
    }

    private void RemoveTab(BrowserTab tab)
    {
        _tabs.Remove(tab);
        Tabs.TabItems.Remove(tab.Item);
        Stage.Children.Remove(tab.View);
        tab.Dispose();
    }

    private void Cycle(int step)
    {
        if (_tabs.Count < 2 || _active is null) return;
        var index = (_tabs.IndexOf(_active) + step + _tabs.Count) % _tabs.Count;
        SelectTab(_tabs[index]);
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem { Tag: BrowserTab tab } && !ReferenceEquals(tab, _active)) SelectTab(tab);
    }

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is BrowserTab tab) CloseTab(tab);
    }

    private static FrameworkElement? FindByName(DependencyObject parent, string name)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindByName(child, name) is { } found) return found;
        }
        return null;
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => RunSafe(() => OpenUserTabAsync(null, focusAddress: true));

    /// <summary>
    /// Requests from agents (see <see cref="ControlPipe"/>), run on the UI thread. A new agent tab is
    /// selected only when the user is not looking at a page of their own, so an agent never pulls the
    /// window away from what the user is doing.
    /// </summary>
    private Task<object> HandleControlAsync(JsonElement request)
    {
        var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => _ = HandleOnUiAsync(request, done)))
        {
            done.TrySetResult(new { error = "Codale Browser is closing." });
        }

        return done.Task;
    }

    private async Task HandleOnUiAsync(JsonElement request, TaskCompletionSource<object> done)
    {
        try
        {
            var op = request.GetProperty("op").GetString();
            var session = request.GetProperty("session").GetString() ?? throw new InvalidOperationException("Missing session.");
            var tab = _tabs.FirstOrDefault(t => t.AgentKey == session);
            switch (op)
            {
                case "open":
                    var label = request.TryGetProperty("label", out var labelValue) ? labelValue.GetString() : null;
                    if (tab is null)
                    {
                        var userIsBusy = _active is { IsAgent: false } current && current.Url != "about:blank";
                        var blank = _active is { IsAgent: false } spare && spare.Url == "about:blank" && _tabs.Count == 1 ? spare : null;
                        tab = await CreateTabAsync(session, label ?? "Agent", activate: !userIsBusy);
                        if (blank is not null) CloseTab(blank);
                    }
                    else if (label is not null)
                    {
                        tab.AgentLabel = label;
                        tab.RefreshHeader();
                    }

                    done.TrySetResult(tab.TargetId is null
                        ? new { error = "The tab is still starting; try again." }
                        : new { targetId = tab.TargetId, port = DebuggingPort });
                    break;

                case "focus":
                    if (tab is not null) SelectTab(tab);
                    if (IsIconic(_hwnd)) ShowWindow(_hwnd, 9);
                    Activate();
                    SetForegroundWindow(_hwnd);
                    done.TrySetResult(new { ok = tab is not null });
                    break;

                default:
                    done.TrySetResult(new { error = $"Unknown op '{op}'." });
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            done.TrySetResult(new { error = ex.Message });
        }
    }

    // ---- Toolbar and keyboard -------------------------------------------------------------

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.View.CoreWebView2 is { CanGoBack: true } core) core.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.View.CoreWebView2 is { CanGoForward: true } core) core.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _active?.View.CoreWebView2?.Reload();

    private void UpdateNavigationButtons()
    {
        var core = _active?.View.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
    }

    /// <summary>Shows the selected tab's address, its connection state and whether it is bookmarked.</summary>
    private void SyncAddress()
    {
        if (_active is null) return;
        UpdateBookmarkStar();
        var url = _active.Url;
        var secure = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        SecurityIcon.Glyph = url == "about:blank" ? "" : secure ? "" : "";
        ToolTipService.SetToolTip(SecurityIcon, url == "about:blank" ? "New tab" : secure ? "Connection is secure (https)" : "Connection is not secure (http)");
        // While the user is typing in the address, keep their text; otherwise follow the page.
        if (Address.FocusState != FocusState.Unfocused && _addressEdited) return;
        _settingAddress = true;
        Address.Text = url == "about:blank" ? "" : url;
        _settingAddress = false;
        _addressEdited = false;
    }

    private void Address_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        _addressEdited = false;
        NavigateActive(Address.Text);
        _active?.View.Focus(FocusState.Programmatic);
    }

    private void Address_GotFocus(object sender, RoutedEventArgs e) => Address.SelectAll();

    private void NavigateActive(string input)
    {
        if (_active?.View.CoreWebView2 is not { } core) return;
        var text = input.Trim();
        if (text.Length == 0) return;
        if (text.Contains("://", StringComparison.Ordinal) && OriginOf(text) is null)
        {
            _notifications.Post("Can't open that address", "Enter an http or https address.");
            return;
        }

        core.Navigate(ToUrl(text));
    }

    /// <summary>
    /// What the address bar means: an address as typed (https unless it is a local development host), or a
    /// search when the text is not an address.
    /// </summary>
    internal static string ToUrl(string text)
    {
        if (text == "about:blank" || text.Contains("://", StringComparison.Ordinal)) return text;
        if (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || text.StartsWith("127.0.0.1", StringComparison.Ordinal) || text.StartsWith("[::1]", StringComparison.Ordinal))
            return "http://" + text;
        if (!text.Contains(' ') && text.Contains('.')) return "https://" + text;
        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
    }

    private void NewTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RunSafe(() => OpenUserTabAsync(null, focusAddress: true));
    }

    private void CloseTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_active is not null) CloseTab(_active);
    }

    private void Address_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Address.Focus(FocusState.Keyboard);
        Address.SelectAll();
    }

    private void NextTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Cycle(1);
    }

    private void PreviousTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Cycle(-1);
    }

    private void HardReload_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RunSafe(HardReloadAsync);
    }

    private void Credentials_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenCredentials(passkeys: false);
    }

    /// <summary>Reloads the page and everything it loads straight from the server, skipping the HTTP cache.</summary>
    private async Task HardReloadAsync()
    {
        if (_active?.View.CoreWebView2 is not { } core) return;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            core.Reload();
        }
    }

    private void Console_Click(object sender, RoutedEventArgs e) => ToggleDiagnostics("console");

    private void Requests_Click(object sender, RoutedEventArgs e) => ToggleDiagnostics("requests");

    private void ToggleDiagnostics(string mode)
    {
        _diagnosticsMode = _diagnosticsMode == mode ? null : mode;
        Diagnostics.Show(_diagnosticsMode);
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e) => _notifications.Clear();

    // ---- Breakpoints and layout --------------------------------------------------------------

    private void BuildBreakpointMenu()
    {
        AddBreakpointItem(null);
        _breakpointMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var breakpoint in Breakpoint.All) AddBreakpointItem(breakpoint);
        BreakpointButton.Flyout = _breakpointMenu;
    }

    private void AddBreakpointItem(Breakpoint? breakpoint)
    {
        var item = new ToggleMenuFlyoutItem { Text = breakpoint?.Label ?? "Fill window" };
        item.Click += (_, _) => ChooseBreakpoint(breakpoint);
        _breakpointMenu.Items.Add(item);
        _breakpointItems.Add((breakpoint, item));
    }

    private void ChooseBreakpoint(Breakpoint? choice)
    {
        if (_active is not { } tab) return;
        var reload = choice?.UserAgent != tab.Breakpoint?.UserAgent;
        tab.Breakpoint = choice;
        tab.AppliedFit = -1;
        ShowBreakpoint(choice);
        RunSafe(() => ApplyBreakpointAsync(tab, reload));
    }

    private void ShowBreakpoint(Breakpoint? choice)
    {
        foreach (var (breakpoint, item) in _breakpointItems) item.IsChecked = breakpoint == choice;
        ToolTipService.SetToolTip(BreakpointButton, choice is null ? "View breakpoint: fill window" : $"View breakpoint: {choice.Label}");
    }

    /// <summary>
    /// Where a tab's page sits on the stage, and how much a breakpoint is shrunk to fit. Without a
    /// breakpoint the page fills the stage; with one it is centred on the backdrop.
    /// </summary>
    private (double X, double Y, double Width, double Height, double Fit) Frame(BrowserTab tab)
    {
        var width = Stage.ActualWidth;
        var height = Stage.ActualHeight;
        if (tab.Breakpoint is not { } b) return (0, 0, width, height, 1);
        const double margin = 16;
        var fit = Math.Clamp(Math.Min((width - 2 * margin) / b.Width, (height - 2 * margin) / b.Height), 0.1, 1);
        var w = b.Width * fit;
        var h = b.Height * fit;
        return (Math.Max(0, (width - w) / 2), Math.Max(0, (height - h) / 2), w, h, fit);
    }

    /// <summary>
    /// Places a tab's page: the selected one on the stage, the rest just off it at their full size.
    /// Background pages stay rendered, so an agent working in a tab the user is not looking at keeps
    /// full-speed rendering, timers and screenshots.
    /// </summary>
    private double LayoutTab(BrowserTab tab)
    {
        var (x, y, width, height, fit) = Frame(tab);
        if (!ReferenceEquals(tab, _active)) x = -(x + width + 64);
        Canvas.SetLeft(tab.View, x);
        Canvas.SetTop(tab.View, y);
        tab.View.Width = Math.Max(1, width);
        tab.View.Height = Math.Max(1, height);
        return fit;
    }

    private void RelayoutAll()
    {
        foreach (var tab in _tabs)
        {
            var fit = LayoutTab(tab);
            // Only re-emulate when the scale actually changed, so resizing the window does not flood DevTools.
            if (tab.Breakpoint is not null && Math.Abs(fit - tab.AppliedFit) > 0.001) RunSafe(() => ApplyBreakpointAsync(tab, reload: false));
        }

        if (_active is not null) Canvas.SetZIndex(_active.View, 1);
    }

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => RelayoutAll();

    /// <summary>
    /// Emulates the tab's breakpoint on its page: CSS size, pixel ratio, touch and (for phone and tablet)
    /// user agent, shrunk to fit the window when bigger than the view. A new user agent only reaches the
    /// page's scripts and server after a reload.
    /// </summary>
    private async Task ApplyBreakpointAsync(BrowserTab tab, bool reload)
    {
        var fit = LayoutTab(tab);
        if (tab.View.CoreWebView2 is not { } core) return;
        var applied = tab.Breakpoint is null ? 1 : fit;
        if (!reload && Math.Abs(applied - tab.AppliedFit) < 0.001) return;
        tab.AppliedFit = applied;
        try
        {
            if (tab.Breakpoint is { } b)
            {
                await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", JsonSerializer.Serialize(new { width = b.Width, height = b.Height, deviceScaleFactor = b.Scale, mobile = b.Mobile, scale = fit }));
            }
            else
            {
                await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
            }

            var touch = tab.Breakpoint?.Mobile == true;
            await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", JsonSerializer.Serialize(new { enabled = touch, maxTouchPoints = touch ? 5 : 1 }));
            await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", JsonSerializer.Serialize(new { userAgent = tab.Breakpoint?.UserAgent ?? "" }));
            if (reload && !core.Source.StartsWith("about:", StringComparison.Ordinal)) core.Reload();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Debug.WriteLine(ex);
            tab.AppliedFit = -1;
        }
    }

    // ---- Page messages: element picker, logins, passkeys ---------------------------------------

    private void Pick_Click(object sender, RoutedEventArgs e) => RunSafe(PickAsync);

    private async Task PickAsync()
    {
        if (_active?.View.CoreWebView2 is not { } core) return;
        _picking = true;
        UpdateTitle();
        await core.ExecuteScriptAsync(PageScripts.Pick);
    }

    private void OnPageMessage(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var data = JsonDocument.Parse(e.WebMessageAsJson);
            var root = data.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "codale-pick" when ReferenceEquals(tab, _active) && _picking:
                    FinishPick(root);
                    break;

                case "codale-pick-cancel" when ReferenceEquals(tab, _active):
                    _picking = false;
                    UpdateTitle();
                    break;

                case "codale-credentials":
                    // Only a page's own top document may offer to save its logins: the origin it reports must be the one it is on.
                    var origin = root.GetProperty("origin").GetString();
                    if (origin is not null && OriginOf(tab.Url) == origin)
                    {
                        var username = root.GetProperty("username").GetString() ?? "";
                        var password = root.GetProperty("password").GetString() ?? "";
                        RunSafe(() => OfferSaveAsync(origin, username, password));
                    }

                    break;

                case "codale-passkey":
                    var site = root.GetProperty("origin").GetString();
                    var operation = root.GetProperty("op").GetString() ?? "get";
                    if (site is not null && OriginOf(tab.Url) == site)
                    {
                        _passkeys.Record(site, operation);
                        _notifications.Post(operation == "create" ? "Passkey created" : "Passkey sign-in", Prompts.Site(site));
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Debug.WriteLine(ex);
        }
    }

    private void FinishPick(JsonElement root)
    {
        _picking = false;
        UpdateTitle();
        try
        {
            var selectors = root.GetProperty("selectors").EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            var hosts = root.GetProperty("shadowHosts").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            var selector = selectors.FirstOrDefault() ?? throw new InvalidOperationException("Could not find a unique path for this element.");
            var path = string.Join(" >> ", hosts.Append(selector));
            CopyText(path);
            _notifications.Post("Element path copied", path);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _notifications.Post("Could not capture element", ex.Message);
        }
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>Offers to save a login the user submitted, unless it is already saved, the site is excluded, or the user declined it in this session.</summary>
    private async Task OfferSaveAsync(string origin, string username, string password)
    {
        if (_credentials.IsNever(origin)) return;
        var existing = _credentials.Find(origin, username);
        if (existing is not null && _credentials.Password(existing) == password) return;
        var key = origin + "\n" + username;
        if (existing is null && _declinedSaves.Contains(key)) return;
        if (!_savePending.Add(key)) return;
        try
        {
            switch (await Prompts.SavePasswordAsync(Root.XamlRoot, origin, username, existing is not null))
            {
                case SaveChoice.Save:
                    var saved = _credentials.Upsert(origin, username, password);
                    _notifications.Post(saved ? "Password saved" : "Password not saved",
                        $"{username} on {Prompts.Site(origin)}" + (saved ? "" : ". The password store could not be written."));
                    break;

                case SaveChoice.Never:
                    _credentials.SetNever(origin, true);
                    _notifications.Post("Won't offer to save here", Prompts.Site(origin));
                    break;

                default:
                    _declinedSaves.Add(key);
                    break;
            }
        }
        finally
        {
            _savePending.Remove(key);
        }
    }

    /// <summary>Fills the page with the only saved login for its site, shortly after it loads. Several logins: the key button asks which.</summary>
    private async Task AutofillAsync(BrowserTab tab)
    {
        if (tab.View.CoreWebView2 is not { } core) return;
        var origin = OriginOf(core.Source);
        if (origin is null) return;
        var logins = _credentials.ForOrigin(origin);
        if (logins.Count != 1) return;
        await Task.Delay(150);
        if (OriginOf(core.Source) == origin) PostFill(core, logins[0]);
    }

    private void PostFill(CoreWebView2 core, CredentialEntry entry)
    {
        var secret = _credentials.Password(entry);
        if (secret is null)
        {
            _notifications.Post("Could not read a saved password", entry.Site);
            return;
        }

        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "codale-fill", username = entry.Username, password = secret }));
    }

    private void Fill_Click(object sender, RoutedEventArgs e) => RunSafe(async () =>
    {
        if (_active?.View.CoreWebView2 is not { } core) return;
        var origin = OriginOf(core.Source);
        var logins = origin is null ? new List<CredentialEntry>() : _credentials.ForOrigin(origin).ToList();
        if (logins.Count == 0)
        {
            _notifications.Post("No saved login", $"Nothing is saved for {Prompts.Site(origin ?? core.Source)}.");
            return;
        }

        var choice = logins.Count == 1 ? logins[0] : await Prompts.ChooseAccountAsync(Root.XamlRoot, logins);
        if (choice is not null) PostFill(core, choice);
    });

    private Task FillFromManagerAsync(CredentialEntry entry)
    {
        if (_active?.View.CoreWebView2 is not { } core) return Task.CompletedTask;
        if (OriginOf(core.Source) != entry.Origin)
        {
            _notifications.Post("Open the site to fill it", $"{entry.Username} is saved for {entry.Site}; this tab is on another site.");
            return Task.CompletedTask;
        }

        PostFill(core, entry);
        return Task.CompletedTask;
    }

    private void Credentials_Click(object sender, RoutedEventArgs e) => OpenCredentials(passkeys: false);

    private void OpenCredentials(bool passkeys)
    {
        if (_credentialsWindow is null)
        {
            _credentialsWindow = new CredentialsWindow(_credentials, _passkeys, FillFromManagerAsync, passkeys);
            _credentialsWindow.Closed += (_, _) => _credentialsWindow = null;
        }

        _credentialsWindow.Activate();
    }

    private static string? OriginOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https") ? uri.GetLeftPart(UriPartial.Authority) : null;

    // ---- Permissions, downloads, new windows ----------------------------------------------

    private void OnNewWindow(BrowserTab tab, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (OriginOf(e.Uri) is null) return;

        // An agent's pop-up stays in its tab, so the agent keeps driving it; the user's opens a new tab.
        if (tab.IsAgent) tab.View.CoreWebView2?.Navigate(e.Uri);
        else RunSafe(() => OpenUserTabAsync(e.Uri, focusAddress: false));
    }

    /// <summary>
    /// A site asks for a permission. Kinds the browser cares about are answered from the saved choice or by
    /// asking the user in a native dialog; the rest keep WebView2's own handling.
    /// </summary>
    private void OnPermissionRequested(CoreWebView2PermissionRequestedEventArgs e)
    {
        var what = SitePermissions.Describe(e.PermissionKind);
        if (what is null) return;
        var origin = OriginOf(e.Uri);
        if (origin is null)
        {
            e.State = CoreWebView2PermissionState.Deny;
            return;
        }

        if (_permissions.Get(origin, e.PermissionKind) is { } remembered)
        {
            e.State = remembered ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            return;
        }

        var kind = e.PermissionKind;
        var deferral = e.GetDeferral();
        RunSafe(async () =>
        {
            try
            {
                var answer = await Prompts.PermissionAsync(Root.XamlRoot, origin, what);
                if (answer.Remember) _permissions.Set(origin, kind, answer.Allow);
                e.State = answer.Allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                _notifications.Post(answer.Allow ? "Allowed" : "Blocked", $"{Prompts.Site(origin)} · {what}");
            }
            finally
            {
                deferral.Complete();
            }
        });
    }
}
