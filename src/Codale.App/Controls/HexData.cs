using System.Collections;
using System.ComponentModel;
using System.Text;

using Microsoft.UI.Xaml;
using Microsoft.Win32.SafeHandles;

namespace Codale.App.Controls;

/// <summary>
/// Random-access, read-only view of a file for the hex viewer. Reads go straight to the
/// handle (no buffering of the whole file), so a multi-GB file opens instantly, and the
/// handle shares read/write/delete so an agent can keep rewriting the file underneath.
/// </summary>
public sealed class HexSource : IDisposable
{
    private readonly SafeFileHandle _handle;

    public HexSource(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public long Length { get; }

    /// <summary>Reads up to <paramref name="count"/> bytes at <paramref name="offset"/>; fewer at the end of the file.</summary>
    public byte[] Read(long offset, int count)
    {
        if (offset < 0 || offset >= Length || count <= 0)
        {
            return [];
        }

        var size = (int)Math.Min(count, Length - offset);
        var buffer = new byte[size];
        var total = 0;

        while (total < size)
        {
            var read = RandomAccess.Read(_handle, buffer.AsSpan(total), offset + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == size ? buffer : buffer[..total];
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Layout shared by the header, the rows and the hit-testing, so they never drift apart.</summary>
public static class HexLayout
{
    /// <summary>Bytes per visual group; groups are separated by one extra space.</summary>
    public const int GroupSize = 8;

    /// <summary>Width of one monospace character in device-independent pixels; measured once by the view.</summary>
    public static double CharWidth { get; set; } = 8;

    /// <summary>Character column of byte <paramref name="i"/> in the hex text.</summary>
    public static int HexColumn(int i) => i * 3 + i / GroupSize;

    /// <summary>The byte index a hex-text character column falls on, clamped into the row.</summary>
    public static int ByteAtHexColumn(int column, int perRow)
    {
        var group = column / (GroupSize * 3 + 1);
        var within = Math.Min(column % (GroupSize * 3 + 1) / 3, GroupSize - 1);
        return Math.Clamp(group * GroupSize + within, 0, perRow - 1);
    }
}

/// <summary>One line of the dump: offset, hex bytes and the printable-ASCII column.</summary>
public sealed class HexRow : INotifyPropertyChanged
{
    private static readonly string[] HexPairs = Enumerable.Range(0, 256).Select(i => i.ToString("X2")).ToArray();

    private readonly int _perRow;
    private long _selStart = -1;
    private long _selEnd = -1;

    public HexRow(long offset, byte[] data, int perRow)
    {
        Offset = offset;
        Data = data;
        _perRow = perRow;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Offset { get; }

    public byte[] Data { get; }

    public string OffsetText => Offset.ToString("X8");

    public string HexText
    {
        get
        {
            var sb = new StringBuilder(_perRow * 3 + _perRow / HexLayout.GroupSize);
            for (var i = 0; i < _perRow; i++)
            {
                sb.Append(i < Data.Length ? HexPairs[Data[i]] : "  ");
                sb.Append(' ');
                if (i % HexLayout.GroupSize == HexLayout.GroupSize - 1 && i < _perRow - 1)
                {
                    sb.Append(' ');
                }
            }

            return sb.ToString();
        }
    }

    public string AsciiText
    {
        get
        {
            var chars = new char[Data.Length];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = Data[i] is >= 0x20 and < 0x7F ? (char)Data[i] : '·';
            }

            return new string(chars);
        }
    }

    // The highlight is a rectangle behind the text; its geometry is plain arithmetic
    // on the (measured) character width, since both columns are monospace.
    public Thickness HexHighlightMargin => new(Span is { } s ? HexLayout.HexColumn(s.First) * HexLayout.CharWidth : 0, 0, 0, 0);

    public double HexHighlightWidth => Span is { } s
        ? (HexLayout.HexColumn(s.Last) + 2 - HexLayout.HexColumn(s.First)) * HexLayout.CharWidth
        : 0;

    public Thickness AsciiHighlightMargin => new(Span is { } s ? s.First * HexLayout.CharWidth : 0, 0, 0, 0);

    public double AsciiHighlightWidth => Span is { } s ? (s.Last - s.First + 1) * HexLayout.CharWidth : 0;

    public Visibility HighlightVisibility => Span is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The selected byte columns inside this row, or null when the selection misses it.</summary>
    private (int First, int Last)? Span
    {
        get
        {
            if (_selStart < 0 || Data.Length == 0)
            {
                return null;
            }

            var rowEnd = Offset + Data.Length - 1;
            if (_selEnd < Offset || _selStart > rowEnd)
            {
                return null;
            }

            return ((int)(Math.Max(_selStart, Offset) - Offset), (int)(Math.Min(_selEnd, rowEnd) - Offset));
        }
    }

    /// <summary>Applies the (inclusive) selection; -1 clears it.</summary>
    public void ApplySelection(long start, long end)
    {
        if (_selStart == start && _selEnd == end)
        {
            return;
        }

        var hadSpan = Span;
        _selStart = start;
        _selEnd = end;

        if (hadSpan is null && Span is null)
        {
            return; // neither the old nor the new selection touches this row
        }

        foreach (var name in new[]
        {
            nameof(HexHighlightMargin), nameof(HexHighlightWidth), nameof(AsciiHighlightMargin),
            nameof(AsciiHighlightWidth), nameof(HighlightVisibility),
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>
/// The rows of the dump as a lazy list: the list view only asks for the rows it draws,
/// so a file of any size is "loaded" without reading it. Rows are cached (first in, first
/// out) so selection changes can reach the ones still on screen.
/// </summary>
public sealed class HexRowList : IList
{
    private const int CacheLimit = 2048;

    private readonly HexSource _source;
    private readonly Dictionary<int, HexRow> _cache = [];
    private readonly Queue<int> _order = new();

    private long _selStart = -1;
    private long _selEnd = -1;

    public HexRowList(HexSource source, int bytesPerRow)
    {
        _source = source;
        BytesPerRow = bytesPerRow;
    }

    public int BytesPerRow { get; }

    public int Count => (int)Math.Min(int.MaxValue, (_source.Length + BytesPerRow - 1) / BytesPerRow);

    public bool IsReadOnly => true;

    public bool IsFixedSize => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int index]
    {
        get
        {
            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (_cache.TryGetValue(index, out var cached))
            {
                return cached;
            }

            var offset = (long)index * BytesPerRow;
            var row = new HexRow(offset, _source.Read(offset, BytesPerRow), BytesPerRow);
            row.ApplySelection(_selStart, _selEnd);

            if (_order.Count >= CacheLimit)
            {
                _cache.Remove(_order.Dequeue());
            }

            _cache[index] = row;
            _order.Enqueue(index);
            return row;
        }
        set => throw new NotSupportedException();
    }

    public HexRow RowAt(long offset) => (HexRow)this[(int)(offset / BytesPerRow)]!;

    public void SetSelection(long start, long end)
    {
        _selStart = start;
        _selEnd = end;

        foreach (var row in _cache.Values)
        {
            row.ApplySelection(start, end);
        }
    }

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i]!;
        }
    }

    public bool Contains(object? value) => value is HexRow;

    public int IndexOf(object? value) => value is HexRow row ? (int)(row.Offset / BytesPerRow) : -1;

    public void CopyTo(Array array, int index) => throw new NotSupportedException();

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();
}

/// <summary>Byte-pattern search over a <see cref="HexSource"/>, chunked so it never holds the file in memory.</summary>
public static class HexSearch
{
    private const int Chunk = 1024 * 1024;

    /// <summary>
    /// Finds the next (or previous) occurrence of <paramref name="pattern"/> strictly after
    /// (or before) <paramref name="from"/>, wrapping around once. Returns -1 when absent.
    /// </summary>
    public static long Find(HexSource source, byte[] pattern, long from, bool forward, bool ignoreCase, CancellationToken token)
    {
        if (pattern.Length == 0 || source.Length < pattern.Length)
        {
            return -1;
        }

        var needle = ignoreCase ? Fold(pattern) : pattern;
        var last = source.Length - pattern.Length; // highest valid match start

        if (forward)
        {
            var start = from + 1;
            var hit = start <= last ? Scan(source, needle, start, last, true, ignoreCase, token) : -1;
            return hit >= 0 || from < 0 ? hit : Scan(source, needle, 0, Math.Min(from, last), true, ignoreCase, token);
        }

        var begin = Math.Min(from - 1, last);
        var found = begin >= 0 ? Scan(source, needle, 0, begin, false, ignoreCase, token) : -1;
        return found >= 0 || from > last ? found : Scan(source, needle, from, last, false, ignoreCase, token);
    }

    /// <summary>Searches match starts in [<paramref name="lo"/>, <paramref name="hi"/>] in the given direction.</summary>
    private static long Scan(HexSource source, byte[] needle, long lo, long hi, bool forward, bool ignoreCase, CancellationToken token)
    {
        var overlap = needle.Length - 1;

        if (forward)
        {
            for (var pos = lo; pos <= hi; pos += Chunk)
            {
                token.ThrowIfCancellationRequested();
                var span = (int)Math.Min(Chunk, hi - pos + 1);
                var data = source.Read(pos, span + overlap);
                var index = IndexOf(data, needle, span, ignoreCase, false);
                if (index >= 0)
                {
                    return pos + index;
                }
            }

            return -1;
        }

        for (var end = hi; end >= lo; end -= Chunk)
        {
            token.ThrowIfCancellationRequested();
            var start = Math.Max(lo, end - Chunk + 1);
            var span = (int)(end - start + 1);
            var data = source.Read(start, span + overlap);
            var index = IndexOf(data, needle, span, ignoreCase, true);
            if (index >= 0)
            {
                return start + index;
            }
        }

        return -1;
    }

    /// <summary>First (or last) match whose start is below <paramref name="starts"/>.</summary>
    private static int IndexOf(byte[] data, byte[] needle, int starts, bool ignoreCase, bool last)
    {
        if (!ignoreCase)
        {
            var window = data.AsSpan(0, Math.Min(data.Length, starts + needle.Length - 1));
            return last ? window.LastIndexOf(needle) : window.IndexOf(needle);
        }

        // The needle is already folded to lower case; jump between candidates of its first byte
        // (vectorized) instead of testing every position.
        var limit = Math.Min(starts, data.Length - needle.Length + 1);
        if (limit <= 0)
        {
            return -1;
        }

        var lower = needle[0];
        var upper = lower is >= (byte)'a' and <= (byte)'z' ? (byte)(lower - 32) : lower;
        var haystack = data.AsSpan(0, limit);
        var offset = last ? limit : 0;

        while (true)
        {
            int rel;
            if (last)
            {
                rel = haystack[..offset].LastIndexOfAny(lower, upper);
                if (rel < 0)
                {
                    return -1;
                }
            }
            else
            {
                rel = haystack[offset..].IndexOfAny(lower, upper);
                if (rel < 0)
                {
                    return -1;
                }

                rel += offset;
            }

            var ok = true;
            for (var j = 1; j < needle.Length; j++)
            {
                if (Fold(data[rel + j]) != needle[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return rel;
            }

            offset = last ? rel : rel + 1;
            if (!last && offset >= limit)
            {
                return -1;
            }
        }
    }

    private static byte Fold(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    private static byte[] Fold(byte[] bytes) => bytes.Select(Fold).ToArray();

    /// <summary>Parses "DE AD be-ef", "0xDEADBEEF" or "de,ad" into bytes; null when it is not whole hex bytes.</summary>
    public static byte[]? ParseHex(string text)
    {
        var digits = new StringBuilder();
        foreach (var token in text.Split([' ', ',', '-', ':', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            digits.Append(token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token);
        }

        if (digits.Length == 0 || digits.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(digits.ToString());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Recognises common formats by their leading bytes, for the viewer's header.</summary>
public static class HexSignatures
{
    private static readonly (byte[] Magic, string Name)[] Known =
    [
        ([0x4D, 0x5A], "Windows executable (PE / MZ)"),
        ([0x7F, 0x45, 0x4C, 0x46], "ELF executable"),
        ([0xCF, 0xFA, 0xED, 0xFE], "Mach-O executable"),
        ([0x50, 0x4B, 0x03, 0x04], "ZIP archive (also docx, xlsx, pptx, nupkg, msix, jar)"),
        ([0x52, 0x61, 0x72, 0x21], "RAR archive"),
        ([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], "7-Zip archive"),
        ([0x1F, 0x8B], "gzip archive"),
        ([0x25, 0x50, 0x44, 0x46], "PDF document"),
        ([0x89, 0x50, 0x4E, 0x47], "PNG image"),
        ([0xFF, 0xD8, 0xFF], "JPEG image"),
        ([0x47, 0x49, 0x46, 0x38], "GIF image"),
        ([0x42, 0x4D], "BMP image"),
        ([0x49, 0x44, 0x33], "MP3 audio (ID3)"),
        ([0x66, 0x4C, 0x61, 0x43], "FLAC audio"),
        ([0x4F, 0x67, 0x67, 0x53], "Ogg container"),
        ([0x1A, 0x45, 0xDF, 0xA3], "Matroska / WebM video"),
        ([0x77, 0x4F, 0x46, 0x46], "WOFF font"),
        ([0x77, 0x4F, 0x46, 0x32], "WOFF2 font"),
        ([0x00, 0x01, 0x00, 0x00], "TrueType font"),
        ([0x4F, 0x54, 0x54, 0x4F], "OpenType font"),
        ([0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66], "SQLite database"),
        ([0x4D, 0x53, 0x43, 0x46], "Cabinet archive"),
        ([0xD0, 0xCF, 0x11, 0xE0], "OLE compound file (legacy Office, MSI)"),
        ([0x00, 0x61, 0x73, 0x6D], "WebAssembly module"),
        ([0xCA, 0xFE, 0xBA, 0xBE], "Java class / Mach-O universal"),
    ];

    public static string? Detect(byte[] head)
    {
        foreach (var (magic, name) in Known)
        {
            if (head.AsSpan().StartsWith(magic))
            {
                return name;
            }
        }

        // RIFF and ISO-BMFF carry their real type a few bytes in.
        if (head.Length >= 12 && head.AsSpan(0, 4).SequenceEqual("RIFF"u8))
        {
            return Encoding.ASCII.GetString(head, 8, 4) switch
            {
                "WAVE" => "WAV audio",
                "AVI " => "AVI video",
                "WEBP" => "WebP image",
                var other => $"RIFF container ({other.Trim()})",
            };
        }

        if (head.Length >= 12 && head.AsSpan(4, 4).SequenceEqual("ftyp"u8))
        {
            return "MP4 / QuickTime media";
        }

        return null;
    }
}
