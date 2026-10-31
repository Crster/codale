using Codale.App.Controls;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Codale.App.Views;

/// <summary>
/// What the workspace needs from a centre tab that shows a terminal, whether it is a plain
/// shell (<see cref="TerminalTab"/>) or an agent CLI beside its session panel (<see cref="CliTerminalTab"/>).
/// </summary>
public interface ITerminalHost
{
    /// <summary>A URL or file path in the output was Ctrl+clicked.</summary>
    event EventHandler<LinkMatch>? LinkActivated;

    /// <summary>Live-applies Settings changes to the terminal display.</summary>
    void ApplyAppSettings();

    /// <summary>Shows this terminal's actions at the requested surface, including its tab header.</summary>
    void ShowContextMenu(UIElement anchor, ContextRequestedEventArgs args);

    /// <summary>Tears the display down; called only when the tab is actually closing.</summary>
    void CloseHost();
}
