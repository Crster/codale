using System.IO.Pipes;
using System.Text.Json;

namespace Codale.Browser;

/// <summary>
/// The channel agents use to get a tab of their own: one JSON request per line, one JSON answer
/// per line. Only this Windows user can connect. Requests are
/// <c>{"op":"open","session":key,"label":text}</c> (that session's tab, created on first use;
/// answers with its DevTools target id) and <c>{"op":"focus","session":key}</c> (show that tab).
/// The optional project key selects the profile for these requests and for <c>show</c>,
/// <c>cookies-info</c>, and <c>cookies-clear</c>. Codale.Core.Browser.BrowserHost is the client.
/// </summary>
internal sealed class ControlPipe : IDisposable
{
    public static string Name => "Codale.Browser.Control." + Environment.UserName;

    private readonly Func<JsonElement, Task<object>> _handle;
    private readonly CancellationTokenSource _stop = new();

    public ControlPipe(Func<JsonElement, Task<object>> handle)
    {
        _handle = handle;
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected));
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch (Exception) { pipe?.Dispose(); await Task.Delay(200).ConfigureAwait(false); } // never let the accept loop die
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            try
            {
                while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                {
                    object answer;
                    try
                    {
                        using var request = JsonDocument.Parse(line);
                        answer = await _handle(request.RootElement.Clone()).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !_stop.IsCancellationRequested)
                    {
                        answer = new { error = ex.Message };
                    }

                    await writer.WriteLineAsync(JsonSerializer.Serialize(answer)).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The agent hung up.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
    }
}
