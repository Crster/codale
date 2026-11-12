using System.IO.Compression;
using System.Numerics;

using Codale.Core.Text;

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace Codale.App.Views;

/// <summary>
/// Draws a prompt pill as a PNG for the composer, where a RichEditBox can only hold text and
/// images. Rendering and encoding are synchronous on purpose: the editor inserts pills in the
/// middle of an edit, and an await there would let typing land between the halves of it.
/// </summary>
internal static class PromptPillBitmap
{
    private const float PadLeft = 6;
    private const float GlyphWidth = 12;
    private const float Gap = 4;
    private const float PadRight = 7;

    /// <summary>How far the pill rises above the text baseline; the rest hangs below with the descenders.</summary>
    public const int Ascent = 15;

    /// <summary>
    /// DirectWrite takes one family name, not XAML's comma list: Segoe Fluent Icons on Windows 11,
    /// Segoe MDL2 Assets (same code points) before it.
    /// </summary>
    private static readonly string IconFont =
        CanvasTextFormat.GetSystemFontFamilies().Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public readonly record struct Rendered(byte[] Png, int Width, int Height);

    private static readonly Dictionary<(PromptPillKind, string, ElementTheme, float), Rendered> Cache = [];

    public static Rendered Render(PromptPill pill, ElementTheme theme, float scale)
    {
        scale = Math.Clamp(scale, 1f, 4f);
        var key = (pill.Kind, pill.Title, theme, scale);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var device = CanvasDevice.GetSharedDevice();
        var height = (float)PromptPillVisuals.Height;

        using var titleFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = (float)PromptPillVisuals.TitleSize,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        using var measure = new CanvasTextLayout(device, pill.Title, titleFormat, (float)PromptPillVisuals.MaxTitleWidth, height);
        var titleWidth = (float)Math.Ceiling(Math.Min(measure.LayoutBounds.Width, PromptPillVisuals.MaxTitleWidth));
        var width = (float)Math.Ceiling(PadLeft + GlyphWidth + Gap + titleWidth + PadRight);

        using var target = new CanvasRenderTarget(device, width, height, 96 * scale);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);
            session.FillRoundedRectangle(0.5f, 0.5f, width - 1, height - 1, 5, 5, PromptPillVisuals.Fill(theme));
            session.DrawRoundedRectangle(0.5f, 0.5f, width - 1, height - 1, 5, 5, PromptPillVisuals.Stroke(theme), 1);

            using var glyphFormat = new CanvasTextFormat
            {
                FontFamily = IconFont,
                FontSize = (float)PromptPillVisuals.GlyphSize,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center,
            };
            session.DrawText(
                PromptPillVisuals.Glyph(pill.Kind),
                new Windows.Foundation.Rect(PadLeft, 0, GlyphWidth, height),
                PromptPillVisuals.GlyphColor(pill.Kind, theme),
                glyphFormat);

            using var title = new CanvasTextLayout(session, pill.Title, titleFormat, titleWidth + 1, height);
            session.DrawTextLayout(title, new Vector2(PadLeft + GlyphWidth + Gap, 0), PromptPillVisuals.Foreground(theme));
        }

        var pixelWidth = (int)target.SizeInPixels.Width;
        var pixelHeight = (int)target.SizeInPixels.Height;
        var rendered = new Rendered(EncodePng(target.GetPixelBytes(), pixelWidth, pixelHeight), (int)width, (int)height);
        if (Cache.Count > 256)
        {
            Cache.Clear();
        }

        Cache[key] = rendered;
        return rendered;
    }

    /// <summary>Premultiplied BGRA to a straight-alpha RGBA PNG: no filtering, one zlib stream.</summary>
    private static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1);
            raw[row] = 0;
            for (var x = 0; x < width; x++)
            {
                var source = (y * width + x) * 4;
                var target = row + 1 + x * 4;
                var a = bgra[source + 3];
                if (a == 0)
                {
                    continue;
                }

                raw[target] = (byte)Math.Min(255, bgra[source + 2] * 255 / a);
                raw[target + 1] = (byte)Math.Min(255, bgra[source + 1] * 255 / a);
                raw[target + 2] = (byte)Math.Min(255, bgra[source] * 255 / a);
                raw[target + 3] = a;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(Crc32(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
}
