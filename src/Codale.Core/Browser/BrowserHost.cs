using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

using Codale.Core.Projects;

namespace Codale.Core.Browser;

/// <summary>Launches and sends requests to the per-user Codale Browser window.</summary>
public static class BrowserHost
{
    public static async Task<JsonElement?> RequestAsync(object request, TimeSpan timeout, TimeSpan? connectTimeout = null,
        bool allowForegroundActivation = false)
    {
        using var cancellation = new CancellationTokenSource(connectTimeout ?? timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", "Codale.Browser.Control." + Environment.UserName,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            if (connectTimeout is not null) cancellation.CancelAfter(timeout);
            // Grant the connected browser permission before it handles the user's show request.
            if (allowForegroundActivation && OperatingSystem.IsWindows() &&
                GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
            {
                AllowSetForegroundWindow(processId);
            }

            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellation.Token).ConfigureAwait(false);
            if (await reader.ReadLineAsync(cancellation.Token).ConfigureAwait(false) is not { } line) return null;
            using var answer = JsonDocument.Parse(line);
            return answer.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    public static async Task LaunchAsync(string executable)
    {
        // Explorer launches the unpackaged browser without inheriting the caller's package identity.
        // The startup pipe carries the data root that Explorer cannot pass in the environment.
        await using var pipe = new NamedPipeServerStream("Codale.Browser.Startup." + Environment.UserName, PipeDirection.Out,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = $"\"{executable}\"",
            UseShellExecute = false,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(CodalePaths.DataRoot).AsMemory(), timeout.Token).ConfigureAwait(false);
            await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Explorer may have opened an already-running instance. The caller checks its control pipe.
        }
    }
}
