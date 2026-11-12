using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;

using Microsoft.Win32.SafeHandles;

namespace Codale.Db;

internal enum RecordType : byte
{
    Put = 1,
    Remove = 2,
    Vector = 3,
    Link = 4,
    Unlink = 5,
    Index = 6,

    /// <summary>Opens a batch: everything up to the matching <see cref="Commit"/> is one unit, dropped at open if the commit never made it.</summary>
    Begin = 7,
    Commit = 8,
}

internal delegate void ReplayHandler(int segment, long offset, int size, RecordType type, byte flags, ReadOnlySpan<byte> body);

/// <summary>
/// Append-only record log split into segment files. Record layout:
/// <c>[crc32 u32][length u32][type u8][flags u8][body…]</c>, where the CRC covers everything after
/// itself and length counts type + flags + body. Writes go straight to the OS (no user-space buffer),
/// so a read through the same handle always sees them; durability is a separate <see cref="Sync"/>.
/// </summary>
internal sealed class SegmentLog : IDisposable
{
    public const int HeaderSize = 10;
    private const int MaxRecord = 1 << 30;

    private readonly string _dir;
    private readonly long _segmentBytes;
    private readonly Dictionary<int, SafeFileHandle> _handles = [];
    private readonly Dictionary<int, long> _lengths = [];
    private int _active;
    private long _tail;
    private bool _dirty;

    public SegmentLog(string directory, long segmentBytes)
    {
        _dir = directory;
        _segmentBytes = segmentBytes;
    }

    public long TotalBytes { get; private set; }

    /// <summary>Opens every segment, replays its records in order and truncates a torn tail on the last one.</summary>
    public void Open(ReplayHandler handler)
    {
        var ids = Directory.EnumerateFiles(_dir, "seg-*.log")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f)[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1)
            .Where(id => id > 0)
            .Order()
            .ToList();
        if (ids.Count == 0) ids.Add(1);

        foreach (var id in ids)
            _handles[id] = File.OpenHandle(SegmentPath(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        Scan(handler);
    }

    /// <summary>Replays every open segment from the start.</summary>
    public void Scan(ReplayHandler handler)
    {
        var ids = _handles.Keys.Order().ToList();
        TotalBytes = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var length = Replay(id, _handles[id], i == ids.Count - 1, handler);
            _lengths[id] = length;
            TotalBytes += length;
            _active = id;
            _tail = length;
        }
    }

    /// <summary>Reads a segment front to back through one buffer (a read per 64 KB, not two per record).</summary>
    private static long Replay(int id, SafeFileHandle handle, bool last, ReplayHandler handler)
    {
        var length = RandomAccess.GetLength(handle);
        var buffer = new byte[64 << 10];
        var start = 0; // buffer[start..end] is the file from `position` on
        var end = 0;
        long position = 0;
        while (position < length)
        {
            var good = false;
            if (Fill(8))
            {
                var crc = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(start));
                var len = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(start + 4));
                if (len is >= 2 and <= MaxRecord && position + 8 + len <= length && Fill(8 + (int)len))
                {
                    var span = buffer.AsSpan(start + 8, (int)len);
                    if (Crc32.Compute(span) == crc)
                    {
                        handler(id, position, (int)(8 + len), (RecordType)span[0], span[1], span[2..]);
                        start += 8 + (int)len;
                        position += 8 + len;
                        good = true;
                    }
                }
            }
            if (good) continue;
            if (!last) throw new InvalidDataException($"Corrupt record in segment {id} at offset {position}.");
            // A torn write at the very end of the newest segment: drop it and carry on.
            RandomAccess.SetLength(handle, position);
            return position;
        }
        return length;

        bool Fill(int need)
        {
            if (end - start >= need) return true;
            if (need > buffer.Length)
            {
                var larger = new byte[Math.Max(need, buffer.Length * 2)];
                buffer.AsSpan(start, end - start).CopyTo(larger);
                buffer = larger;
            }
            else if (start > 0)
            {
                buffer.AsSpan(start, end - start).CopyTo(buffer);
            }
            end -= start;
            start = 0;
            while (end < need)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(end), position + end);
                if (read <= 0) return false;
                end += read;
            }
            return true;
        }
    }

    private static bool ReadFull(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (!buffer.IsEmpty)
        {
            var read = RandomAccess.Read(handle, buffer, offset);
            if (read <= 0) return false;
            buffer = buffer[read..];
            offset += read;
        }
        return true;
    }

    public (int Segment, long Offset, int Size) Append(RecordType type, byte flags, ReadOnlySpan<byte> part1, ReadOnlySpan<byte> part2 = default)
    {
        var bodyLength = part1.Length + part2.Length;
        var total = HeaderSize + bodyLength;
        if (_tail > 0 && _tail + total > _segmentBytes) Roll();

        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)(2 + bodyLength));
            buffer[8] = (byte)type;
            buffer[9] = flags;
            part1.CopyTo(buffer.AsSpan(HeaderSize));
            part2.CopyTo(buffer.AsSpan(HeaderSize + part1.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, Crc32.Compute(buffer.AsSpan(8, 2 + bodyLength)));
            RandomAccess.Write(_handles[_active], buffer.AsSpan(0, total), _tail);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        var offset = _tail;
        _tail += total;
        _lengths[_active] = _tail;
        TotalBytes += total;
        _dirty = true;
        return (_active, offset, total);
    }

    /// <summary>Appends a copy of an existing record as it is (compaction): its checksum travels with it, so nothing is re-encoded.</summary>
    public (int Segment, long Offset, int Size) Copy(int segment, long offset, int size)
    {
        if (_tail > 0 && _tail + size > _segmentBytes) Roll();
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var record = buffer.AsSpan(0, size);
            Read(segment, offset, record);
            RandomAccess.Write(_handles[_active], record, _tail);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        var at = _tail;
        _tail += size;
        _lengths[_active] = _tail;
        TotalBytes += size;
        _dirty = true;
        return (_active, at, size);
    }

    public void Read(int segment, long offset, Span<byte> destination)
    {
        if (!ReadFull(_handles[segment], destination, offset)) throw new InvalidDataException($"Segment {segment} is shorter than the index expects.");
    }

    /// <summary>Forces the newest segment to stable storage.</summary>
    public void Sync()
    {
        if (!_dirty) return;
        _dirty = false;
        RandomAccess.FlushToDisk(_handles[_active]);
    }

    private void Roll()
    {
        Sync();
        var id = _active + 1;
        _handles[id] = File.OpenHandle(SegmentPath(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _lengths[id] = 0;
        _active = id;
        _tail = 0;
    }

    /// <summary>Starts a fresh segment for compaction output; returns its id, below which everything is old.</summary>
    public int BeginCompaction()
    {
        if (_tail > 0) Roll();
        return _active;
    }

    /// <summary>
    /// Deletes the segments compaction replaced, oldest first. The order matters: if the process dies part-way, what
    /// is left is a tail of the old log followed by the complete new one, which replays to the same state. Deleting
    /// a newer segment before an older one could bring back data that the newer one had removed.
    /// </summary>
    public void DeleteSegmentsBefore(int first)
    {
        var blocked = false;
        foreach (var id in _handles.Keys.Where(id => id < first).Order().ToList())
        {
            _handles[id].Dispose();
            _handles.Remove(id);
            TotalBytes -= _lengths[id];
            _lengths.Remove(id);
            if (blocked) continue;
            // A file something else holds open (a scanner, a backup) is left for the next open to replay and the
            // next compaction to delete; so is everything newer than it, to keep what remains a tail.
            try { File.Delete(SegmentPath(id)); }
            catch (IOException) { blocked = true; }
            catch (UnauthorizedAccessException) { blocked = true; }
        }
    }

    /// <summary>
    /// Cuts the log back to a point (an unfinished batch found at open): later segments go, newest first, then the
    /// segment itself is truncated. Dying part-way leaves the unfinished batch in place to be cut again.
    /// </summary>
    public void TruncateAt(int segment, long offset)
    {
        foreach (var id in _handles.Keys.Where(id => id > segment).OrderDescending().ToList())
        {
            _handles[id].Dispose();
            _handles.Remove(id);
            TotalBytes -= _lengths[id];
            _lengths.Remove(id);
            File.Delete(SegmentPath(id));
        }
        RandomAccess.SetLength(_handles[segment], offset);
        RandomAccess.FlushToDisk(_handles[segment]);
        TotalBytes -= _lengths[segment] - offset;
        _lengths[segment] = offset;
        _active = segment;
        _tail = offset;
    }

    private string SegmentPath(int id) => Path.Combine(_dir, $"seg-{id:D8}.log");

    public void Dispose()
    {
        try { Sync(); } catch (ObjectDisposedException) { }
        foreach (var handle in _handles.Values) handle.Dispose();
        _handles.Clear();
    }
}
