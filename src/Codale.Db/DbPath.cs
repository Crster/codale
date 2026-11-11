using System.Text;

namespace Codale.Db;

/// <summary>A node's address, rendered the way the database prints it: <c>setting."ai provider"."Claude API key"</c>.</summary>
public readonly struct DbPath : IEquatable<DbPath>
{
    private readonly string[]? _segments;

    internal DbPath(string[] segments) => _segments = segments;

    public IReadOnlyList<string> Segments => _segments ?? [];

    internal static DbPath FromRaw(string raw) => new(raw.Length == 0 ? [] : raw.Split(CodaleDb.Sep));

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var segment in Segments)
        {
            if (builder.Length > 0) builder.Append('.');
            if (IsPlain(segment)) builder.Append(segment);
            else builder.Append('"').Append(segment.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }
        return builder.ToString();
    }

    private static bool IsPlain(string segment) => segment.Length > 0 && segment.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    public bool Equals(DbPath other) => Segments.SequenceEqual(other.Segments);
    public override bool Equals(object? obj) => obj is DbPath other && Equals(other);
    public override int GetHashCode() => Segments.Aggregate(17, (hash, s) => hash * 31 + s.GetHashCode());
    public static bool operator ==(DbPath left, DbPath right) => left.Equals(right);
    public static bool operator !=(DbPath left, DbPath right) => !left.Equals(right);
}
