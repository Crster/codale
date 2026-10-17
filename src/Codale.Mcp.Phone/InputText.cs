using System.Text;

namespace Codale.Mcp.Phone;

internal static class InputText
{
    /// <summary>
    /// Text as <c>adb shell input text</c> takes it: the device shell re-parses the argument, so spaces become
    /// %s and shell metacharacters are backslash-escaped. Newlines are not representable and must be sent as keys.
    /// </summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            switch (c)
            {
                case ' ':
                    sb.Append("%s");
                    break;
                case '\r':
                    break;
                case '\n':
                    throw new McpToolException("phone_type cannot send a newline; type the text, then call phone_key with ENTER.");
                case '%':
                    sb.Append("\\%");
                    break;
                case '\\' or '"' or '\'' or '`' or '&' or '|' or ';' or '<' or '>' or '(' or ')' or '$' or '*' or '?' or '#' or '~' or '!' or '{' or '}' or '[' or ']':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
