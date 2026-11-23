using Microsoft.Web.WebView2.WinForms;

namespace Codale.Browser;

/// <summary>A device width to preview the page at; the same sizes the agent's browser tools offer.</summary>
internal sealed record Breakpoint(string Name, string Glyph, int Width, int Height, double Scale, bool Mobile, string? UserAgent)
{
    public const string NoneGlyph = ""; // fill the window

    public static readonly Breakpoint[] All =
    [
        new("Phone", "", 390, 844, 3, true, "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1"),
        new("Tablet", "", 820, 1180, 2, true, "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1"),
        new("Laptop", "", 1366, 768, 1, false, null),
        new("Desktop", "", 1920, 1080, 1, false, null),
    ];
}

/// <summary>
/// One page in the window: its WebView, its own console/requests history, its breakpoint, and -
/// for a tab an agent asked for - the agent session that owns it and the colour that marks it.
/// </summary>
internal sealed class BrowserTab
{
    public WebView2 View { get; } = new() { Dock = DockStyle.None };

    public DiagnosticsPanel Diagnostics { get; } = new();

    /// <summary>The agent session this tab belongs to; null for a tab the user opened.</summary>
    public string? AgentKey { get; init; }

    public string? AgentLabel { get; set; }

    public Color? AgentColor { get; init; }

    public bool IsAgent => AgentKey is not null;

    public Breakpoint? Breakpoint { get; set; }

    /// <summary>The page's DevTools target id, which the agent looks up on the debugging port.</summary>
    public string? TargetId { get; set; }

    public string Url => View.CoreWebView2?.Source ?? "about:blank";

    public string Title => View.CoreWebView2?.DocumentTitle is { Length: > 0 } title ? title
        : Url is "about:blank" or "" ? "New tab" : Url;
}
