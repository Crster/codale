using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;

using Codale.App.Services;
using Codale.Core.Serving;
using Codale.Core.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.UI.Dispatching;

using Windows.ApplicationModel.DataTransfer;

namespace Codale.App.ViewModels;

/// <summary>
/// The built-in MCP servers as the status bar flyout sets them (Block, CLI Default, Allow). A change lands in
/// the store at once and reaches the next spawned session.
/// </summary>
public sealed partial class McpToolsViewModel : ObservableObject, IDisposable
{
    private readonly McpServerSettings _settings;
    private readonly FolderServerHost _servers;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    /// <summary>Set while values load from the store, so the round-trip does not rewrite them.</summary>
    private bool _loading;

    public McpToolsViewModel(McpServerSettings settings, FolderServerHost servers, string projectPath)
    {
        _settings = settings;
        _servers = servers;
        ProjectPath = projectPath;
        _servers.Changed += OnServersChanged;
        Reload();
    }

    /// <summary>What "Serve project folder" serves.</summary>
    public string ProjectPath { get; }

    /// <summary>Folders served on localhost right now, by the user or an agent.</summary>
    public ObservableCollection<ServedFolderItem> ServedFolders { get; } = [];

    public bool HasServedFolders => ServedFolders.Count > 0;

    /// <summary>Why the last serve attempt failed; null when it worked (or has not run).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServeError))]
    public partial string? ServeError { get; set; }

    public bool HasServeError => ServeError is { Length: > 0 };

    /// <summary>Serves a folder on localhost and opens it in the default browser.</summary>
    public void ServeFolder(string folder)
    {
        try
        {
            var served = _servers.Serve(folder, startedBy: "you");
            ServeError = null;
            OpenUrl(served.Url);
        }
        catch (TaskServiceException e)
        {
            ServeError = e.Message;
        }
    }

    public void StopServing(string id)
    {
        try
        {
            _servers.Stop(id);
        }
        catch (TaskServiceException)
        {
            // Already stopped (by the agent, say); the list catches up on its own.
        }
    }

    public static void CopyUrl(string url)
    {
        var data = new DataPackage();
        data.SetText(url);
        Clipboard.SetContent(data);
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
        }
    }

    private void OnServersChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnServersChanged(sender, e));
            return;
        }

        ServedFolders.Clear();
        foreach (var served in _servers.List())
        {
            ServedFolders.Add(new ServedFolderItem(served));
        }

        OnPropertyChanged(nameof(HasServedFolders));
    }

    public void Dispose() => _servers.Changed -= OnServersChanged;

    /// <summary>The Browser Use dropdown's index; its items are in <see cref="AgentToolMode"/> order (Block, CLI Default, Allow).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(BrowserNote), nameof(CanOpenBrowser))]
    public partial int BrowserMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(ComputerNote))]
    public partial int ComputerMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(PhoneNote))]
    public partial int PhoneMode { get; set; }

    public bool PhoneAvailable => _settings.PhoneAvailable;

    public bool BrowserAvailable => _settings.BrowserAvailable;

    public bool ComputerAvailable => _settings.ComputerAvailable;

    /// <summary>"Open browser" opens the window Codale's browser server attaches to, so it only matters on Allow.</summary>
    public bool CanOpenBrowser => BrowserAvailable && (AgentToolMode)BrowserMode == AgentToolMode.Allow;

    /// <summary>The status bar button's text: what the agents can reach.</summary>
    public string Summary => string.Join(" + ", new[]
    {
        BrowserMode != (int)AgentToolMode.Block ? "Browser" : null,
        ComputerMode != (int)AgentToolMode.Block ? "Desktop" : null,
        PhoneMode != (int)AgentToolMode.Block ? "Phone" : null,
    }.OfType<string>());

    public string BrowserNote => Note(BrowserMode, BrowserAvailable,
        "Codale's browser: browse and check web pages",
        "The CLI's own: Claude in Chrome or Codex's browser");

    public string ComputerNote => Note(ComputerMode, ComputerAvailable,
        "Codale's desktop control: mouse and keyboard",
        "The CLI's own computer use (Codex, or Claude's if set up)");

    public string PhoneNote => Note(PhoneMode, PhoneAvailable,
        "Codale's adb tools: an Android device or emulator",
        "Neither CLI has phone tools of its own");

    private static string Note(int mode, bool available, string allow, string cliDefault) => (AgentToolMode)mode switch
    {
        AgentToolMode.Allow => available ? allow : "Codale's server is not installed with this build.",
        AgentToolMode.CliDefault => cliDefault,
        _ => "Off for agents",
    };

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

        OpenBrowserError = "Neither Microsoft Edge nor Google Chrome was found. Install one to open a browser the agent can use.";
    }

    public void Reload()
    {
        _loading = true;
        BrowserMode = (int)_settings.BrowserMode;
        ComputerMode = (int)_settings.ComputerMode;
        PhoneMode = (int)_settings.PhoneMode;
        _loading = false;
    }

    // A ComboBox reports -1 while its items are swapped; that is not a choice.
    partial void OnBrowserModeChanged(int value)
    {
        if (!_loading && value >= 0)
        {
            _settings.BrowserMode = (AgentToolMode)value;
        }
    }

    partial void OnComputerModeChanged(int value)
    {
        if (!_loading && value >= 0)
        {
            _settings.ComputerMode = (AgentToolMode)value;
        }
    }

    partial void OnPhoneModeChanged(int value)
    {
        if (!_loading && value >= 0)
        {
            _settings.PhoneMode = (AgentToolMode)value;
        }
    }
}

/// <summary>One row of the flyout's served-folders list.</summary>
public sealed class ServedFolderItem(ServedFolder served)
{
    public string Id => served.Id;

    public string Url => served.Url;

    public string Folder => served.Folder;

    public string Name => Path.GetFileName(served.Folder) is { Length: > 0 } name ? name : served.Folder;

    public string Detail => served.StartedBy == "agent" ? $"{served.Url}  · by agent" : served.Url;
}
