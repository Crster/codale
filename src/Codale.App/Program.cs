using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Codale.App;

/// <summary>
/// Entry point. Replaces the generated XAML main (see DISABLE_XAML_GENERATED_MAIN)
/// because instancing has to be resolved before the XAML application starts.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var request = ActivationRequest.From(activation, args);

        // Codale is multi-instance, but only one window per project. Whoever registers
        // the project's key first owns it; everyone else hands their activation over
        // and exits, which is what makes reopening a project focus the existing window.
        var keyInstance = AppInstance.FindOrRegisterForKey(request.InstanceKey);

        if (!keyInstance.IsCurrent)
        {
            RedirectActivationTo(activation, keyInstance);
            return 0;
        }

        Application.Start(callbackParams =>
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(dispatcherQueue));

            // Application takes ownership of itself; the instance is intentionally unheld.
            new App(request, keyInstance);
        });

        return 0;
    }

    /// <summary>
    /// Hands this launch to the instance that already owns the project.
    /// </summary>
    /// <remarks>
    /// Awaiting <c>RedirectActivationToAsync</c> directly on the STA main thread
    /// deadlocks, so it is pumped from the thread pool and waited on with a semaphore.
    /// This is the pattern the Windows App SDK samples use.
    /// </remarks>
    private static void RedirectActivationTo(AppActivationArguments activation, AppInstance keyInstance)
    {
        // Not disposed: after a timeout the pool task still releases it, and the process exits right after.
        var redirected = new SemaphoreSlim(0, 1);

        _ = Task.Run(async () =>
        {
            try
            {
                await keyInstance.RedirectActivationToAsync(activation);
            }
            finally
            {
                redirected.Release();
            }
        });

        redirected.Wait(TimeSpan.FromSeconds(10));
    }
}
