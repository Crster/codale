using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Codale.Mcp.Phone;

internal static partial class UiTree
{
    [GeneratedRegex(@"\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]")]
    private static partial Regex BoundsPattern();

    /// <summary>
    /// A <c>uiautomator dump</c> as one line per element that has text, a description, an id or can be
    /// clicked: <c>[n] Class "text" desc="..." id=... center=(x,y) clickable</c>. Centers are device pixels,
    /// ready for phone_tap.
    /// </summary>
    public static string Parse(string xml)
    {
        var start = xml.IndexOf("<?xml", StringComparison.Ordinal);
        if (start < 0)
        {
            start = xml.IndexOf("<hierarchy", StringComparison.Ordinal);
        }

        if (start < 0)
        {
            throw new McpToolException("The device returned no UI hierarchy: " + xml.Trim());
        }

        var doc = XDocument.Parse(xml[start..]);
        var sb = new StringBuilder();
        var index = 0;
        foreach (var node in doc.Descendants("node"))
        {
            var text = (string?)node.Attribute("text") ?? "";
            var desc = (string?)node.Attribute("content-desc") ?? "";
            var id = (string?)node.Attribute("resource-id") ?? "";
            var clickable = (string?)node.Attribute("clickable") == "true";
            if (text.Length == 0 && desc.Length == 0 && id.Length == 0 && !clickable)
            {
                continue;
            }

            var match = BoundsPattern().Match((string?)node.Attribute("bounds") ?? "");
            if (!match.Success)
            {
                continue;
            }

            var cx = (int.Parse(match.Groups[1].Value) + int.Parse(match.Groups[3].Value)) / 2;
            var cy = (int.Parse(match.Groups[2].Value) + int.Parse(match.Groups[4].Value)) / 2;
            var cls = ((string?)node.Attribute("class") ?? "").Split('.').Last();
            sb.Append('[').Append(index++).Append("] ").Append(cls);
            if (text.Length > 0) sb.Append(" \"").Append(text).Append('"');
            if (desc.Length > 0) sb.Append(" desc=\"").Append(desc).Append('"');
            if (id.Length > 0) sb.Append(" id=").Append(id);
            sb.Append(" center=(").Append(cx).Append(',').Append(cy).Append(')');
            if (clickable) sb.Append(" clickable");
            sb.Append('\n');
        }

        return sb.Length == 0 ? "(no visible elements)" : sb.ToString().TrimEnd();
    }
}
