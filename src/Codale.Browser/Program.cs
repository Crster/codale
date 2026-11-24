using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace Codale.Browser;

/// <summary>
/// Entry point for the unpackaged WinUI 3 browser. One window per user: a second launch asks the
/// running window to come forward over its control pipe, then exits. The Windows App Runtime is
/// bootstrapped before any WinRT call.
/// </summary>
public static class Program
{
    /// <summary>The Windows App Runtime major.minor this build was compiled against (Microsoft.WindowsAppSDK 2.7).</summary>
    private const uint RuntimeMajorMinor = 0x00020007;
    private const string CodalePackageName = "BB9ADD6B-2E64-46A7-ADF5-885B70F99D51";

    /// <summary>
    /// The only Win32 call left. Before the runtime is up there is no WinUI window to report a missing
    /// Windows App Runtime, so a native message box is the one way to tell the person.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    [STAThread]
    private static int Main() => Run();

    private static int Run()
    {
        using var instance = new Mutex(true, @"Local\Codale.WebViewBrowser", out var created);
        if (!created)
        {
            RequestShow();
            return 0;
        }

        var packaged = HasCodalePackageIdentity();
        try
        {
            if (!packaged) Bootstrap.Initialize(RuntimeMajorMinor, null, default);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            MessageBoxW(IntPtr.Zero, "Codale Browser needs the Windows App Runtime 2.7. Install it from https://aka.ms/windowsappsdk and try again.\n\n" + ex.Message + $"\n\n0x{ex.HResult:X8} ({ex.GetType().Name})", "Codale Browser", 0x10);
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
            if (!packaged) Bootstrap.Shutdown();
        }

        return 0;
    }

    /// <summary>True only when launched from Codale.App; other packaged parents (such as the IDE) need their own runtime bootstrap.</summary>
    private static bool HasCodalePackageIdentity()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id.Name == CodalePackageName;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Unpackaged: there is no package identity at all (APPMODEL_ERROR_NO_PACKAGE, which WinRT surfaces as InvalidOperationException).
            return false;
        }
    }

    /// <summary>Asks the running window to come forward. Its control pipe does the work; nothing is sent if it is not reachable.</summary>
    private static void RequestShow()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", ControlPipe.Name, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            writer.WriteLine("{\"op\":\"show\"}");
            reader.ReadLine();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The running window is not reachable; this launch simply ends.
        }
    }
}
