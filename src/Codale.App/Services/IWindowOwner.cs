namespace Codale.App.Services;

/// <summary>
/// The project window that owns the pickers, dialogs and taskbar feedback raised from its content.
/// Views receive it explicitly; they never look the window up through the application.
/// </summary>
public interface IWindowOwner
{
    /// <summary>The native window handle, or null when the window has no native handle yet.</summary>
    IntPtr? WindowHandle { get; }
}