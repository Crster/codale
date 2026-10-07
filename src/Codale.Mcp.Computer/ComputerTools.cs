using System.Text;

namespace Codale.Mcp.Computer;

internal static class ComputerTools
{
    private static readonly (string, string, string) X = ("x", "number", "Horizontal position in pixels, measured on the most recent computer_screenshot.");
    private static readonly (string, string, string) Y = ("y", "number", "Vertical position in pixels, measured on the most recent computer_screenshot.");

    private static Task<IReadOnlyList<McpContent>> Done(string text) => McpTool.Text(text);

    public static IReadOnlyList<McpTool> Create(Desktop d) =>
    [
        new("computer_screenshot",
            "Capture the user's real Windows desktop, or one window by title, as a PNG. This is how you see native apps, dialogs and anything outside the browser. " +
            "ALWAYS take one before any computer_click / computer_move / computer_drag / computer_scroll, and again after the screen changes: those tools' coordinates refer to the most recent screenshot.",
            McpTool.Schema([("window", "string", "Optional: part of a window title (see computer_list_windows) to capture only that window.")]),
            (a, _) =>
            {
                var (png, description) = d.Screenshot(a.String("window"));
                return Task.FromResult<IReadOnlyList<McpContent>>([McpContent.FromText(description), McpContent.FromImage(png)]);
            }),

        new("computer_click", "Click the real mouse at a position on the desktop, taken from the most recent computer_screenshot. For elements inside the automation browser use browser_click instead.",
            McpTool.Schema([X, Y, ("button", "string", "Mouse button: left (default), right or middle."), ("double", "boolean", "Double-click instead of a single click.")], "x", "y"),
            (a, _) =>
            {
                d.Click(a.RequiredInt("x"), a.RequiredInt("y"), a.String("button") ?? "left", a.Bool("double") == true ? 2 : 1);
                return Done("Clicked.");
            }),

        new("computer_move", "Move the real mouse pointer to a desktop position without clicking, to open hover menus or tooltips before the next screenshot.",
            McpTool.Schema([X, Y], "x", "y"),
            (a, _) =>
            {
                d.MoveTo(a.RequiredInt("x"), a.RequiredInt("y"));
                return Done("Moved.");
            }),

        new("computer_drag", "Press the left mouse button at a start position, move to an end position and release: for moving windows, resizing panes, dragging sliders or files. Positions come from the most recent computer_screenshot.",
            McpTool.Schema([("x1", "number", "Start X in pixels."), ("y1", "number", "Start Y in pixels."), ("x2", "number", "End X in pixels."), ("y2", "number", "End Y in pixels.")], "x1", "y1", "x2", "y2"),
            (a, _) =>
            {
                d.Drag(a.RequiredInt("x1"), a.RequiredInt("y1"), a.RequiredInt("x2"), a.RequiredInt("y2"));
                return Done("Dragged.");
            }),

        new("computer_scroll", "Turn the mouse wheel with the pointer at a desktop position, so the control under it scrolls. Use it to reach content below the fold in a native app.",
            McpTool.Schema([X, Y, ("down", "number", "Wheel clicks to scroll down; negative scrolls up."), ("right", "number", "Wheel clicks to scroll right; negative scrolls left.")], "x", "y"),
            (a, _) =>
            {
                d.Scroll(a.RequiredInt("x"), a.RequiredInt("y"), a.Int("down") ?? 0, a.Int("right") ?? 0);
                return Done("Scrolled.");
            }),

        new("computer_type", "Type text on the real keyboard into whatever control has focus on the desktop; click the field first. A newline in the text presses Enter. For keys and shortcuts use computer_key.",
            McpTool.Schema([("text", "string", "The text to type, exactly as it should appear.")], "text"),
            (a, _) =>
            {
                d.TypeText(a.RequiredString("text"));
                return Done("Typed.");
            }),

        new("computer_key", "Press one key or keyboard shortcut on the desktop: enter, escape, tab, f5, ctrl+s, ctrl+shift+t, alt+f4, win. Not for entering text; use computer_type for that.",
            McpTool.Schema([("key", "string", "A key name, or a chord joined with +, e.g. ctrl+shift+p.")], "key"),
            (a, _) =>
            {
                d.PressChord(a.RequiredString("key"));
                return Done("Pressed.");
            }),

        new("computer_list_windows", "List the windows open on the desktop, one per line as process: title. Use it to find the exact title for computer_focus_window or computer_screenshot's window filter, or to check whether an app you launched has opened.",
            McpTool.Schema(),
            (_, _) =>
            {
                var sb = new StringBuilder();
                foreach (var (_, title, process) in d.Windows()) sb.AppendLine($"{process}: {title}");
                return Done(sb.Length == 0 ? "(none)" : sb.ToString().TrimEnd());
            }),

        new("computer_focus_window", "Bring a window to the front and give it keyboard focus, found by part of its title. Do this before typing into or screenshotting an app that is behind other windows.",
            McpTool.Schema([("window", "string", "Part of the window title, as computer_list_windows shows it.")], "window"),
            (a, _) => Done(d.Focus(a.RequiredString("window")))),
    ];
}
