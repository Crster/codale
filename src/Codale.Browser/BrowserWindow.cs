using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Codale.Browser;

/// <summary>
/// Codale Browser: tabs over one shared WebView2 profile. Each agent session gets a tab of its own,
/// marked with the session's colour (see <see cref="ControlPipe"/>); tabs the user opens are plain.
/// The toolbar - address, breakpoint, console, requests, element picker - acts on the selected tab.
/// </summary>
internal sealed class BrowserWindow : Form
{
    /// <summary>The DevTools port agents attach to (McpDefaults.BrowserAttachPort in Codale.Mcp.Common).</summary>
    internal const int DebuggingPort = 9333;

    /// <summary>Session colours, from the app's palette; each open agent tab gets one no other agent tab is using.</summary>
    private static readonly Color[] AgentPalette =
    [
        Color.FromArgb(14, 165, 233), Color.FromArgb(245, 158, 11), Color.FromArgb(16, 185, 129), Color.FromArgb(139, 92, 246),
        Color.FromArgb(236, 72, 153), Color.FromArgb(249, 115, 22), Color.FromArgb(99, 102, 241), Color.FromArgb(20, 184, 166),
    ];

    private readonly Panel _stage = new() { Dock = DockStyle.Fill };
    private readonly TabStrip _strip = new() { Dock = DockStyle.Top };
    private readonly TableLayoutPanel _toolbar = new() { Dock = DockStyle.Top, Height = 54, ColumnCount = 8, RowCount = 1, Padding = new Padding(10, 7, 10, 7), BackColor = Color.White };
    private readonly TextBox _address = new() { BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f), ForeColor = Color.FromArgb(48, 53, 61), BackColor = Color.FromArgb(240, 242, 245) };
    private readonly Panel _addressBox = new() { Dock = DockStyle.Fill, Margin = new Padding(8, 3, 8, 3) };
    private readonly Label _addressIcon = new() { Text = "", Font = new Font("Segoe MDL2 Assets", 10f), ForeColor = Color.FromArgb(122, 131, 142), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Left, Width = 34 };
    private readonly ToolTip _tooltips = new();
    private readonly ContextMenuStrip _breakpointMenu = new() { ShowImageMargin = false, ShowCheckMargin = true, Font = new Font("Segoe UI", 9.5f) };
    private readonly Button _breakpointButton;
    private readonly Button _themeButton;
    private readonly List<BrowserTab> _tabs = [];
    private readonly Dictionary<string, Color> _agentColors = [];
    private readonly TaskCompletionSource<CoreWebView2Environment> _environment = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ControlPipe _pipe;
    private BrowserTab? _active;
    private string? _diagnosticsMode;
    private bool _picking;
    private bool _dark;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    public BrowserWindow()
    {
        Text = "Codale Browser";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 1280;
        Height = 850;
        KeyPreview = true;

        _toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));

        _addressBox.Controls.Add(_address);
        _addressBox.Controls.Add(_addressIcon);
        _addressBox.Resize += (_, _) => _address.SetBounds(36, Math.Max(0, (_addressBox.ClientSize.Height - _address.PreferredHeight) / 2), Math.Max(0, _addressBox.ClientSize.Width - 46), _address.PreferredHeight);
        _address.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(); } };

        _toolbar.Controls.Add(IconButton("", "Back", (_, _) => { if (_active?.View.CanGoBack == true) _active.View.GoBack(); }), 0, 0);
        _toolbar.Controls.Add(IconButton("", "Hard refresh (reload without cache)", async (_, _) => await HardReloadAsync()), 1, 0);
        _toolbar.Controls.Add(_addressBox, 2, 0);
        _breakpointButton = IconButton(Breakpoint.NoneGlyph, "View breakpoint: fill window", (_, _) => _breakpointMenu.Show(_breakpointButton!, new Point(0, _breakpointButton!.Height + 2)));
        _toolbar.Controls.Add(_breakpointButton, 3, 0);
        _themeButton = IconButton("", "Switch to dark mode", (_, _) => ApplyTheme(!_dark));
        _toolbar.Controls.Add(_themeButton, 4, 0);
        _toolbar.Controls.Add(IconButton("", "Copy element path", async (_, _) => await PickAsync()), 5, 0);
        _toolbar.Controls.Add(IconButton("", "Console", (_, _) => ToggleDiagnostics("console")), 6, 0);
        _toolbar.Controls.Add(IconButton("", "Requests", (_, _) => ToggleDiagnostics("requests")), 7, 0);
        BuildBreakpointMenu();

        _strip.Tabs = _tabs;
        _strip.Selected += Activate;
        _strip.Closed += CloseTab;
        _strip.NewTab += () => _ = OpenUserTabAsync(null, focusAddress: true);

        // Keep every page sized (and a breakpoint centred and fitted) as the window or a panel changes size.
        _stage.Resize += (_, _) => RelayoutAll();

        Controls.Add(_stage);
        Controls.Add(_toolbar);
        Controls.Add(_strip);
        ApplyTheme(false);

        _pipe = new ControlPipe(HandleControlAsync);
        Shown += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => _pipe.Dispose();
    }

    private Button IconButton(string glyph, string label, EventHandler action)
    {
        var button = new Button { Text = glyph, AccessibleName = label, Dock = DockStyle.Fill, Margin = new Padding(2), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe MDL2 Assets", 12f), Cursor = Cursors.Hand, TabStop = true };
        button.FlatAppearance.BorderSize = 0;
        button.Click += action;
        _tooltips.SetToolTip(button, label);
        return button;
    }

    private async Task InitializeAsync()
    {
        try
        {
            var profile = Environment.GetEnvironmentVariable("CODALE_WEBVIEW_PROFILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale", "webview-browser-profile");
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--remote-debugging-port={DebuggingPort}" };
            _environment.TrySetResult(await CoreWebView2Environment.CreateAsync(null, profile, options));

            // An agent that started this window asks for its own tab right away; the user's blank tab is only for an empty window.
            await Task.Delay(300);
            if (_tabs.Count == 0) await OpenUserTabAsync(null, focusAddress: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _environment.TrySetException(ex);
            MessageBox.Show(this, ex.Message, "Could not start WebView2");
            Close();
        }
    }

    // ---- Tabs -------------------------------------------------------------------------

    private Color ColorFor(string agentKey)
    {
        if (_agentColors.TryGetValue(agentKey, out var known)) return known;
        var inUse = _tabs.Where(t => t.AgentColor is not null).Select(t => t.AgentColor!.Value).ToHashSet();
        var color = AgentPalette.FirstOrDefault(c => !inUse.Contains(c));
        if (color.IsEmpty) color = AgentPalette[_agentColors.Count % AgentPalette.Length];
        return _agentColors[agentKey] = color;
    }

    private Task<BrowserTab> OpenUserTabAsync(string? url, bool focusAddress) => CreateTabAsync(null, null, activate: true, url, focusAddress);

    private async Task<BrowserTab> CreateTabAsync(string? agentKey, string? agentLabel, bool activate, string? url = null, bool focusAddress = false)
    {
        var environment = await _environment.Task;
        var tab = new BrowserTab { AgentKey = agentKey, AgentLabel = agentLabel, AgentColor = agentKey is null ? null : ColorFor(agentKey) };
        _tabs.Add(tab);
        _stage.Controls.Add(tab.View);
        tab.Diagnostics.Visible = false;
        tab.Diagnostics.ApplyTheme(_dark);
        tab.Diagnostics.VisibleChanged += (_, _) => { if (tab == _active && !tab.Diagnostics.Visible) _diagnosticsMode = null; };
        Controls.Add(tab.Diagnostics);
        if (activate || _active is null) Activate(tab); else { LayoutTab(tab); _strip.Invalidate(); }

        await tab.View.EnsureCoreWebView2Async(environment);
        var core = tab.View.CoreWebView2;
        core.Profile.PreferredColorScheme = _dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        core.SourceChanged += (_, _) => { if (tab == _active) _address.Text = core.Source; };
        core.DocumentTitleChanged += (_, _) => { _strip.Invalidate(); if (tab == _active) UpdateTitle(); };
        core.NavigationStarting += (_, _) => { if (tab == _active) { _picking = false; UpdateTitle(); } };
        core.WebMessageReceived += async (_, e) => await ReceivePickAsync(e);
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https")) return;

            // An agent's pop-up stays in its tab, so the agent keeps driving it; the user's opens a new tab.
            if (tab.IsAgent) core.Navigate(e.Uri);
            else _ = OpenUserTabAsync(e.Uri, focusAddress: false);
        };
        tab.Diagnostics.Attach(core);
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        if (tab.Breakpoint is not null) await ApplyBreakpointAsync(tab, reload: false);
        using (var info = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}")))
        {
            tab.TargetId = info.RootElement.GetProperty("targetInfo").GetProperty("targetId").GetString();
        }

        if (url is not null) core.Navigate(url);
        if (focusAddress && tab == _active) { _address.Focus(); _address.SelectAll(); }
        return tab;
    }

    private void Activate(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        _active = tab;
        _picking = false;
        _strip.Active = tab;
        foreach (var other in _tabs) other.Diagnostics.Show(other == tab ? _diagnosticsMode : null);
        _address.Text = tab.Url == "about:blank" ? "" : tab.Url;
        ShowBreakpoint(tab.Breakpoint);
        RelayoutAll();
        UpdateTitle();
    }

    private void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        _tabs.RemoveAt(index);
        _stage.Controls.Remove(tab.View);
        Controls.Remove(tab.Diagnostics);
        tab.View.Dispose();
        tab.Diagnostics.Dispose();
        if (tab == _active)
        {
            _active = null;
            if (_tabs.Count > 0) Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }

        if (_tabs.Count == 0) _ = OpenUserTabAsync(null, focusAddress: true);
        _strip.Invalidate();
    }

    private void UpdateTitle() =>
        Text = _active is null ? "Codale Browser" : $"{(_picking ? "Select an element — " : "")}{_active.Title} — Codale Browser";

    /// <summary>
    /// Requests from agents (see <see cref="ControlPipe"/>), run on the UI thread. A new agent tab is
    /// selected only when the user is not looking at a page of their own, so an agent never pulls the
    /// window away from what the user is doing.
    /// </summary>
    private Task<object> HandleControlAsync(JsonElement request)
    {
        var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(async () =>
        {
            try
            {
                var op = request.GetProperty("op").GetString();
                var session = request.GetProperty("session").GetString() ?? throw new InvalidOperationException("Missing session.");
                var tab = _tabs.FirstOrDefault(t => t.AgentKey == session);
                switch (op)
                {
                    case "open":
                        var label = request.TryGetProperty("label", out var l) ? l.GetString() : null;
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
                        }

                        done.TrySetResult(tab.TargetId is null
                            ? new { error = "The tab is still starting; try again." }
                            : new { targetId = tab.TargetId, port = DebuggingPort });
                        break;

                    case "focus":
                        if (tab is not null) Activate(tab);
                        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                        Activate();
                        SetForegroundWindow(Handle);
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
        });
        return done.Task;
    }

    // ---- Keyboard ---------------------------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => Shortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    /// <summary>The window's shortcuts, wherever focus is: the WinForms WebView2 hands the page's unhandled accelerator keys to ProcessCmdKey.</summary>
    private bool Shortcut(Keys keys)
    {
        var key = keys & Keys.KeyCode;
        var ctrl = (keys & Keys.Control) != 0;
        var shift = (keys & Keys.Shift) != 0;
        if (ctrl && key == Keys.T) { _ = OpenUserTabAsync(null, focusAddress: true); return true; }
        if (ctrl && key == Keys.W) { if (_active is not null) CloseTab(_active); return true; }
        if (ctrl && key == Keys.L) { _address.Focus(); _address.SelectAll(); return true; }
        if (ctrl && key == Keys.Tab && _tabs.Count > 1 && _active is not null)
        {
            var index = (_tabs.IndexOf(_active) + (shift ? -1 : 1) + _tabs.Count) % _tabs.Count;
            Activate(_tabs[index]);
            return true;
        }

        if ((ctrl && key == Keys.F5) || (ctrl && shift && key == Keys.R)) { _ = HardReloadAsync(); return true; }
        return false;
    }

    // ---- Theme ------------------------------------------------------------------------

    /// <summary>
    /// Light or dark for the window and every page: the pages' prefers-color-scheme follows the
    /// toggle instead of the OS setting, so a site's two styles can be checked from here.
    /// </summary>
    private void ApplyTheme(bool dark)
    {
        _dark = dark;
        var surface = dark ? Color.FromArgb(31, 34, 40) : Color.White;
        var field = dark ? Color.FromArgb(47, 51, 59) : Color.FromArgb(240, 242, 245);
        var foreground = dark ? Color.FromArgb(225, 229, 235) : Color.FromArgb(75, 83, 94);
        _toolbar.BackColor = surface;
        _addressBox.BackColor = _addressIcon.BackColor = _address.BackColor = field;
        _addressIcon.ForeColor = dark ? Color.FromArgb(164, 173, 185) : Color.FromArgb(122, 131, 142);
        _address.ForeColor = foreground;
        _stage.BackColor = dark ? Color.FromArgb(20, 21, 24) : Color.FromArgb(233, 235, 238);
        _strip.ApplyTheme(dark);
        foreach (var button in _toolbar.Controls.OfType<Button>())
        {
            button.BackColor = surface;
            button.ForeColor = foreground;
            button.FlatAppearance.MouseOverBackColor = dark ? Color.FromArgb(57, 62, 71) : Color.FromArgb(240, 242, 245);
            button.FlatAppearance.MouseDownBackColor = dark ? Color.FromArgb(68, 74, 84) : Color.FromArgb(228, 231, 236);
        }

        foreach (var tab in _tabs)
        {
            tab.Diagnostics.ApplyTheme(dark);
            if (tab.View.CoreWebView2?.Profile is { } profile)
                profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        }

        var colors = new MenuColors(dark);
        _breakpointMenu.Renderer = new ToolStripProfessionalRenderer(colors) { RoundedEdges = false };
        _breakpointMenu.BackColor = colors.ToolStripDropDownBackground;
        _breakpointMenu.ForeColor = dark ? Color.FromArgb(225, 229, 235) : Color.FromArgb(45, 50, 58);
        _themeButton.Text = dark ? "" : "";
        _themeButton.AccessibleName = dark ? "Switch to light mode" : "Switch to dark mode";
        _tooltips.SetToolTip(_themeButton, _themeButton.AccessibleName);
    }

    /// <summary>The toolbar's menu colours, matching the window's light and dark surfaces.</summary>
    private sealed class MenuColors(bool dark) : ProfessionalColorTable
    {
        private readonly Color _surface = dark ? Color.FromArgb(31, 34, 40) : Color.White;
        private readonly Color _hover = dark ? Color.FromArgb(57, 62, 71) : Color.FromArgb(240, 242, 245);
        private readonly Color _line = dark ? Color.FromArgb(57, 62, 71) : Color.FromArgb(227, 230, 234);
        public override Color ToolStripDropDownBackground => _surface;
        public override Color ImageMarginGradientBegin => _surface;
        public override Color ImageMarginGradientMiddle => _surface;
        public override Color ImageMarginGradientEnd => _surface;
        public override Color MenuBorder => _line;
        public override Color MenuItemBorder => _hover;
        public override Color MenuItemSelected => _hover;
        public override Color SeparatorDark => _line;
        public override Color SeparatorLight => _surface;
        public override Color CheckBackground => _hover;
        public override Color CheckSelectedBackground => _hover;
        public override Color CheckPressedBackground => _hover;
    }

    // ---- Diagnostics ------------------------------------------------------------------

    private void ToggleDiagnostics(string mode)
    {
        _diagnosticsMode = _diagnosticsMode == mode ? null : mode;
        _active?.Diagnostics.Show(_diagnosticsMode);
    }

    // ---- Breakpoints and layout ---------------------------------------------------------

    private void BuildBreakpointMenu()
    {
        _breakpointMenu.Items.Add(new ToolStripMenuItem("Fill window") { Checked = true, Tag = null });
        _breakpointMenu.Items.Add(new ToolStripSeparator());
        foreach (var breakpoint in Breakpoint.All)
        {
            _breakpointMenu.Items.Add(new ToolStripMenuItem(breakpoint.Name) { Tag = breakpoint, ShortcutKeyDisplayString = $"{breakpoint.Width} × {breakpoint.Height}" });
        }

        _breakpointMenu.ItemClicked += (_, e) =>
        {
            if (e.ClickedItem is not ToolStripMenuItem item || _active is not { } tab) return;
            var choice = item.Tag as Breakpoint;
            var reload = choice?.UserAgent != tab.Breakpoint?.UserAgent;
            tab.Breakpoint = choice;
            ShowBreakpoint(choice);
            _ = ApplyBreakpointAsync(tab, reload);
        };
    }

    private void ShowBreakpoint(Breakpoint? choice)
    {
        foreach (var item in _breakpointMenu.Items.OfType<ToolStripMenuItem>()) item.Checked = item.Tag == (object?)choice;
        _breakpointButton.Text = choice?.Glyph ?? Breakpoint.NoneGlyph;
        _breakpointButton.AccessibleName = choice is null ? "View breakpoint: fill window" : $"View breakpoint: {choice.Name} {choice.Width}×{choice.Height}";
        _tooltips.SetToolTip(_breakpointButton, _breakpointButton.AccessibleName);
    }

    /// <summary>
    /// Where a tab's page sits on the stage, and how much a breakpoint is shrunk to fit. Without a
    /// breakpoint the page fills the stage; with one it is centred on the backdrop.
    /// </summary>
    private (Rectangle Bounds, double Fit) Frame(BrowserTab tab)
    {
        var area = _stage.ClientSize;
        if (tab.Breakpoint is not { } b) return (new Rectangle(Point.Empty, area), 1);
        const int margin = 16;
        var dpi = _stage.DeviceDpi / 96.0;
        var fit = Math.Clamp(Math.Min((area.Width - 2 * margin) / dpi / b.Width, (area.Height - 2 * margin) / dpi / b.Height), 0.1, 1);
        var size = new Size((int)Math.Round(b.Width * fit * dpi), (int)Math.Round(b.Height * fit * dpi));
        return (new Rectangle(Math.Max(0, (area.Width - size.Width) / 2), Math.Max(0, (area.Height - size.Height) / 2), size.Width, size.Height), fit);
    }

    /// <summary>
    /// Places a tab's page: the selected one on the stage, the rest just off it at their full size.
    /// Background pages stay "visible" to the browser, so an agent working in a tab the user is not
    /// looking at keeps full-speed rendering, timers and screenshots.
    /// </summary>
    private double LayoutTab(BrowserTab tab)
    {
        var (bounds, fit) = Frame(tab);
        if (tab != _active) bounds.Offset(-(bounds.Right + 64), 0);
        tab.View.Bounds = bounds;
        return fit;
    }

    private void RelayoutAll()
    {
        foreach (var tab in _tabs)
        {
            LayoutTab(tab);
            if (tab.Breakpoint is not null) _ = ApplyBreakpointAsync(tab, reload: false);
        }

        _active?.View.BringToFront();
    }

    /// <summary>
    /// Emulates the tab's breakpoint on its page: CSS size, pixel ratio, touch and (for phone and
    /// tablet) user agent, shrunk to fit the window when bigger than the view. A new user agent
    /// only reaches the page's scripts and server after a reload.
    /// </summary>
    private async Task ApplyBreakpointAsync(BrowserTab tab, bool reload)
    {
        var fit = LayoutTab(tab);
        if (tab.View.CoreWebView2 is not { } core) return;
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
        }
    }

    // ---- Navigation -------------------------------------------------------------------

    /// <summary>
    /// Reloads the page and everything it loads straight from the server, skipping the HTTP cache,
    /// so a rebuilt script or stylesheet shows up even when it was cached.
    /// </summary>
    private async Task HardReloadAsync()
    {
        if (_active?.View.CoreWebView2 is not { } core) return;
        try { await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}"); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { core.Reload(); }
    }

    private void Navigate()
    {
        var text = _address.Text.Trim();
        if (!text.Contains("://") && text != "about:blank") text = "https://" + text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https" || text == "about:blank")) _active?.View.CoreWebView2?.Navigate(text);
        else MessageBox.Show(this, "Enter an http or https address.", "Invalid address");
    }

    // ---- Element picker ---------------------------------------------------------------

    private async Task PickAsync()
    {
        if (_active?.View.CoreWebView2 is not { } core) return;
        _picking = true;
        UpdateTitle();
        await core.ExecuteScriptAsync(PickScript);
    }

    private Task ReceivePickAsync(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_picking) return Task.CompletedTask;
        try
        {
            using var data = JsonDocument.Parse(e.WebMessageAsJson);
            var root = data.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "codale-pick-cancel") { _picking = false; UpdateTitle(); return Task.CompletedTask; }
            if (type != "codale-pick") return Task.CompletedTask;
            _picking = false;
            var selectors = root.GetProperty("selectors").EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            var hosts = root.GetProperty("shadowHosts").EnumerateArray().Select(x => x.GetString()).ToArray();
            var selector = selectors.FirstOrDefault() ?? throw new InvalidOperationException("Could not find a unique path for this element.");
            Clipboard.SetText(string.Join(" >> ", hosts.Append(selector)), TextDataFormat.UnicodeText);
            Text = "Element path copied — Codale Browser";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ExternalException)
        {
            _picking = false;
            MessageBox.Show(this, ex.Message, "Could not capture element");
        }

        return Task.CompletedTask;
    }

    private const string PickScript = """
        (() => {
          window.__codalePickCleanup?.();
          const overlay = document.createElement('div');
          overlay.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;box-sizing:border-box;border:2px solid #3b82f6;background:rgba(59,130,246,.15);display:none;';
          document.documentElement.appendChild(overlay);
          const cleanup = () => {
            document.removeEventListener('click', handler, true);
            document.removeEventListener('pointermove', hover, true);
            document.removeEventListener('keydown', escape, true);
            overlay.remove();
            delete window.__codalePickCleanup;
          };
          const hover = e => {
            const element = e.composedPath().find(x => x instanceof Element && x !== overlay);
            if (!element) return;
            const r = element.getBoundingClientRect();
            Object.assign(overlay.style, {display:'block', left:r.left+'px', top:r.top+'px', width:r.width+'px', height:r.height+'px'});
          };
          const escape = e => {
            if (e.key !== 'Escape') return;
            e.preventDefault(); e.stopImmediatePropagation(); cleanup();
            window.chrome.webview.postMessage({type:'codale-pick-cancel'});
          };
          const selectorFor = (element, root) => {
            const unique = selector => {
              try { return root.querySelectorAll(selector).length === 1 && root.querySelector(selector) === element; }
              catch { return false; }
            };
            const candidates = [];
            if (element.id) candidates.push('#' + CSS.escape(element.id));
            for (const attr of ['data-testid', 'data-test', 'data-cy', 'name', 'aria-label']) {
              const value = element.getAttribute(attr);
              if (value && value.length <= 160) candidates.push(element.localName + '[' + attr + '=' + CSS.escape(value) + ']');
            }
            const alternatives = candidates.filter(unique);
            let node = element, parts = [];
            while (node && node.nodeType === 1) {
              let part = CSS.escape(node.localName);
              if (node.id) {
                const id = '#' + CSS.escape(node.id);
                try { if (root.querySelectorAll(id).length === 1) { parts.unshift(id); break; } } catch { }
              }
              const parent = node.parentElement || (node.parentNode instanceof ShadowRoot ? node.parentNode : null);
              const siblings = parent ? [...parent.children].filter(x => x.localName === node.localName) : [];
              if (siblings.length > 1) part += ':nth-of-type(' + (siblings.indexOf(node) + 1) + ')';
              parts.unshift(part);
              node = node.parentElement;
            }
            const fallback = parts.join(' > ');
            if (unique(fallback) && !alternatives.includes(fallback)) alternatives.push(fallback);
            return alternatives;
          };
          const handler = e => {
            e.preventDefault(); e.stopImmediatePropagation();
            const element = e.composedPath().find(x => x instanceof Element) || e.target;
            cleanup();
            const root = element.getRootNode();
            const selectors = selectorFor(element, root);
            const shadowHosts = [];
            for (let current = root; current instanceof ShadowRoot; current = current.host.getRootNode()) {
              const parent = current.host.getRootNode();
              shadowHosts.unshift(selectorFor(current.host, parent)[0] || current.host.localName);
            }
            window.chrome.webview.postMessage({type:'codale-pick', selectors, shadowHosts});
          };
          window.__codalePickCleanup = cleanup;
          document.addEventListener('click', handler, true);
          document.addEventListener('pointermove', hover, true);
          document.addEventListener('keydown', escape, true);
        })()
        """;
}
