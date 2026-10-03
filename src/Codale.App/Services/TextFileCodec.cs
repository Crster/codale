using System.Globalization;
using System.Text;

namespace Codale.App.Services;

/// <summary>How a text file was stored, so saving it writes back the same bytes layout.</summary>
/// <param name="Encoding">Without a byte-order mark; <see cref="HasBom"/> says whether to write one.</param>
/// <param name="HasBom">True when the file started with a byte-order mark.</param>
/// <param name="LineEnding">The dominant line ending: <c>"\r\n"</c>, <c>"\n"</c> or <c>"\r"</c>.</param>
public sealed record TextFileFormat(Encoding Encoding, bool HasBom, string LineEnding)
{
    /// <summary>What a new file gets: UTF-8 without a BOM and the platform's line ending.</summary>
    public static TextFileFormat Default { get; } = new(new UTF8Encoding(false), false, Environment.NewLine);
}

/// <summary>
/// Reads and writes text files without silently changing them: the encoding (UTF-8 with
/// or without BOM, UTF-16, or the system code page for legacy files) and the dominant line
/// ending are detected on load and re-applied on save. Pure logic, no UI dependencies.
/// </summary>
public static class TextFileCodec
{
    /// <summary>Decodes a file's bytes; the returned text keeps its original line endings.</summary>
    public static (string Text, TextFileFormat Format) Decode(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding;
        var bomLength = 0;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(false);
            bomLength = 3;
        }
        else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: false);
            bomLength = 4;
        }
        else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: false);
            bomLength = 4;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            bomLength = 2;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
            bomLength = 2;
        }
        else
        {
            encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
        }

        string text;
        try
        {
            text = encoding.GetString(bytes[bomLength..]);
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8: a legacy file in the system's ANSI code page.
            encoding = LegacyEncoding();
            text = encoding.GetString(bytes);
        }

        return (text, new TextFileFormat(encoding, bomLength > 0, DominantLineEnding(text)));
    }

    /// <summary>
    /// The bytes to write for <paramref name="text"/> (any mix of line endings, as an editor
    /// reports them): line endings are rewritten to the format's own, and the encoding and BOM
    /// are restored. Text the legacy code page cannot represent is written as UTF-8 instead
    /// of being turned into question marks; <paramref name="format"/> is updated to match.
    /// </summary>
    public static byte[] Encode(string text, ref TextFileFormat format)
    {
        var normalised = NormalizeLineEndings(text, format.LineEnding);
        var encoding = format.Encoding;

        byte[] body;
        try
        {
            body = Strict(encoding).GetBytes(normalised);
        }
        catch (EncoderFallbackException)
        {
            format = format with { Encoding = new UTF8Encoding(false), HasBom = format.HasBom };
            encoding = format.Encoding;
            body = encoding.GetBytes(normalised);
        }

        if (!format.HasBom)
        {
            return body;
        }

        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0)
        {
            preamble = BomFor(encoding);
        }

        return [.. preamble, .. body];
    }

    /// <summary>The line ending most lines use; the platform's when the text has none.</summary>
    public static string DominantLineEnding(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        if (crlf == 0 && lf == 0 && cr == 0)
        {
            return Environment.NewLine;
        }

        return crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
    }

    /// <summary>Rewrites every line break (CRLF, LF or lone CR) to <paramref name="lineEnding"/>.</summary>
    public static string NormalizeLineEndings(string text, string lineEnding)
    {
        if (text.IndexOfAny(['\r', '\n']) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append(lineEnding);
            }
            else if (c == '\n')
            {
                builder.Append(lineEnding);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>A copy of the encoding that throws on characters it cannot represent, instead of substituting.</summary>
    private static Encoding Strict(Encoding encoding) =>
        Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static byte[] BomFor(Encoding encoding) => encoding.CodePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        12000 => [0xFF, 0xFE, 0x00, 0x00],
        12001 => [0x00, 0x00, 0xFE, 0xFF],
        _ => [],
    };

    private static Encoding LegacyEncoding()
    {
        try
        {
            // The Windows code pages (1252 and friends) are not built in on .NET Core.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }
}
