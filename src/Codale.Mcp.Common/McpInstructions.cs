namespace Codale.Mcp;

/// <summary>
/// What each of Codale's servers tells the model about itself in the <c>initialize</c> reply's
/// <c>instructions</c>, which MCP clients (Claude Code, Gemini CLI and its forks) add to the system
/// prompt. Only sent when the host asks for it (<see cref="Enabled"/>): an agent CLI in a terminal
/// has no other way to learn them, while the panel's own Claude session gets its fuller directive
/// through the system prompt and would otherwise read them twice.
/// </summary>
public static class McpInstructions
{
    /// <summary>Set by the host on servers it hands to an agent CLI running in a terminal.</summary>
    public const string EnvironmentVariable = "CODALE_INSTRUCTIONS";

    public static bool Enabled => Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";

    public const string Tasks =
        "You are running inside Codale, an editor whose session panel mirrors your work through this server. " +
        "Always report progress with todos_set: pass the whole step list every call (2-6 short steps, one in_progress, " +
        "finished ones completed) when you start a piece of work, as you begin each step and when the last one finishes. " +
        "Use it instead of any built-in task or plan list, which Codale cannot see. " +
        "For a dev server, watcher or any command that keeps running, use start_task (read_task follows its output, stop_task ends it) " +
        "instead of a background shell, so the user sees the task in Codale; quick commands still run in your own shell. " +
        "list_commands, add_command and run_command use the project's saved run commands; list_terminals and read_terminal read the " +
        "user's terminal tabs. Use artifact_add for a deliverable that is not project source (a report, document, image or data file).";

    public const string Browser =
        "You can drive a real Chromium browser with these tools (Codale's own automation browser, not the user's). After you change " +
        "something a browser can show (a web page, UI, or a local dev server), open it with browser_navigate, read it with " +
        "browser_snapshot, look at it with browser_screenshot, exercise the changed flow, and check browser_console_messages for errors, " +
        "before you report the work as done. The browser keeps logins between runs. When a page needs a sign-in, captcha or 2FA, do not " +
        "give up or ask for credentials: call browser_handoff and the user completes it in a visible window. To check responsive layouts " +
        "use browser_set_viewport or browser_responsive_check; browser_emulate switches dark mode. Element refs (e12) come from the latest " +
        "browser_snapshot and expire at the next one.";

    public const string Computer =
        "These tools see and control the user's Windows desktop, for what a browser cannot reach: native apps, installers, system " +
        "dialogs, the running build of a desktop app. They act on the real screen, so take a computer_screenshot before every click " +
        "(coordinates refer to the latest screenshot); the user approves each action.";
}