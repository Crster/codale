using System.Text.Json.Nodes;

using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class McpFileMergeTests
{
    private static readonly IReadOnlySet<string> Owned = new HashSet<string>(StringComparer.Ordinal) { "browser-use" };

    private static JsonObject Doc(string json) => JsonNode.Parse(json)!.AsObject();

    private static McpServerSpec Server(string name, string command = "node", params string[] args) =>
        new() { Name = name, Command = command, Args = args };

    [Fact]
    public void TheMapKeyDependsOnTheFormat()
    {
        Assert.Equal("mcpServers", McpFileMerge.MapKey(McpFileFormat.Standard));
        Assert.Equal("mcpServers", McpFileMerge.MapKey(McpFileFormat.Gemini));
        Assert.Equal("mcp", McpFileMerge.MapKey(McpFileFormat.OpenCode));
    }

    [Fact]
    public void ServersAreAddedBesideTheUsersOwnKeysAndServers()
    {
        var doc = Doc("{\"keep\":1,\"mcpServers\":{\"mine\":{\"command\":\"tool\"}}}");

        McpFileMerge.Apply(doc, [Server("codale-a", "node", "a.js")], McpFileFormat.Standard, Owned);

        Assert.Equal(1, doc["keep"]!.GetValue<int>());
        var map = doc["mcpServers"]!.AsObject();
        Assert.Equal("tool", map["mine"]!["command"]!.GetValue<string>());
        Assert.Equal("node", map["codale-a"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void StaleCodaleServersAndOwnedNamesAreRemoved()
    {
        var doc = Doc("{\"mcpServers\":{\"codale-old\":{\"command\":\"x\"},\"browser-use\":{\"command\":\"y\"},\"mine\":{\"command\":\"z\"}}}");

        McpFileMerge.Apply(doc, [Server("codale-new")], McpFileFormat.Standard, Owned);

        var map = doc["mcpServers"]!.AsObject();
        Assert.False(map.ContainsKey("codale-old"));
        Assert.False(map.ContainsKey("browser-use"));
        Assert.True(map.ContainsKey("mine"));
        Assert.True(map.ContainsKey("codale-new"));
    }

    [Fact]
    public void TurningEveryServerOffRemovesAnEmptyMap()
    {
        var doc = Doc("{\"mcpServers\":{\"codale-a\":{\"command\":\"x\"}},\"keep\":true}");

        McpFileMerge.Apply(doc, [], McpFileFormat.Standard, Owned);

        Assert.False(doc.ContainsKey("mcpServers"));
        Assert.True(doc["keep"]!.GetValue<bool>());
    }

    [Fact]
    public void OpenCodeEntriesAreLocalWithACommandArrayAndEnvironment()
    {
        var doc = Doc("{}");
        var spec = new McpServerSpec
        {
            Name = "codale-a",
            Command = "node",
            Args = ["a.js", "--x"],
            Env = new Dictionary<string, string> { ["TOKEN"] = "v" },
        };

        McpFileMerge.Apply(doc, [spec], McpFileFormat.OpenCode, Owned);

        var entry = doc["mcp"]!["codale-a"]!.AsObject();
        Assert.Equal("local", entry["type"]!.GetValue<string>());
        Assert.True(entry["enabled"]!.GetValue<bool>());
        Assert.Equal(["node", "a.js", "--x"], entry["command"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("v", entry["environment"]!["TOKEN"]!.GetValue<string>());
    }

    [Fact]
    public void OpenCodeOmitsEnvironmentWhenThereIsNone()
    {
        var doc = Doc("{}");

        McpFileMerge.Apply(doc, [Server("codale-a")], McpFileFormat.OpenCode, Owned);

        Assert.Null(doc["mcp"]!["codale-a"]!["environment"]);
    }

    [Fact]
    public void GeminiEntriesHaveNoTypeAndAutoApprovedOnesAreTrusted()
    {
        var doc = Doc("{}");
        var trusted = new McpServerSpec { Name = "codale-trusted", Command = "node", AutoApprove = true };
        var asks = new McpServerSpec { Name = "codale-asks", Command = "node", AutoApprove = true, RequireApproval = true };

        McpFileMerge.Apply(doc, [trusted, asks], McpFileFormat.Gemini, Owned);

        var map = doc["mcpServers"]!.AsObject();
        Assert.Null(map["codale-trusted"]!["type"]);
        Assert.True(map["codale-trusted"]!["trust"]!.GetValue<bool>());
        Assert.Null(map["codale-asks"]!["trust"]);
    }
}