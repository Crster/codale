using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;

namespace Codale.App;

/// <summary>
/// The process-wide exception policy. Marking an exception handled keeps the process alive,
/// which is only right for failures that leave nothing half-done (a cancelled operation, a
/// window that closed under an async callback); anything else takes the process down rather
/// than limp on with state nobody can vouch for.
/// </summary>
public static class UnhandledErrors
{
    // RO_E_CLOSED and RPC_E_DISCONNECTED: a WinRT object or window went away mid-call.
    private const int RoEClosed = unchecked((int)0x80000013);
    private const int RpcEDisconnected = unchecked((int)0x80010108);

    /// <summary>Wires the process-wide handlers. Call once, on the UI thread.</summary>
    public static void Install()
    {
        Application.Current.UnhandledException += (_, e) => e.Handled = IsSafeToContinue(e.Exception);

        // Fire-and-forget work is launched as discarded tasks; their failures must not
        // resurface at finalization.
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
    }

    private static bool IsSafeToContinue(Exception? exception) => exception switch
    {
        OperationCanceledException or ObjectDisposedException => true,
        COMException com => com.HResult is RoEClosed or RpcEDisconnected,
        _ => false,
    };
}
