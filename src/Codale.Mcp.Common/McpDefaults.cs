namespace Codale.Mcp;

/// <summary>Values the MCP servers and the app must agree on.</summary>
public static class McpDefaults
{
    /// <summary>
    /// The remote-debugging port a browser the user opened by hand listens on. The app's
    /// "Open browser" button launches Edge with it and the browser server attaches to it.
    /// </summary>
    public const int BrowserAttachPort = 9333;
}
