using Codale.Mcp;
using Codale.Mcp.Browser;
using Codale.Core.Browser;

await using var browser = new BrowserSession();
await using var viewportControl = Environment.GetEnvironmentVariable(BrowserViewportControl.EnvironmentKey) is { Length: > 0 } pipe
    ? new BrowserViewportControl(pipe, browser.RestoreViewportAsync) : null;
var server = new McpServer("codale-browser", "1.0.0", BrowserTools.Create(browser))
{
    Instructions = McpInstructions.Enabled ? McpInstructions.Browser : null,
};
await server.RunStdioAsync();
