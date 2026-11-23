using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codale.Mcp.Browser;

/// <summary>A Chrome DevTools Protocol error reported for one command.</summary>
public sealed class CdpException(string message) : Exception(message);

/// <summary>
/// One DevTools Protocol connection to a page target: JSON commands over a WebSocket, with
/// replies matched by id and events handed to subscribers on the receive loop.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, List<Action<JsonElement>>> _handlers = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private Task? _receive;
    private int _nextId;

    /// <summary>Optional protocol parameters are left out rather than sent as null, which Chromium rejects.</summary>
    private static readonly JsonSerializerOptions ParameterOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>True until the socket closes - the window was shut, or the page target went away.</summary>
    public bool IsOpen => _socket.State == WebSocketState.Open && _receive is { IsCompleted: false };

    public static async Task<CdpConnection> ConnectAsync(Uri webSocketUrl, CancellationToken ct = default)
    {
        var connection = new CdpConnection();
        connection._socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await connection._socket.ConnectAsync(webSocketUrl, ct).ConfigureAwait(false);
        connection._receive = Task.Run(connection.ReceiveLoopAsync);
        return connection;
    }

    /// <summary>
    /// Calls <paramref name="handler"/> for every <paramref name="method"/> event, on the receive
    /// loop (so it must not wait on a command reply); dispose the result to stop listening.
    /// </summary>
    public IDisposable On(string method, Action<JsonElement> handler)
    {
        var list = _handlers.GetOrAdd(method, _ => []);
        lock (list) list.Add(handler);
        return new Subscription(() => { lock (list) list.Remove(handler); });
    }

    private sealed class Subscription(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }

    /// <summary>Waits for the next <paramref name="method"/> event that matches, or null after the timeout.</summary>
    public async Task<JsonElement?> WaitForEventAsync(string method, Func<JsonElement, bool>? match, TimeSpan timeout, Task? until = null)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = On(method, e => { if (match?.Invoke(e) != false) tcs.TrySetResult(e.Clone()); });
        var waits = new List<Task> { tcs.Task, Task.Delay(timeout) };
        if (until is not null) waits.Add(until);
        var done = await Task.WhenAny(waits).ConfigureAwait(false);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, TimeSpan? timeout = null)
    {
        if (!IsOpen) throw new CdpException("The connection to Codale Browser is closed.");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var message = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = JsonSerializer.SerializeToNode(parameters, ParameterOptions);
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());

        await _send.WaitAsync().ConfigureAwait(false);
        try { await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, _closing.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new CdpException("The connection to Codale Browser is closed.");
        }
        finally { _send.Release(); }

        var limit = timeout ?? TimeSpan.FromSeconds(30);
        if (await Task.WhenAny(tcs.Task, Task.Delay(limit)).ConfigureAwait(false) != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            throw new CdpException($"{method} did not answer within {limit.TotalSeconds:0}s.");
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _closing.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Closed under us: the window was shut or we are disposing.
        }
        finally
        {
            foreach (var (id, pending) in _pending)
            {
                if (_pending.TryRemove(id, out _)) pending.TrySetException(new CdpException("Codale Browser closed the connection."));
            }
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> data)
    {
        using var json = JsonDocument.Parse(data);
        var root = json.RootElement;
        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
        {
            if (!_pending.TryRemove(id, out var tcs)) return;
            if (root.TryGetProperty("error", out var error))
            {
                var text = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
                tcs.TrySetException(new CdpException(text ?? "DevTools error"));
            }
            else
            {
                tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }

            return;
        }

        if (!root.TryGetProperty("method", out var methodElement) || methodElement.GetString() is not { } method) return;
        if (!_handlers.TryGetValue(method, out var handlers)) return;
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        Action<JsonElement>[] snapshot;
        lock (handlers) snapshot = [.. handlers];
        foreach (var handler in snapshot)
        {
            try { handler(parameters); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(1000);
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Already gone.
        }

        _closing.Cancel();
        if (_receive is not null) await _receive.ConfigureAwait(false);
        _socket.Dispose();
        _closing.Dispose();
    }
}
