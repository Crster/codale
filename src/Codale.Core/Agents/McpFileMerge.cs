using System.Text.Json.Nodes;

namespace Codale.Core.Agents;

/// <summary>The MCP config file formats the CLIs read.</summary>
public enum McpFileFormat
{
    /// <summary><c>mcpServers</c> with a <c>type</c> field (Claude's <c>.mcp.json</c>).</summary>
    Standard,

    /// <summary><c>mcpServers</c> without <c>type</c>, plus <c>trust</c> (Gemini CLI and Qwen Code).</summary>
    Gemini,

    /// <summary>OpenCode's <c>mcp</c> map: <c>type: local</c>, a <c>command</c> array and an <c>environment</c> map.</summary>
    OpenCode,
}

/// <summary>
/// Writes Codale's servers into the MCP map of a CLI's JSON config, keeping every other key and every server Codale does
/// not own. Stale <c>codale-*</c> entries and the named Codale servers are removed first, so a server that was switched
/// off leaves the file. Pure: the caller reads and writes the file.
/// </summary>
public static class McpFileMerge
{
    /// <summary>The key of the server map in the document.</summary>
    public static string MapKey(McpFileFormat format) => format == McpFileFormat.OpenCode ? "mcp" : "mcpServers";

    /// <summary>Applies <paramref name="servers"/> to <paramref name="doc"/> in place.</summary>
    /// <param name="ownedNames">The server names Codale owns beyond the <c>codale-*</c> prefix.</param>
    public static void Apply(JsonObject doc, IReadOnlyList<McpServerSpec> servers, McpFileFormat format, IReadOnlySet<string> ownedNames)
    {
        var mapKey = MapKey(format);
        var gemini = format == McpFileFormat.Gemini;

        var map = doc[mapKey] as JsonObject ?? [];
        foreach (var key in map.Select(p => p.Key).Where(k => k.StartsWith("codale-", StringComparison.Ordinal) || ownedNames.Contains(k)).ToList())
        {
            map.Remove(key);
        }

        if (servers.Count > 0 && JsonNode.Parse(McpServerSpec.ToClaudeConfig(servers)!)?["mcpServers"] is JsonObject ours)
        {
            foreach (var (name, entry) in ours.ToList())
            {
                ours.Remove(name);
                if (format == McpFileFormat.OpenCode)
                {
                    var spec = servers.First(s => s.Name == name);
                    var local = new JsonObject
                    {
                        ["type"] = "local",
                        ["command"] = new JsonArray(
                            new[] { spec.Command }.Concat(spec.Args).Select(a => (JsonNode)a).ToArray()),
                        ["enabled"] = true,
                    };
                    if (spec.Env is { Count: > 0 } env)
                    {
                        var vars = new JsonObject();
                        foreach (var (key, value) in env)
                        {
                            vars[key] = value;
                        }

                        local["environment"] = vars;
                    }

                    map[name] = local;
                    continue;
                }

                if (gemini && entry is JsonObject e)
                {
                    // Gemini has no "type" field; "trust" skips its per-call confirmation.
                    e.Remove("type");
                    var spec = servers.First(s => s.Name == name);
                    if (spec.AutoApprove && !spec.RequireApproval)
                    {
                        e["trust"] = true;
                    }
                }

                map[name] = entry;
            }
        }

        if (map.Count == 0)
        {
            doc.Remove(mapKey);
        }
        else
        {
            doc[mapKey] = map;
        }
    }
}