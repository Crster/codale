using System.Text;

using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class FileTailerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "codale-tail-" + Guid.NewGuid().ToString("N") + ".log");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void A_large_append_is_delivered_in_capped_chunks_without_loss()
    {
        var total = FileTailer.MaxChunkBytes * 2 + 123;
        File.WriteAllBytes(_path, Enumerable.Repeat((byte)'a', total).ToArray());
        using var tailer = new FileTailer(_path);

        var chunks = new List<string>();
        while (tailer.Poll(maxInitialBytes: int.MaxValue) is { } text)
        {
            chunks.Add(text);
        }

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks.Take(2), c => Assert.Equal(FileTailer.MaxChunkBytes, c.Length));
        Assert.Equal(total, chunks.Sum(c => c.Length));
    }

    [Fact]
    public void A_character_split_across_two_polls_is_not_corrupted()
    {
        // "é" is two bytes in UTF-8; write the first byte, poll, then the second.
        var bytes = Encoding.UTF8.GetBytes("café!");
        File.WriteAllBytes(_path, bytes[..4]);
        using var tailer = new FileTailer(_path);

        var first = tailer.Poll();

        using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(bytes, 4, bytes.Length - 4);
        }

        var second = tailer.Poll();

        Assert.Equal("caf", first);
        Assert.Equal("é!", second);
    }

    [Fact]
    public void A_truncated_file_is_read_again_from_the_start()
    {
        File.WriteAllText(_path, "first run output");
        using var tailer = new FileTailer(_path);
        Assert.Equal("first run output", tailer.Poll());

        File.WriteAllText(_path, "new");

        Assert.Equal("new", tailer.Poll());
    }

    [Fact]
    public async Task A_throwing_handler_does_not_end_the_tail()
    {
        File.WriteAllText(_path, "one");
        using var tailer = new FileTailer(_path, TimeSpan.FromMilliseconds(20));
        var received = new List<string>();
        var calls = 0;
        tailer.Chunk += text =>
        {
            received.Add(text);
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("subscriber bug");
            }
        };
        tailer.Start();

        await Until(() => calls >= 1);
        File.AppendAllText(_path, "two");
        await Until(() => calls >= 2);

        Assert.Equal(["one", "two"], received);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
