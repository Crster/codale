using System.Text.Json;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

using Windows.ApplicationModel.DataTransfer;

namespace Codale.Browser;

/// <summary>
/// The page tools on the address row: the element picker (copies a CSS selector), the view breakpoint menu, and the
/// DevTools protocol events that feed the Network and Console panel (the panel itself is in MainWindow.Panel.cs).
/// </summary>
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _selectorTipTimer;

    private void InitializeTools()
    {
        BreakpointButton.Flyout = BuildBreakpointMenu();
        InitializePanel();
    }

    /// <summary>Brings the toolbar and panel in line with the tab that just came to the front.</summary>
    private void OnActiveTabChanged()
    {
        UpdateBreakpointLabel();
        SetPanel(_panel);
        RefreshPanelSummary();
    }

    // ---- Element picker -------------------------------------------------------------

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (PickToggle.IsChecked != true)
        {
            StopPick(_active);
            return;
        }

        if (_active?.View.CoreWebView2 is not { } core)
        {
            PickToggle.IsChecked = false;
            return;
        }

        SelectorTip.IsOpen = false;
        RunSafe(() => InjectPickAsync(core));
        // The page takes the keyboard so Escape reaches the picker.
        _active.View.Focus(FocusState.Programmatic);
    }

    private static async Task InjectPickAsync(CoreWebView2 core) => await core.ExecuteScriptAsync(PagePicker.Start);

    /// <summary>Turns the picker off (in the page too) and unchecks the toolbar button.</summary>
    private void StopPick(BrowserTab? tab)
    {
        PickToggle.IsChecked = false;
        if (tab is not { IsClosed: false } || tab.View.CoreWebView2 is not { } core) return;
        RunSafe(async () => await core.ExecuteScriptAsync(PagePicker.Stop));
    }

    private void OnPageMessage(BrowserTab tab, string json)
    {
        if (!ReferenceEquals(tab, _active)) return;

        string? type;
        string? selector;
        try
        {
            using var document = JsonDocument.Parse(json);
            type = ConsoleFormat.Str(document.RootElement, "type");
            selector = ConsoleFormat.Str(document.RootElement, "selector");
        }
        catch (JsonException)
        {
            return;
        }

        if (type == "codale-pick-cancel")
        {
            PickToggle.IsChecked = false;
            return;
        }

        if (type != "codale-pick" || string.IsNullOrWhiteSpace(selector)) return;

        PickToggle.IsChecked = false;
        var data = new DataPackage();
        data.SetText(selector);
        Clipboard.SetContent(data);
        Clipboard.Flush();

        SelectorTip.Subtitle = selector;
        SelectorTip.IsOpen = true;

        // The tip closes itself after a few seconds; a click anywhere closes it sooner.
        _selectorTipTimer ??= CreateSelectorTipTimer();
        _selectorTipTimer.Stop();
        _selectorTipTimer.Start();
    }

    private DispatcherQueueTimer CreateSelectorTipTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => SelectorTip.IsOpen = false;
        return timer;
    }

    // ---- View breakpoints -----------------------------------------------------------

    private MenuFlyout BuildBreakpointMenu()
    {
        var flyout = new MenuFlyout();
        var sizes = new List<(ToggleMenuFlyoutItem Item, Breakpoint? Size)>();
        foreach (var size in new Breakpoint?[] { null }.Concat(Breakpoint.All))
        {
            var item = new ToggleMenuFlyoutItem { Text = size?.Label ?? "Fill window" };
            var chosen = size;
            item.Click += (_, _) => RunSafe(() => ApplyBreakpointAsync(_active, chosen));
            sizes.Add((item, size));
            flyout.Items.Add(item);
            // Separators set off "Fill window" and the touch devices from the desktop screens.
            if (size is null || size == Breakpoint.TabletLandscape) flyout.Items.Add(new MenuFlyoutSeparator());
        }

        flyout.Opening += (_, _) =>
        {
            foreach (var (item, size) in sizes) item.IsChecked = size == _active?.Breakpoint;
        };
        return flyout;
    }

    /// <summary>Emulates a device size on the tab's page (or restores the window's own size when <paramref name="size"/> is null).</summary>
    private async Task ApplyBreakpointAsync(BrowserTab? tab, Breakpoint? size)
    {
        if (tab is not { IsClosed: false } || tab.View.CoreWebView2 is not { } core) return;

        tab.Breakpoint = size;
        tab.BreakpointScale = 0;
        if (size is null)
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride",
                JsonSerializer.Serialize(new { userAgent = core.Settings.UserAgent }));
        }
        else
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride",
                JsonSerializer.Serialize(new { userAgent = size.UserAgent ?? core.Settings.UserAgent }));
        }

        // Laying the tab out sends the device metrics at the scale that fits the stage.
        RelayoutAll();
        UpdateBreakpointLabel();
    }

    /// <summary>
    /// Lays the page out at the device's full size and draws it at <paramref name="scale"/>, so a device larger than the
    /// window is shown whole (as in DevTools' device mode) instead of spilling out of the stage.
    /// </summary>
    private async Task EmulateBreakpointAsync(BrowserTab tab, Breakpoint size, double scale)
    {
        if (tab.IsClosed || tab.View.CoreWebView2 is not { } core) return;
        await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", JsonSerializer.Serialize(new
        {
            width = size.Width,
            height = size.Height,
            // 0 keeps the screen's own pixel density, so a scaled-down page stays sharp.
            deviceScaleFactor = 0,
            mobile = size.Mobile,
            scale,
            screenWidth = size.Width,
            screenHeight = size.Height,
            // screen.orientation and orientationchange match the device: a turned phone or tablet reports 90°.
            screenOrientation = new
            {
                type = size.Landscape ? "landscapePrimary" : "portraitPrimary",
                angle = size.Mobile && size.Landscape ? 90 : 0,
            },
            // The view keeps the size the stage gives it; without this the page's view is resized to the device and overflows.
            dontSetVisibleSize = true,
        }));
    }

    /// <summary>Names the emulated device beside the breakpoint icon, so it is clear the page is not at the window's size.</summary>
    private void UpdateBreakpointLabel()
    {
        var size = _active?.Breakpoint;
        var scale = _active?.BreakpointScale ?? 1;
        var zoom = scale is > 0 and < 0.995 ? $" · {Math.Round(scale * 100)}%" : "";
        BreakpointLabel.Text = size is null ? "" : size.Name + zoom;
        BreakpointLabel.Visibility = size is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(BreakpointButton, size is null ? "View breakpoint" : $"View breakpoint: {size.Label}{zoom}");
    }

    // ---- DevTools protocol ----------------------------------------------------------

    /// <summary>Hooks a tab's page into the picker, the panel, and the DevTools events. Called once per tab.</summary>
    private void WireTools(BrowserTab tab, CoreWebView2 core)
    {
        core.WebMessageReceived += (_, e) => OnPageMessage(tab, e.WebMessageAsJson);
        core.NavigationStarting += (_, e) =>
        {
            // A new page starts with an empty log, as in DevTools, unless "Preserve log" is on. Redirects keep the hop that led here.
            if (!_preserveLog && !e.IsRedirected) tab.Log.Clear();
        };
        core.NavigationCompleted += (_, _) =>
        {
            // A navigation replaces the page's script, so an active picker is started again in the new page.
            if (ReferenceEquals(tab, _active) && PickToggle.IsChecked == true) RunSafe(() => InjectPickAsync(core));
        };

        tab.Log.Added += item =>
        {
            if (ReferenceEquals(tab, _active)) OnLogAdded(item);
        };
        tab.Log.Removed += item =>
        {
            if (ReferenceEquals(tab, _active)) OnLogRemoved(item);
        };
        tab.Log.Cleared += () =>
        {
            if (ReferenceEquals(tab, _active)) RebuildRows();
        };
        tab.Log.Changed += () =>
        {
            if (ReferenceEquals(tab, _active)) QueuePanelRefresh();
        };

        var log = tab.Log;
        Listen(tab, core, "Network.requestWillBeSent", log.OnRequestWillBeSent);
        Listen(tab, core, "Network.responseReceived", log.OnResponseReceived);
        Listen(tab, core, "Network.loadingFinished", log.OnLoadingFinished);
        Listen(tab, core, "Network.loadingFailed", log.OnLoadingFailed);
        Listen(tab, core, "Network.requestServedFromCache", log.OnServedFromCache);
        Listen(tab, core, "Network.webSocketCreated", log.OnWebSocketCreated);
        Listen(tab, core, "Network.webSocketHandshakeResponseReceived", log.OnWebSocketHandshake);
        Listen(tab, core, "Network.webSocketClosed", log.OnWebSocketClosed);
        Listen(tab, core, "Runtime.consoleAPICalled", log.OnConsoleApiCalled);
        Listen(tab, core, "Runtime.exceptionThrown", log.OnExceptionThrown);
        Listen(tab, core, "Log.entryAdded", log.OnLogEntry);

        RunSafe(async () =>
        {
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        });
    }

    private static void Listen(BrowserTab tab, CoreWebView2 core, string name, Action<JsonElement> handle)
    {
        var receiver = core.GetDevToolsProtocolEventReceiver(name);
        receiver.DevToolsProtocolEventReceived += (_, e) =>
        {
            if (tab.IsClosed) return;
            try
            {
                using var document = JsonDocument.Parse(e.ParameterObjectAsJson);
                handle(document.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
            {
                // An event in a shape this browser does not read is dropped.
            }
        };

        // The receiver is kept alive with the tab; WebView2 only holds the subscription through it.
        tab.Receivers.Add(receiver);
    }
}
