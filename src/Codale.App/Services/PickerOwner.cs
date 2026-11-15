namespace Codale.App.Services;

/// <summary>
/// Attaches a file or folder picker to the window that raised it. A packaged picker must be told its owner or it can
/// open behind the window; with no native handle the picker simply opens unowned.
/// </summary>
internal static class PickerOwner
{
    /// <summary>For the WinRT pickers (open, save and folder).</summary>
    public static void Attach(object picker, IntPtr? window)
    {
        if (window is { } hwnd)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
    }

    /// <summary>For the App SDK save picker, which takes the owner as a window ID.</summary>
    public static Microsoft.UI.WindowId WindowId(IntPtr? window) =>
        window is { } hwnd ? Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd) : default;
}
