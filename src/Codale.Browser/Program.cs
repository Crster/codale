using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace Codale.Browser;

/// <summary>
/// Entry point for the unpackaged WinUI 3 browser. One window per user: a second launch brings the
/// running window forward and exits. The Windows App Runtime is bootstrapped before any WinRT call.
/// </summary>
public static class Program
{
    /// <summary>The Windows App Runtime major.minor this build was compiled against (Microsoft.WindowsAppSDK 2.5).</summary>
    private const uint RuntimeMajorMinor = 0x00020005;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    [STAThread]
    private static int Main()
    {
        using var instance = new Mutex(true, @"Local\Codale.WebViewBrowser", out var created);
        if (!created)
        {
            FocusExisting();
            return 0;
        }

        try
        {
            Bootstrap.Initialize(RuntimeMajorMinor, null, default);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            MessageBoxW(IntPtr.Zero, "Codale Browser needs the Windows App Runtime 2.5. Install it from https://aka.ms/windowsappsdk and try again.\n\n" + ex.Message, "Codale Browser", 0x10);
            return 1;
        }

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(callbackParams =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
        }
        finally
        {
            Bootstrap.Shutdown();
        }

        return 0;
    }

    /// <summary>Brings the window another launch already opened to the front (restoring it when minimized).</summary>
    private static void FocusExisting()
    {
        var current = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("Codale.Browser"))
        {
            using (process)
            {
                if (process.Id == current || process.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(process.MainWindowHandle)) ShowWindow(process.MainWindowHandle, 9);
                SetForegroundWindow(process.MainWindowHandle);
                break;
            }
        }
    }
}
