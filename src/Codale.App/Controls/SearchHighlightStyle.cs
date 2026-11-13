using Windows.UI;

namespace Codale.App.Controls;

/// <summary>Shared search colors, painted behind syntax-colored text in both views.</summary>
internal static class SearchHighlightStyle
{
    /// <summary>Behind each keyword hit.</summary>
    public static Color Match(bool light) => light
        ? Color.FromArgb(0x40, 0xE0, 0xA2, 0x00)
        : Color.FromArgb(0x40, 0xF2, 0xC1, 0x4E);

    /// <summary>A soft band across a whole matching row.</summary>
    public static Color Row(bool light) => light
        ? Color.FromArgb(0x16, 0xE0, 0xA2, 0x00)
        : Color.FromArgb(0x14, 0xF2, 0xC1, 0x4E);

    /// <summary>The thin bar on the band's left edge, so matched rows read at a glance.</summary>
    public static Color RowEdge(bool light) => light
        ? Color.FromArgb(0xFF, 0xD9, 0x9A, 0x00)
        : Color.FromArgb(0xFF, 0xE8, 0xB3, 0x39);

    public const float RowEdgeWidth = 2f;
}
