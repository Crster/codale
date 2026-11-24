using System.Runtime.InteropServices;

namespace Codale.App.Services;

/// <summary>
/// Reads plain text from the Win32 clipboard. WinUI's Clipboard.GetContent can fail with
/// an intermittent, uncatchable stowed exception when called from a text control's Paste event.
/// </summary>
internal static class ClipboardReader
{
    private const uint CfUnicodeText = 13;
    private const uint CfHDrop = 15;
    private const uint CfBitmap = 2;
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;

    /// <summary>Whether plain Unicode text is currently advertised by the clipboard.</summary>
    public static bool ContainsText => IsClipboardFormatAvailable(CfUnicodeText);

    /// <summary>Whether the clipboard advertises a file drop, which takes precedence over text.</summary>
    public static bool ContainsFiles => IsClipboardFormatAvailable(CfHDrop);

    /// <summary>Whether the clipboard advertises a standard bitmap format.</summary>
    public static bool ContainsBitmap => IsClipboardFormatAvailable(CfBitmap) ||
        IsClipboardFormatAvailable(CfDib) || IsClipboardFormatAvailable(CfDibV5);

    /// <summary>Reads Unicode text when the clipboard is available; false means retry or skip.</summary>
    public static bool TryGetText(out string text)
    {
        text = "";
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                text = Marshal.PtrToStringUni(pointer) ?? "";
                return true;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);
}
