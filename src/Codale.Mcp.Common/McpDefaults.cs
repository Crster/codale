namespace Codale.Mcp;

/// <summary>Values the MCP servers and the app must agree on.</summary>
public static class McpDefaults
{
    /// <summary>
    /// The remote-debugging port Codale Browser (the app's WebView2 window) listens on; the
    /// browser server drives the window through it. Codale.Browser\Program.cs hard-codes the same value.
    /// </summary>
    public const int BrowserAttachPort = 9333;
}