using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Web.WebView2.Core;
using Windows.UI;

namespace Codale.Browser;

/// <summary>
/// One page in the window: a basic WebView2 and tab, plus the agent session key used by the automation pipe.
/// </summary>
public sealed class BrowserTab
{
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };

    public BrowserTab(string? agentKey, string? agentLabel, Color? agentColor)
    {
        AgentKey = agentKey;
        AgentLabel = agentLabel;
        AgentColor = agentColor;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (agentColor is { } dot)
        {
            header.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(dot), VerticalAlignment = VerticalAlignment.Center });
        }

        header.Children.Add(_title);
        Item = new TabViewItem { Header = header, IsClosable = true, Tag = this };
        if (agentColor is { } hue)
        {
            // A faint tint of the session's colour behind the tab, so an agent's pages stand apart from the user's.
            Item.Background = new SolidColorBrush(Color.FromArgb(0x22, hue.R, hue.G, hue.B));
        }

        RefreshHeader();
    }

    /// <summary>The WebView2 that shows the page. Always in the stage: a background tab is moved off-screen, not hidden, so it keeps rendering.</summary>
    public WebView2 View { get; } = new();

    public TabViewItem Item { get; }

    /// <summary>The agent session this tab belongs to; null for a tab the user opened.</summary>
    public string? AgentKey { get; }

    public string? AgentLabel { get; set; }

    public Color? AgentColor { get; }

    public bool IsAgent => AgentKey is not null;

    /// <summary>The page's DevTools target id, which the agent looks up on the debugging port.</summary>
    public string? TargetId { get; set; }

    /// <summary>The device size the page is previewed at, or null for the window's own size.</summary>
    public Breakpoint? Breakpoint { get; set; }

    /// <summary>The scale the breakpoint was last drawn at in the page (1 = actual size); 0 when it has not been sent yet.</summary>
    public double BreakpointScale { get; set; }

    /// <summary>The network requests and console output the page has produced.</summary>
    public DevToolsLog Log { get; } = new();

    /// <summary>The DevTools event subscriptions for this page; kept so they live as long as the tab.</summary>
    internal List<CoreWebView2DevToolsProtocolEventReceiver> Receivers { get; } = [];

    /// <summary>Completes when the tab's WebView2 is initialised (or fails/is closed), so a second agent request can wait for it.</summary>
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True once the tab was closed; async work that resumes afterwards must stop touching it.</summary>
    public bool IsClosed { get; private set; }

    public string Url => View.CoreWebView2?.Source ?? "about:blank";

    public string Title => View.CoreWebView2?.DocumentTitle is { Length: > 0 } title ? title
        : Url is "about:blank" or "" ? "New tab" : Url;

    /// <summary>Updates the tab's text and tooltip from the page title (or the agent's label before a page loads).</summary>
    public void RefreshHeader()
    {
        var text = IsAgent && Url is "about:blank" ? AgentLabel ?? Title : Title;
        _title.Text = text;
        ToolTipService.SetToolTip(Item, IsAgent ? $"{AgentLabel}\n{Title}" : Title);
    }

    public void Dispose()
    {
        IsClosed = true;
        Ready.TrySetException(new OperationCanceledException("The tab was closed."));
        try
        {
            View.Close();
        }
        catch (Exception)
        {
            // Already closed, or the browser process is gone.
        }
    }
}
