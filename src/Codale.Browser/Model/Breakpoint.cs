namespace Codale.Browser;

/// <summary>A device size a page can be previewed at, chosen from the view breakpoint menu.</summary>
public sealed record Breakpoint(string Name, int Width, int Height, bool Mobile, string? UserAgent)
{
    private const string IPhoneAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";

    private const string IPadAgent =
        "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";

    public static readonly Breakpoint Phone = new("Phone", 390, 844, true, IPhoneAgent);
    public static readonly Breakpoint PhoneLandscape = new("Phone landscape", 844, 390, true, IPhoneAgent);
    public static readonly Breakpoint Tablet = new("Tablet", 820, 1180, true, IPadAgent);
    public static readonly Breakpoint TabletLandscape = new("Tablet landscape", 1180, 820, true, IPadAgent);
    public static readonly Breakpoint Laptop = new("Laptop", 1366, 768, false, null);
    public static readonly Breakpoint Desktop = new("Desktop", 1920, 1080, false, null);
    public static readonly Breakpoint WideScreen = new("Wide screen", 2560, 1440, false, null);

    /// <summary>The sizes in menu order; the menu also offers "Fill window" (no breakpoint).</summary>
    public static IReadOnlyList<Breakpoint> All { get; } = [Phone, PhoneLandscape, Tablet, TabletLandscape, Laptop, Desktop, WideScreen];

    /// <summary>Wider than tall: a turned phone or tablet, or any desktop screen.</summary>
    public bool Landscape => Width > Height;

    public string Label => $"{Name}  {Width} × {Height}";
}
