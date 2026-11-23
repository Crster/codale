using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Codale.Db.Engine;

internal enum KeyKind { Text, Signed, Unsigned, Real, Decimal, Boolean, DateTime, DateTimeOffset, Date, Time, Guid }

internal static class OrderedKey
{
    public static byte[] Text(string value)
    {
        var bytes = new byte[checked(value.Length * 2)];
        for (var i = 0; i < value.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), value[i]);
        return bytes;
    }

    public static string ReadText(byte[] value) => Encoding.BigEndianUnicode.GetString(value);

    public static byte[]? AfterPrefix(byte[]? prefix)
    {
        if (prefix is null) return null;
        for (var i = prefix.Length - 1; i >= 0; i--)
        {
            if (prefix[i] == byte.MaxValue) continue;
            var end = prefix[..(i + 1)];
            end[i]++;
            return end;
        }
        return null;
    }

    public static KeyKind Kind(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(DateTimeOffset)) return KeyKind.DateTimeOffset;
        if (type == typeof(DateOnly)) return KeyKind.Date;
        if (type == typeof(TimeOnly)) return KeyKind.Time;
        if (type == typeof(Guid)) return KeyKind.Guid;
        return Type.GetTypeCode(type) switch
        {
            TypeCode.String or TypeCode.Char => KeyKind.Text,
            TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => KeyKind.Signed,
            TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64 => KeyKind.Unsigned,
            TypeCode.Single or TypeCode.Double => KeyKind.Real,
            TypeCode.Decimal => KeyKind.Decimal,
            TypeCode.Boolean => KeyKind.Boolean,
            TypeCode.DateTime => KeyKind.DateTime,
            _ => throw new NotSupportedException($"{type} cannot be used as an ordered index key."),
        };
    }

    public static byte[] Encode(KeyKind kind, JsonElement value) => kind switch
    {
        KeyKind.Text => Text(value.GetString()!),
        KeyKind.Signed => Signed(value.GetInt64()),
        KeyKind.Unsigned => Unsigned(value.GetUInt64()),
        KeyKind.Real => Real(value.GetDouble()),
        KeyKind.Decimal => Decimal(value.GetDecimal()),
        KeyKind.Boolean => [value.GetBoolean() ? (byte)1 : (byte)0],
        KeyKind.DateTime => Signed(value.GetDateTime().Ticks),
        KeyKind.DateTimeOffset => Signed(value.GetDateTimeOffset().UtcTicks),
        KeyKind.Date => Signed(value.Deserialize<DateOnly>().DayNumber),
        KeyKind.Time => Signed(value.Deserialize<TimeOnly>().Ticks),
        KeyKind.Guid => value.GetGuid().ToByteArray(bigEndian: true),
        _ => throw new InvalidDataException("Unknown ordered key encoding."),
    };

    private static byte[] Signed(long value) => Unsigned(unchecked((ulong)(value ^ long.MinValue)));

    private static byte[] Unsigned(ulong value)
    {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Real(double value)
    {
        var bits = value == 0 ? 0 : BitConverter.DoubleToUInt64Bits(value);
        return Unsigned((bits & 0x8000_0000_0000_0000UL) == 0 ? bits ^ 0x8000_0000_0000_0000UL : ~bits);
    }

    private static byte[] Decimal(decimal value)
    {
        Span<int> chunks = stackalloc int[4];
        decimal.GetBits(value, chunks);
        var magnitude = ((BigInteger)(uint)chunks[2] << 64) | ((BigInteger)(uint)chunks[1] << 32) | (uint)chunks[0];
        var scale = (chunks[3] >> 16) & 0xff;
        var scaled = magnitude * BigInteger.Pow(10, 28 - scale);
        if (chunks[3] < 0) scaled = -scaled;
        var encoded = ((BigInteger.One << 192) + scaled).ToByteArray(isUnsigned: true, isBigEndian: true);
        var bytes = new byte[25];
        encoded.CopyTo(bytes, bytes.Length - encoded.Length);
        return bytes;
    }
}
