using Codale.Mcp;
using Codale.Mcp.Phone;

var adb = new Adb();

// One device, one input stream: calls queue one at a time instead of interleaving.
var server = new McpServer("codale-phone", "1.0.0", PhoneTools.Create(adb))
{
    MaxConcurrentCalls = 1,
    Instructions = McpInstructions.Enabled ? McpInstructions.Phone : null,
};
await server.RunStdioAsync();
