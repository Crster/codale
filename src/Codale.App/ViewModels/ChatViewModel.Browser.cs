using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Core.Browser;

namespace Codale.App.ViewModels;

public sealed partial class ChatViewModel
{
    private readonly string _browserControlPipe = "codale-browser-" + Guid.NewGuid().ToString("N");
    private bool _browserViewportChanged;
    private Task _browserViewportRestore = Task.CompletedTask;

    /// <summary>
    /// Wires this chat into the browser server: the pipe that releases emulation at turn end, and the
    /// session key and label that give the chat its own colour-marked tab in Codale Browser. The key
    /// is this chat's pipe name - unique and stable for the chat's life - so a CLI restart reuses the tab.
    /// </summary>
    private IReadOnlyList<McpServerSpec> WithBrowserControl(IReadOnlyList<McpServerSpec> servers) =>
        [.. servers.Select(server => server.Name == McpServerSettings.BrowserName
            ? server with
            {
                Env = new Dictionary<string, string>(server.Env ?? new Dictionary<string, string>())
                {
                    [BrowserViewportControl.EnvironmentKey] = _browserControlPipe,
                    ["CODALE_BROWSER_SESSION"] = _browserControlPipe,
                    ["CODALE_BROWSER_LABEL"] = Title is { Length: > 0 } title ? $"{ProviderName} · {title}" : ProviderName,
                },
            } : server)];

    private void TrackBrowserViewport(AgentEvent e)
    {
        if (e is ToolCallStarted call &&
            call.ToolName.StartsWith(McpServerSpec.PrefixOf(McpServerSettings.BrowserName), StringComparison.Ordinal) &&
            (call.ToolName.EndsWith("__browser_set_viewport", StringComparison.Ordinal) ||
             call.ToolName.EndsWith("__browser_resize", StringComparison.Ordinal)))
        {
            _browserViewportChanged = true;
        }
    }

    private void RestoreBrowserViewport()
    {
        if (!_browserViewportChanged) return;
        _browserViewportChanged = false;
        _browserViewportRestore = RestoreBrowserViewportCoreAsync();
    }

    private async Task RestoreBrowserViewportCoreAsync()
    {
        if (!await BrowserViewportControl.RestoreAsync(_browserControlPipe))
        {
        }
    }
}