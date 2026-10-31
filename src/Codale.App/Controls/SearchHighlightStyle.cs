using Windows.UI;

namespace Codale.App.Controls;

/// <summary>Shared search colors, painted behind syntax-colored text in both views.</summary>
internal static class SearchHighlightStyle
{
    public static Color Match(bool light) => light
        ? Color.FromArgb(0x38, 0xCE, 0x91, 0x00)
        : Color.FromArgb(0x38, 0xF2, 0xC1, 0x4E);

    public static Color Row(bool light) => light
        ? Color.FromArgb(0x80, 0x9C, 0x6A, 0x00)
        : Color.FromArgb(0x80, 0xF2, 0xC1, 0x4E);
}
