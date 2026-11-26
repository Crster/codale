using System.Runtime.InteropServices;
using System.Text.Json;

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Codale.Browser;

/// <summary>
/// A small tabbed WebView2 host. Automation connects to WebView2's built-in DevTools endpoint;
/// this window only creates tabs, exposes target IDs, and lets a person navigate and focus pages.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>The local DevTools port the automation client attaches to.</summary>
    internal const int DebuggingPort = 9333;

    private static readonly Color[] AgentPalette =
    [
        Hex(0x0EA5E9), Hex(0xF59E0B), Hex(0x10B981), Hex(0x8B5CF6),
        Hex(0xEC4899), Hex(0xF97316), Hex(0x6366F1), Hex(0x14B8A6),
    ];

    private readonly List<BrowserTab> _tabs = [];
    private readonly Dictionary<string, Color> _agentColors = [];
    private readonly ControlPipe _pipe;
    private Task<CoreWebView2Environment>? _environment;
    private BrowserTab? _active;
    private bool _addressEdited;
    private bool _settingAddress;
    private bool _started;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTools();
        Title = "Codale Browser";
        AppWindow.Resize(new SizeInt32(1280, 850));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        _pipe = new ControlPipe(HandleControlAsync);
        Root.SizeChanged += (_, _) => UpdateCaptionInset();
        Root.Loaded += (_, _) =>
        {
            UpdateCaptionInset();
            if (_started) return;
            _started = true;
            RunSafe(StartAsync);
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

    private async void RunSafe(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // The tab the operation belonged to was closed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing to report: the failed operation is dropped and the window keeps running.
        }
    }

    private async Task StartAsync()
    {
        try
        {
            // Let an agent's open request arrive before creating a user-only blank tab.
            await Task.Delay(300);
            if (_tabs.Count == 0) await OpenUserTabAsync(null, focusAddress: true);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException) && _tabs.Count == 0)
        {
            await Prompts.MessageAsync(Root.XamlRoot, "Could not start WebView2",
                ex.Message + "\n\nInstall the WebView2 Runtime and Windows App Runtime 2.7, then try again.");
            Close();
        }
    }

    private void UpdateCaptionInset()
    {
        var inset = AppWindow.TitleBar.RightInset;
        if (inset > 0) CaptionInset.Width = new GridLength(inset);
    }

    private void UpdateTitle() => Title = _active switch
    {
        { Project: { } project } tab => $"{tab.Title} — {project.Name} — Codale Browser",
        { } tab => $"{tab.Title} — Codale Browser",
        _ => "Codale Browser",
    };

    private Color ColorFor(string agentKey)
    {
        if (_agentColors.TryGetValue(agentKey, out var known)) return known;
        var inUse = _tabs.Where(t => t.AgentColor is not null).Select(t => t.AgentColor!.Value).ToHashSet();
        var color = AgentPalette.FirstOrDefault(c => !inUse.Contains(c));
        if (color.A == 0) color = AgentPalette[_agentColors.Count % AgentPalette.Length];
        return _agentColors[agentKey] = color;
    }

    private Task<BrowserTab> OpenUserTabAsync(string? url, bool focusAddress, BrowserProject? project = null, bool isPrivate = false) =>
        CreateTabAsync(project, null, null, activate: true, url, focusAddress, isPrivate);

    private async Task<BrowserTab> CreateTabAsync(BrowserProject? project, string? agentKey, string? agentLabel, bool activate, string? url = null, bool focusAddress = false, bool isPrivate = false)
    {
        var tab = new BrowserTab(project, agentKey, agentLabel, agentKey is null ? null : ColorFor(agentKey), isPrivate);
        _tabs.Add(tab);
        Tabs.TabItems.Add(tab.Item);
        Stage.Children.Add(tab.View);
        if (activate || _active is null) SelectTab(tab);
        else LayoutTab(tab);

        try
        {
            var environment = await EnvironmentAsync();
            if (tab.IsClosed) throw new OperationCanceledException("The tab was closed.");

            CoreWebView2ControllerOptions? options = null;
            if ((tab.PrivateProfileName ?? project?.Key) is { } profileName)
            {
                options = environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = profileName;
                options.IsInPrivateModeEnabled = tab.IsPrivate;
            }
            var initialize = options is null ? tab.View.EnsureCoreWebView2Async(environment)
                : tab.View.EnsureCoreWebView2Async(environment, options);
            await initialize.AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            if (tab.IsClosed || tab.View.CoreWebView2 is not { } core)
                throw new OperationCanceledException("The tab was closed.");

            if (tab.IsPrivate && (!core.Profile.IsInPrivateModeEnabled || core.Profile.ProfileName != tab.PrivateProfileName))
                throw new InvalidOperationException("WebView2 could not create an isolated private profile.");

            WireCore(tab, core);
            if (!tab.IsPrivate)
            {
                using var info = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}"));
                tab.TargetId = info.RootElement.GetProperty("targetInfo").GetProperty("targetId").GetString();
            }

            if (tab.IsClosed) throw new OperationCanceledException("The tab was closed.");
            tab.RefreshHeader();
            if (url is not null) core.Navigate(url);
            tab.Ready.TrySetResult();
        }
        catch (Exception ex)
        {
            tab.Ready.TrySetException(ex);
            if (_tabs.Contains(tab)) CloseTab(tab, reopen: false);
            throw;
        }

        if (focusAddress && ReferenceEquals(tab, _active))
        {
            Address.Focus(FocusState.Programmatic);
            Address.SelectAll();
        }

        return tab;
    }

    /// <summary>One WebView2 environment and DevTools endpoint for all profiles.</summary>
    private async Task<CoreWebView2Environment> EnvironmentAsync()
    {
        var pending = _environment ??= CoreWebView2Environment.CreateWithOptionsAsync(null, BrowserPaths.UserDataFolder,
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--remote-debugging-port={DebuggingPort}" }).AsTask();
        try
        {
            return await pending;
        }
        catch
        {
            if (ReferenceEquals(_environment, pending)) _environment = null;
            throw;
        }
    }

    private void WireCore(BrowserTab tab, CoreWebView2 core)
    {
        // Pages stay at 100%: no Ctrl+wheel, Ctrl+plus/minus or pinch zoom (it would also skew breakpoint previews).
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.ProcessFailed += (_, e) => OnProcessFailed(tab, e);
        core.SourceChanged += (_, _) =>
        {
            tab.RefreshHeader();
            if (ReferenceEquals(tab, _active)) SyncAddress();
            UpdateNavigationButtons();
        };
        core.HistoryChanged += (_, _) => UpdateNavigationButtons();
        WireTools(tab, core);
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.RefreshHeader();
            if (ReferenceEquals(tab, _active)) UpdateTitle();
        };
        core.NewWindowRequested += (_, e) => OnNewWindow(tab, e);
    }

    private void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        if (tab.IsClosed) return;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                var project = _active is { } active ? active.Project : tab.Project;
                _environment = null;
                foreach (var dead in _tabs.ToList()) RemoveTab(dead);
                _active = null;
                RunSafe(() => OpenUserTabAsync(null, focusAddress: false, project));
                break;

            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                try
                {
                    tab.View.CoreWebView2?.Reload();
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    // The view is already gone; the next navigation recreates it.
                }

                break;
        }
    }

    private void SelectTab(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        var previous = _active;
        _active = tab;
        if (!ReferenceEquals(Tabs.SelectedItem, tab.Item)) Tabs.SelectedItem = tab.Item;

        if (!ReferenceEquals(previous, tab))
        {
            if (PickToggle.IsChecked == true) StopPick(previous);
            _addressEdited = false;
        }
        SyncAddress();
        OnActiveTabChanged();
        RelayoutAll();
        UpdateTitle();
        UpdateNavigationButtons();
    }

    private void CloseTab(BrowserTab tab, bool reopen = true)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        RemoveTab(tab);
        if (ReferenceEquals(tab, _active))
        {
            _active = null;
            if (_tabs.Count > 0) SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }

        if (reopen && _tabs.Count == 0) RunSafe(() => OpenUserTabAsync(null, focusAddress: true, tab.Project));
    }

    private void RemoveTab(BrowserTab tab)
    {
        _tabs.Remove(tab);
        Tabs.TabItems.Remove(tab.Item);
        Stage.Children.Remove(tab.View);
        tab.Dispose();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is TabViewItem { Tag: BrowserTab tab } && !ReferenceEquals(tab, _active)) SelectTab(tab);
    }

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is BrowserTab tab) CloseTab(tab);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => RunSafe(() => OpenUserTabAsync(null, focusAddress: true, _active?.Project));

    private void NewPrivateTab_Click(object sender, RoutedEventArgs e) => RunSafe(OpenPrivateTabAsync);

    private void NewPrivateTab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RunSafe(OpenPrivateTabAsync);
    }

    private async Task OpenPrivateTabAsync()
    {
        try
        {
            await OpenUserTabAsync(null, focusAddress: true, _active?.Project, isPrivate: true);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException) && Root.XamlRoot is { } root)
        {
            await Prompts.MessageAsync(root, "Could not open a private tab", ex.Message);
        }
    }

    private Task<object> HandleControlAsync(JsonElement request)
    {
        var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => _ = HandleOnUiAsync(request, done)))
            done.TrySetResult(new { error = "Codale Browser is closing." });
        return done.Task;
    }

    private async Task HandleOnUiAsync(JsonElement request, TaskCompletionSource<object> done)
    {
        try
        {
            var op = request.GetProperty("op").GetString();
            var project = ReadProject(request);
            if (op == "show")
            {
                BringToFront();
                if (project is not null) await ShowProjectAsync(project);
                done.TrySetResult(new { ok = true, project = project?.Key });
                return;
            }

            var session = request.GetProperty("session").GetString() ?? throw new InvalidOperationException("Missing session.");
            var tab = _tabs.FirstOrDefault(t => t.Project?.Key == project?.Key && t.AgentKey == session);
            switch (op)
            {
                case "open":
                    var label = request.TryGetProperty("label", out var labelValue) ? labelValue.GetString() : null;
                    if (tab is null)
                    {
                        var userIsBusy = _active is { IsAgent: false } current && current.Url != "about:blank";
                        var blank = SpareTab(project);
                        tab = await CreateTabAsync(project, session, label ?? "Agent", activate: !userIsBusy);
                        if (blank is not null && blank.Url == "about:blank") CloseTab(blank);
                    }
                    else
                    {
                        await tab.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
                        if (label is not null)
                        {
                            tab.AgentLabel = label;
                            tab.RefreshHeader();
                        }
                    }

                    done.TrySetResult(tab.TargetId is null
                        ? new { error = "The tab is still starting; try again." }
                        : new { targetId = tab.TargetId, port = DebuggingPort, project = tab.Project?.Key });
                    break;

                case "focus":
                    if (tab is not null) SelectTab(tab);
                    BringToFront();
                    done.TrySetResult(new { ok = tab is not null });
                    break;

                case "cookies-info":
                case "cookies-clear":
                    if (tab?.View.CoreWebView2 is not { } core)
                        throw new InvalidOperationException("The browser tab is no longer open.");

                    var profile = core.Profile;
                    if (op == "cookies-clear")
                    {
                        await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies);
                        done.TrySetResult(new { ok = true });
                    }
                    else
                    {
                        var cookies = await profile.CookieManager.GetCookiesAsync("");
                        var domains = cookies.Select(cookie => cookie.Domain.TrimStart('.')).Distinct().Order().ToArray();
                        done.TrySetResult(new { count = cookies.Count, domains });
                    }
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

    private static BrowserProject? ReadProject(JsonElement request)
    {
        if (!request.TryGetProperty("project", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.GetString() is not { Length: 64 } key || !key.All(char.IsAsciiHexDigitLower))
            throw new InvalidOperationException("Invalid browser project key.");

        var name = request.TryGetProperty("projectName", out var label) ? label.GetString() : null;
        return new BrowserProject(key, string.IsNullOrWhiteSpace(name) ? "Project" : name);
    }

    private async Task ShowProjectAsync(BrowserProject project)
    {
        var tab = _active is { IsPrivate: false } active && active.Project?.Key == project.Key ? active
            : _tabs.FirstOrDefault(t => t.Project?.Key == project.Key && !t.IsAgent && !t.IsPrivate);
        if (tab is not null)
        {
            if (!ReferenceEquals(tab, _active)) SelectTab(tab);
            await tab.Ready.Task;
            return;
        }

        var blank = SpareTab(project);
        await OpenUserTabAsync(null, focusAddress: true, project);
        if (blank is not null && blank.Url == "about:blank") CloseTab(blank);
    }

    private BrowserTab? SpareTab(BrowserProject? project) =>
        _tabs.Count == 1 && _active is { IsAgent: false, IsPrivate: false, Url: "about:blank" } blank &&
        (blank.Project is null || blank.Project.Key == project?.Key) ? blank : null;

    /// <summary>Shows the window: restored when minimized, then brought to the front.</summary>
    private void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.View.CoreWebView2 is { CanGoBack: true } core) core.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.View.CoreWebView2 is { CanGoForward: true } core) core.GoForward();
    }

    /// <summary>Hard reload: bypasses the HTTP cache so the page and its resources are fetched fresh.</summary>
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        var core = _active?.View.CoreWebView2;
        if (core is null) return;
        RunSafe(async () => await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}"));
    }

    private void UpdateNavigationButtons()
    {
        var core = _active?.View.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
    }

    private void SyncAddress()
    {
        if (_active is null) return;
        if (Address.FocusState != FocusState.Unfocused && _addressEdited) return;
        _settingAddress = true;
        Address.Text = _active.Url == "about:blank" ? "" : _active.Url;
        _settingAddress = false;
        _addressEdited = false;
    }

    private void Address_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingAddress && Address.FocusState != FocusState.Unfocused) _addressEdited = true;
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
        // Only http and https addresses open.
        if (text.Contains("://", StringComparison.Ordinal) && OriginOf(text) is null) return;

        core.Navigate(ToUrl(text));
    }

    internal static string ToUrl(string text)
    {
        if (text == "about:blank" || text.Contains("://", StringComparison.Ordinal)) return text;
        if (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || text.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) || text.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase))
            return "http://" + text;
        if (!text.Contains(' ') && text.Contains('.')) return "https://" + text;
        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
    }

    private void RelayoutAll()
    {
        foreach (var tab in _tabs) LayoutTab(tab);
        if (_active is not null) Canvas.SetZIndex(_active.View, 1);
    }

    private void LayoutTab(BrowserTab tab)
    {
        var width = Math.Max(1, Stage.ActualWidth);
        var height = Math.Max(1, Stage.ActualHeight);
        var active = ReferenceEquals(tab, _active);
        var left = active ? 0 : -(width + 64);
        var top = 0.0;
        if (tab.Breakpoint is { } size)
        {
            // A breakpoint shows the whole device, centred: at its actual size when it fits, scaled down to fit when it doesn't.
            const double gutter = 16;
            var scale = Math.Min(1, Math.Min((width - 2 * gutter) / size.Width, (height - 2 * gutter) / size.Height));
            scale = Math.Max(0.05, Math.Round(scale, 3));
            var w = Math.Max(1, Math.Floor(size.Width * scale));
            var h = Math.Max(1, Math.Floor(size.Height * scale));
            if (active)
            {
                left = Math.Floor((width - w) / 2);
                top = Math.Floor((height - h) / 2);
            }

            width = w;
            height = h;
            if (tab.BreakpointScale != scale)
            {
                tab.BreakpointScale = scale;
                RunSafe(() => EmulateBreakpointAsync(tab, size, scale));
                if (active) UpdateBreakpointLabel();
            }
        }

        Canvas.SetLeft(tab.View, left);
        Canvas.SetTop(tab.View, top);
        tab.View.Width = width;
        tab.View.Height = height;
    }

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => RelayoutAll();

    private void OnNewWindow(BrowserTab tab, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (OriginOf(e.Uri) is null) return;
        // A private page keeps its new-window navigation within the tab's session.
        if (tab.IsAgent || tab.IsPrivate) tab.View.CoreWebView2?.Navigate(e.Uri);
        else RunSafe(() => OpenUserTabAsync(e.Uri, focusAddress: false, tab.Project));
    }

    private static string? OriginOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
