using System.Diagnostics;
using System.Text;

namespace Codale.Core.Agents;

/// <summary>
/// Follows a growing text file (a background shell's output) by polling. Opens with
/// <see cref="FileShare.ReadWrite"/> so the writer is never blocked, and reads only
/// the bytes added since the last poll. Chunks arrive on a thread-pool thread.
/// </summary>
public sealed class FileTailer : IDisposable
{
    private readonly string _path;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>One read never takes more than this, so a log that grew by gigabytes is delivered over several polls.</summary>
    public const int MaxChunkBytes = 1024 * 1024;

    /// <summary>Carries a multi-byte character cut by a chunk boundary over to the next read.</summary>
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private long _offset;
    private bool _disposed;

    public FileTailer(string path, TimeSpan? interval = null)
    {
        _path = path;
        _interval = interval ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>Raised with each newly appended chunk (the whole file first, capped).</summary>
    public event Action<string>? Chunk;

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>One poll: returns text appended since the last call, or null if none.</summary>
    public string? Poll(int maxInitialBytes = 256 * 1024)
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _offset)
            {
                _offset = 0; // truncated or replaced
                _decoder.Reset();
            }

            if (_offset == 0 && stream.Length > maxInitialBytes)
            {
                _offset = stream.Length - maxInitialBytes; // join a huge log near its end
            }

            if (stream.Length == _offset)
            {
                return null;
            }

            stream.Seek(_offset, SeekOrigin.Begin);
            var buffer = new byte[(int)Math.Min(stream.Length - _offset, MaxChunkBytes)];
            var read = stream.Read(buffer, 0, buffer.Length);
            _offset += read;
            if (read == 0)
            {
                return null;
            }

            var chars = new char[_decoder.GetCharCount(buffer, 0, read, flush: false)];
            var count = _decoder.GetChars(buffer, 0, read, chars, 0, flush: false);

            // A chunk that ended inside a character yields nothing yet; the rest arrives next poll.
            return count == 0 ? null : new string(chars, 0, count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // not created yet, or momentarily locked: try again next tick
        }
    }

    private async Task LoopAsync()
    {
        var token = _cts.Token;

        try
        {
            using var timer = new PeriodicTimer(_interval);
            do
            {
                // Drain what is there: a backlog bigger than one chunk should not wait a tick per megabyte.
                while (!token.IsCancellationRequested && Poll() is { } text)
                {
                    try
                    {
                        Chunk?.Invoke(text);
                    }
                    catch (Exception e)
                    {
                        // A throwing subscriber must not end the tail for everyone after it.
                        Trace.WriteLine($"FileTailer chunk handler failed: {e.Message}");
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
