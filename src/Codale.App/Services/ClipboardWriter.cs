using System.Runtime.InteropServices;

using Windows.ApplicationModel.DataTransfer;

namespace Codale.App.Services;

/// <summary>
/// Puts text on the clipboard and keeps trying until it sticks. The clipboard is one
/// shared lock: clipboard managers, remote sessions and other apps hold it open for a
/// few milliseconds after every change, and a copy that lands in that window throws.
/// WinRT is tried first; the plain Win32 clipboard is the second route when it keeps
/// failing, and the waits between rounds are async so the UI thread is never parked.
/// </summary>
internal static class ClipboardWriter
{
    private const int Attempts = 10;

    private const uint CfUnicodeText = 13;

    private const uint GmemMoveable = 0x0002;

    /// <summary>True once the text is on the clipboard; must be called on the UI thread.</summary>
    public static async Task<bool> SetTextAsync(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (TrySetWinRt(text) || TrySetWin32(text))
            {
                return true;
            }

            await Task.Delay(30 * (attempt + 1));
        }

        return false;
    }

    private static bool TrySetWinRt(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);

            try
            {
                // Hands the data to the system so it survives this app losing focus or exiting.
                Clipboard.Flush();
            }
            catch (Exception)
            {
                // Best effort: the content is already set.
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TrySetWin32(string text)
    {
        var bytes = (UIntPtr)((text.Length + 1) * sizeof(char));
        var memory = GlobalAlloc(GmemMoveable, bytes);
        if (memory == IntPtr.Zero)
        {
            return false;
        }

        var owned = true;
        try
        {
            var target = GlobalLock(memory);
            if (target == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            if (!OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            try
            {
                if (!EmptyClipboard() || SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
                {
                    return false;
                }

                owned = false; // the clipboard owns the block now
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (owned)
            {
                GlobalFree(memory);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
