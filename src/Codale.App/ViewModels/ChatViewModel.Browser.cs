using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Core.Browser;

namespace Codale.App.ViewModels;

public sealed partial class ChatViewModel
{
    private readonly string _browserControlPipe = "codale-browser-" + Guid.NewGuid().ToString("N");
    private bool _browserViewportChanged;
    private Task _browserViewportRestore = Task.CompletedTask;

    private IReadOnlyList<McpServerSpec> WithBrowserControl(IReadOnlyList<McpServerSpec> servers) =>
        servers.Select(server => server.Name == McpServerSettings.BrowserName
            ? server with
            {
                Env = new Dictionary<string, string>(server.Env ?? new Dictionary<string, string>())
                {
                    [BrowserViewportControl.EnvironmentKey] = _browserControlPipe,
                },
            } : server).ToArray();

    private void TrackBrowserViewport(AgentEvent e)
    {
        if (e is ToolCallStarted call &&
            call.ToolName.StartsWith("mcp__codale-browser__", StringComparison.Ordinal) &&
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
            CrashLog.Trace("browser: viewport restore unavailable (browser session may have closed)");
        }
    }
}
