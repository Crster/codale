using Codale.Mcp;
using Codale.Mcp.Browser;

await using var browser = new BrowserSession();
var server = new McpServer("codale-browser", "1.0.0", BrowserTools.Create(browser));
await server.RunStdioAsync();
