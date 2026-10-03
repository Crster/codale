using System.Runtime.InteropServices;
using System.Security;

using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Codale.App.Services;

/// <summary>
/// Gets the user's attention when a chat needs them and Codale is not the window they
/// are looking at: the taskbar button flashes until the window is brought forward, and
/// a Windows notification says what is waiting. Clicking the notification opens the
/// project through the codale: protocol, which the instancing in Program routes to the
/// window that already owns it - so it lands in the right window even with several open.
/// </summary>
internal static class Attention
{
    /// <summary>True when the window is the one in front; nothing needs flashing then.</summary>
    public static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;

    /// <summary>
    /// Flashes the taskbar button (and caption) until the window comes to the
    /// foreground - the standard Windows "this app wants you" signal.
    /// </summary>
    public static void FlashTaskbar(IntPtr hwnd)
    {
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Hwnd = hwnd,
            Flags = FlashAll | FlashUntilForeground,
            Count = uint.MaxValue,
            Timeout = 0,
        };

        FlashWindowEx(ref info);
    }

    /// <summary>
    /// A Windows notification. The packaged app's identity is what makes the legacy
    /// notifier work without registration; a failure (notifications off, focus assist,
    /// an unpackaged debug run) just means no toast - the taskbar flash still happened.
    /// </summary>
    public static void Notify(string title, string body, Uri launch)
    {
        try
        {
            var xml = $"""
                <toast activationType="protocol" launch="{SecurityElement.Escape(launch.ToString())}">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{SecurityElement.Escape(title)}</text>
                      <text>{SecurityElement.Escape(body)}</text>
                    </binding>
                  </visual>
                </toast>
                """;

            var document = new XmlDocument();
            document.LoadXml(xml);
            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(document)
            {
                // A question nobody answered in an hour is stale; the flash already said it.
                ExpirationTime = DateTimeOffset.Now.AddHours(1),
            });
        }
        catch (Exception ex)
        {
            CrashLog.Trace($"Notification failed: {ex.Message}");
        }
    }

    private const uint FlashAll = 0x3;              // FLASHW_ALL: caption and taskbar button
    private const uint FlashUntilForeground = 0xC;  // FLASHW_TIMERNOFG

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
