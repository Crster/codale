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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder? name);

    /// <summary>True when launched from a packaged Codale.App: children inherit its package identity and runtime dependency.</summary>
    private static bool HasPackageIdentity()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) != 15700; // APPMODEL_ERROR_NO_PACKAGE
    }

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
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            // Surface startup crashes: stderr for the launching app, and a log next to the browser's data.
            Console.Error.WriteLine(ex.ToString());
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "browser-crash.log"), DateTime.Now + Environment.NewLine + ex);
            }
            catch (Exception) { }
            return ex.HResult != 0 ? ex.HResult : 1;
        }
    }

    private static int Run()
    {
        using var instance = new Mutex(true, @"Local\Codale.WebViewBrowser", out var created);
        if (!created)
        {
            FocusExisting();
            return 0;
        }

        var packaged = HasPackageIdentity();
        try
        {
            if (!packaged) Bootstrap.Initialize(RuntimeMajorMinor, null, default);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            MessageBoxW(IntPtr.Zero, "Codale Browser needs the Windows App Runtime 2.5. Install it from https://aka.ms/windowsappsdk and try again.\n\n" + ex.Message, "Codale Browser", 0x10);
            return 1;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("unhandled", e.ExceptionObject);
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
            if (!packaged) Bootstrap.Shutdown();
        }

        return 0;
    }

    /// <summary>Appends a line to the browser's trace log next to its data (never throws).</summary>
    internal static void Log(string kind, object? ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "browser-trace.log"), $"{DateTime.Now:HH:mm:ss.fff} [{kind}] {ex}{Environment.NewLine}");
        }
        catch (Exception) { }
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
