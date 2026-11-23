using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Codale.Browser;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, @"Local\Codale.WebViewBrowser", out var created);
        if (!created)
        {
            var current = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName("Codale.Browser"))
            {
                using (process)
                {
                    if (process.Id == current || process.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(process.MainWindowHandle, 9);
                    SetForegroundWindow(process.MainWindowHandle);
                    break;
                }
            }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new BrowserWindow());
    }
}
