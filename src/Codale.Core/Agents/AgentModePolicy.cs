namespace Codale.Core.Agents;

/// <summary>Maps Codale's host-controlled modes to the shared CLI vocabulary.</summary>
public static class AgentModePolicy
{
    public const string Ask = "ask";
    public const string Automatic = "automatic";

    public static bool IsHostMode(string mode) => mode is Ask or Automatic;
    public static bool IsFullMode(string mode) => mode is "auto" or "dontAsk" or "bypassPermissions";

    /// <summary>
    /// Codale offers Auto, Interactive, Plan and Full (plus Ask for explain-only turns). The CLI's
    /// "acceptEdits" and "default" are not modes of ours: both land on Interactive ("manual").
    /// </summary>
    public static string ToSupported(string mode) => mode is "acceptEdits" or "default" ? "manual" : mode;

    public static string ToCli(string mode) => AgentPermissionMode.Normalize(mode switch
    {
        Ask or Automatic => "manual",
        "auto" or "dontAsk" => "bypassPermissions",
        _ => mode,
    });

    public static bool RepresentsHostMode(string hostMode, string reported) => hostMode switch
    {
        Ask => reported is "manual" or "default",
        Automatic => reported is "manual" or "acceptEdits" or "auto" or "default",
        "auto" or "dontAsk" => reported is "bypassPermissions" or "dontAsk",
        _ => false,
    };
}