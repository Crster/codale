using System.Buffers.Binary;
using System.IO.Compression;

namespace Codale.Db;

/// <summary>Blobs: binary values stored Brotli-compressed, with their original length in front.</summary>
public sealed partial class CodaleDb
{
    internal void WriteBlob(string path, ReadOnlySpan<byte> data)
    {
        var buffer = new byte[4 + BrotliEncoder.GetMaxCompressedLength(data.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, data.Length);
        if (!BrotliEncoder.TryCompress(data, buffer.AsSpan(4), out var written, Math.Clamp(_options.BlobQuality, 0, 11), 22))
            throw new InvalidOperationException("Brotli compression failed.");
        WriteBytes(path, buffer.AsSpan(0, 4 + written), ValueFlags.Blob, null);
    }

    internal byte[]? ReadBlob(string path)
    {
        _lock.EnterReadLock();
        try
        {
            var value = FindNode(path, create: false)?.Value;
            if (value is null) return null;
            if ((value.Flags & ValueFlags.Blob) == 0) throw new InvalidOperationException("This value was not written with WriteBlob().");
            var raw = ReadRaw(value);
            var output = new byte[BinaryPrimitives.ReadInt32LittleEndian(raw)];
            if (!BrotliDecoder.TryDecompress(raw.AsSpan(4), output, out var written) || written != output.Length)
                throw new InvalidDataException("Blob is corrupt.");
            return output;
        }
        finally { _lock.ExitReadLock(); }
    }
}
