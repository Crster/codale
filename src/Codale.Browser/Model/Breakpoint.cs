namespace Codale.Browser;

/// <summary>A device to preview a page at; the same sizes the agent's browser tools offer.</summary>
public sealed record Breakpoint(string Name, int Width, int Height, double Scale, bool Mobile, string? UserAgent)
{
    public static readonly Breakpoint[] All =
    [
        new("Phone", 390, 844, 3, true, "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1"),
        new("Tablet", 820, 1180, 2, true, "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1"),
        new("Laptop", 1366, 768, 1, false, null),
        new("Desktop", 1920, 1080, 1, false, null),
        new("Wide screen", 2560, 1440, 1, false, null),
    ];

    public string Label => $"{Name}  {Width} × {Height}";
}
