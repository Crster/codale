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
        "MANDATORY: You MUST report your progress with the todos_set tool (e.g. todos_set, mcp_codale-tasks_todos_set, or mcp__codale-tasks__todos_set). " +
        "Call it immediately as your FIRST action when starting any user request, whenever you begin each step, and when the last step finishes. " +
        "Pass the COMPLETE step list every call (2-6 short imperative steps, at most one in_progress, finished ones completed). Do NOT use any other task/todo tools. " +
        "MANDATORY: For a dev server, watcher, continuous build, or any long-running command, you MUST use start_task (read_task follows its output, stop_task ends it) " +
        "so the user sees the task in Codale. NEVER run background commands via shell (&, Start-Job, nohup, run_in_background). Quick commands that finish in seconds still run in your foreground shell. " +
        "list_commands, add_command and run_command manage the project's saved run commands; list_terminals and read_terminal read the user's terminal tabs. " +
        "To view static files (a built site, an HTML report) in a browser, serve_folder hosts any folder on localhost instead of an http server started with start_task. " +
        "Use artifact_add for a deliverable that is not project source (a report, document, image or data file).";

    public const string Browser =
        "You can drive a real Chromium browser with codale-browser tools (browser_navigate, browser_snapshot, browser_screenshot, browser_click, " +
        "browser_type, browser_fill_form, browser_console_messages, browser_handoff, browser_responsive_check). " +
        "MANDATORY: When verifying web pages, UI, or local servers, use these browser tools rather than shell workarounds. After you change " +
        "something a browser can show, open it with browser_navigate, read it with browser_snapshot, look at it with browser_screenshot, " +
        "exercise the changed flow, and check browser_console_messages for errors, before you report the work as done. The browser keeps logins between runs. " +
        "When a page needs a sign-in, captcha or 2FA, do not give up or ask for credentials: call browser_handoff and the user completes it in a visible window. " +
        "To check responsive layouts use browser_set_viewport or browser_responsive_check; browser_emulate switches dark mode. Element refs (e12) come from the latest browser_snapshot.";

    public const string Computer =
        "These tools control the user's Windows desktop (computer_screenshot, computer_click, computer_type, computer_key, computer_move_cursor), " +
        "for what a browser cannot reach: native apps, installers, system dialogs, the running build of a desktop app. They act on the real screen, " +
        "so take a computer_screenshot before every click (coordinates refer to the latest screenshot); the user approves each action.";

    public const string Phone =
        "These tools control an Android phone or emulator over adb (phone_devices, phone_screenshot, phone_ui_tree, phone_tap, phone_swipe, " +
        "phone_type, phone_key, phone_install, phone_launch_app, phone_logcat, phone_port_forward), for building and testing mobile apps. " +
        "MANDATORY: NEVER execute raw 'adb' commands via terminal or shell tools. Always use the codale-phone tools instead. " +
        "Call phone_devices first. Take a phone_screenshot or phone_ui_tree before every tap or swipe (coordinates are device pixels on the latest view). " +
        "Build mobile apps with the shell (e.g. gradlew installDebug, flutter run via start_task), install with phone_install, start the app with " +
        "phone_launch_app and check phone_logcat for crashes before you report the work as done. Use phone_port_forward (reverse) so the " +
        "phone can reach a dev server on this computer. Installing, uninstalling, clearing data, file transfer, shell commands and starting an emulator ask the user first.";
}