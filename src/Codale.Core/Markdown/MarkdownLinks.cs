using Codale.Core.Text;

namespace Codale.Core.Markdown;

/// <summary>
/// The parts of a rendered Markdown document that carry meaning beyond their look: where a link points (a file, a
/// line of one, or a web address), the plain text a run of inlines shows, and the bytes of an inline data image.
/// Pure: the view decides how each of them is drawn.
/// </summary>
public static class MarkdownLinks
{
    /// <summary>The largest inline data image accepted, in base64 characters.</summary>
    public const int MaxDataImageBase64 = 12 * 1024 * 1024;

    /// <summary>The file a markdown link points at: a relative or absolute path, a file: URI, or either with #L42.</summary>
    public static LinkMatch? FileTarget(string url)
    {
        var line = 0;
        var col = 0;
        var endLine = 0;
        var endCol = 0;

        var hash = url.LastIndexOf('#');
        if (hash >= 0)
        {
            var fragment = url[(hash + 1)..];
            url = url[..hash];
            (line, col, endLine, endCol) = LinkFinder.ParseHashLocation(fragment);
        }

        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var file))
            {
                url = file.LocalPath;
            }
            else
            {
                url = url[5..].TrimStart('/');
                if (url.Length >= 2 && url[1] == ':')
                {
                    // e.g. "X:/..."
                }
                else
                {
                    url = "/" + url;
                }
            }
        }

        var decoded = Uri.UnescapeDataString(url).Trim();
        var reference = LinkFinder.AsFileReference(decoded);
        if (reference is null && (decoded.Contains('/') || decoded.Contains('\\') || (decoded.Length >= 2 && decoded[1] == ':')))
        {
            var matches = LinkFinder.Find(decoded);
            reference = matches.Count == 1 && matches[0].Index == 0 && matches[0].Length == decoded.Length
                ? matches[0]
                : new LinkMatch(0, decoded.Length, decoded, null, decoded);
        }

        return reference is { Path: not null }
            ? (reference with
            {
                Line = reference.Line > 0 ? reference.Line : line,
                Column = reference.Column > 0 ? reference.Column : col,
                EndLine = reference.EndLine > 0 ? reference.EndLine : endLine,
                EndColumn = reference.EndColumn > 0 ? reference.EndColumn : endCol,
            })
            : null;
    }

    public static string InlineText(IReadOnlyList<MarkdownInline> content) => string.Concat(content.Select(TextOf));

    private static string TextOf(MarkdownInline inline) => inline switch
    {
        MarkdownInline.Text text => text.Value,
        MarkdownInline.CodeSpan code => code.Value,
        MarkdownInline.Emphasis emphasis => InlineText(emphasis.Content),
        MarkdownInline.Strike struck => InlineText(struck.Content),
        MarkdownInline.Link link => InlineText(link.Content),
        MarkdownInline.Image image => InlineText(image.Content),
        _ => "",
    };

    public static byte[]? DecodeDataImage(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0
            || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            || url.Length - comma - 1 > MaxDataImageBase64)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(url[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}