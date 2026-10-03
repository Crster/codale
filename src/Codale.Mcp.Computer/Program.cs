using Codale.Mcp;
using Codale.Mcp.Computer;

var desktop = new Desktop();

// Input is one physical keyboard and pointer: calls queue one at a time instead of interleaving.
var server = new McpServer("codale-computer", "1.0.0", ComputerTools.Create(desktop)) { MaxConcurrentCalls = 1 };
await server.RunStdioAsync();
