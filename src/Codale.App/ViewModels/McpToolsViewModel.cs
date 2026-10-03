using System.ComponentModel;
using System.Diagnostics;

using Codale.App.Services;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Codale.App.ViewModels;

/// <summary>
/// The built-in MCP servers as the status bar flyout toggles them. A toggle lands in
/// the store at once and reaches the next spawned session.
/// </summary>
public sealed partial class McpToolsViewModel : ObservableObject
{
    private readonly McpServerSettings _settings;

    /// <summary>Set while values load from the store, so the round-trip does not rewrite them.</summary>
    private bool _loading;

    public McpToolsViewModel(McpServerSettings settings)
    {
        _settings = settings;
        Reload();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool BrowserEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool ComputerEnabled { get; set; }

    public bool BrowserAvailable => _settings.BrowserAvailable;

    public bool ComputerAvailable => _settings.ComputerAvailable;

    /// <summary>The status bar button's text: what the agents can reach.</summary>
    public string Summary => (BrowserEnabled, ComputerEnabled) switch
    {
        (true, true) => "Browser + Desktop",
        (true, false) => "Browser",
        (false, true) => "Desktop",
        _ => "",
    };

    public string BrowserNote => BrowserAvailable
        ? "Lets the agent open pages, read them, click, type and take screenshots to test its own work. Runs hidden unless you open a browser below; the agent then uses that one."
        : "Not installed with this build.";

    public string ComputerNote => ComputerAvailable
        ? "Screenshots, mouse and keyboard on your real desktop. Every action asks for your approval. This project only."
        : "Not installed with this build.";

    /// <summary>
    /// Must match <c>McpDefaults.BrowserAttachPort</c> (Codale.Mcp.Common), which the browser
    /// server attaches to; this project does not reference that one, so the value is repeated.
    /// </summary>
    private const int BrowserAttachPort = 9333;

    /// <summary>Why <see cref="OpenBrowserCommand"/> could not open a browser; null when it did (or has not run).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenBrowserError))]
    public partial string? OpenBrowserError { get; set; }

    public bool HasOpenBrowserError => OpenBrowserError is { Length: > 0 };

    /// <summary>
    /// Opens a visible Edge (or Chrome) the agent's browser tools attach to instead of
    /// their usual headless one. Its own profile keeps it separate from the user's
    /// everyday browser, and a debugging port is only honored on a non-default profile.
    /// </summary>
    [RelayCommand]
    private void OpenBrowser()
    {
        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale", "browser-profile");
        var args = $"--remote-debugging-port={BrowserAttachPort} --user-data-dir=\"{profile}\" --no-first-run about:blank";
        foreach (var exe in new[] { "msedge.exe", "chrome.exe" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true })?.Dispose();
                OpenBrowserError = null;
                return;
            }
            catch (Win32Exception)
            {
                // Not installed; try the next one.
            }
        }

        CrashLog.Warn("browser", "neither Edge nor Chrome could be started for the agent's browser");
        OpenBrowserError = "Neither Microsoft Edge nor Google Chrome was found. Install one to open a browser the agent can use.";
    }

    public void Reload()
    {
        _loading = true;
        BrowserEnabled = _settings.BrowserEnabled;
        ComputerEnabled = _settings.ComputerEnabled;
        _loading = false;
    }

    partial void OnBrowserEnabledChanged(bool value)
    {
        if (!_loading)
        {
            _settings.BrowserEnabled = value;
        }
    }

    partial void OnComputerEnabledChanged(bool value)
    {
        if (!_loading)
        {
            _settings.ComputerEnabled = value;
        }
    }
}
