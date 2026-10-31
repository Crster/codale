using System.IO.Pipes;

namespace Codale.Core.Browser;

/// <summary>A private per-chat channel for releasing browser emulation when an agent turn ends.</summary>
public sealed class BrowserViewportControl : IAsyncDisposable
{
    public const string EnvironmentKey = "CODALE_BROWSER_CONTROL_PIPE";
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;

    public BrowserViewportControl(string pipeName, Func<Task> restore) =>
        _listener = ListenAsync(pipeName, restore);

    private async Task ListenAsync(string pipeName, Func<Task> restore)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                if (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) == "restore")
                {
                    var result = "restored";
                    try { await restore().ConfigureAwait(false); }
                    catch (Exception) { result = "failed"; }
                    await writer.WriteLineAsync(result).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (IOException) { /* caller or browser closed; accept the next connection */ }
        }
    }

    public static async Task<bool> RestoreAsync(string pipeName, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("restore".AsMemory(), cancellation.Token).ConfigureAwait(false);
            return await reader.ReadLineAsync(cancellation.Token).ConfigureAwait(false) == "restored";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _listener.ConfigureAwait(false);
        _stop.Dispose();
    }
}
