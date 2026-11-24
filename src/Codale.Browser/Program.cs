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
    /// <summary>The Windows App Runtime major.minor this build was compiled against (Microsoft.WindowsAppSDK 2.7).</summary>
    private const uint RuntimeMajorMinor = 0x00020007;
    private const string CodalePackageName = "BB9ADD6B-2E64-46A7-ADF5-885B70F99D51";
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder? name);

    /// <summary>True only when launched from Codale.App; other packaged parents (such as the IDE) need their own runtime bootstrap.</summary>
    private static bool HasCodalePackageIdentity()
    {
        var packageFullName = CurrentPackageFullName();
        return packageFullName.StartsWith(CodalePackageName + "_", StringComparison.OrdinalIgnoreCase);
    }

    private static string CurrentPackageFullName()
    {
        var length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        if (result == AppModelErrorNoPackage) return "";
        if (result != ErrorInsufficientBuffer || length <= 0) return $"<GetCurrentPackageFullName returned {result}>";

        var fullName = new System.Text.StringBuilder(length);
        result = GetCurrentPackageFullName(ref length, fullName);
        return result == 0 ? fullName.ToString() : $"<GetCurrentPackageFullName returned {result}>";
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

        var packaged = HasCodalePackageIdentity();
        try
        {
            if (!packaged) Bootstrap.Initialize(RuntimeMajorMinor, null, default);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log("bootstrap-failed", $"{ex.GetType().FullName}; HRESULT=0x{ex.HResult:X8}; package={CurrentPackageFullName()}{Environment.NewLine}{ex}");
            MessageBoxW(IntPtr.Zero, "Codale Browser needs the Windows App Runtime 2.7. Install it from https://aka.ms/windowsappsdk and try again.\n\n" + ex.Message + $"\n\n0x{ex.HResult:X8} ({ex.GetType().Name})", "Codale Browser", 0x10);
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
