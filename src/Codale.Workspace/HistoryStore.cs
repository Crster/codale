using System.Globalization;
using System.Text;

using Codale.Db;

namespace Codale.Workspace;

/// <summary>Chat records addressed by their UTF-8 byte offsets. Archives are ranges over the same immutable records.</summary>
public sealed class HistoryStore(string directory, WorkspaceDatabasePool? databases = null)
{
    public const string Current = "log";
    private static readonly State Empty = new(0, 0, []);
    private WorkspaceDatabasePool Pool => databases ?? WorkspaceDb.Shared;

    private sealed record Line(string Text);
    private sealed record Archive(int Number, long Start, long End);
    private sealed record State(long Start, long End, Archive[] Archives, bool Deleted = false);

    private static string Prefix(string feature) => feature.Length.ToString(CultureInfo.InvariantCulture) + ":" + feature + ":";
    private static string Key(string feature, long offset) => Prefix(feature) + offset.ToString("D20", CultureInfo.InvariantCulture);
    private static long SizeOf(string line) => Encoding.UTF8.GetByteCount(line) + 1L;

    private T Use<T>(string feature, Func<CodaleDatabase, State, T> work) =>
        Pool.UseRecords(directory, db => work(db, db.Record<State>().Key(feature).Get() ?? Empty));

    public int Generation(string feature) => Use(feature, (_, state) => state.Archives.Length);
    public long Length(string feature) => Use(feature, (_, state) => state.End - state.Start);

    public void Append(string feature, string line) => Use(feature, (db, state) =>
    {
        if (state.Deleted) return false;
        db.WriteBatch(batch =>
        {
            batch.Put(Key(feature, state.End), new Line(line));
            batch.Put(feature, state with { End = state.End + SizeOf(line) });
        });
        return true;
    });

    public IReadOnlyList<string> Generations(string feature) => Use<IReadOnlyList<string>>(feature, (_, state) =>
    {
        var archives = state.Archives.Select(archive => "arc." + archive.Number.ToString(CultureInfo.InvariantCulture));
        return state.Start < state.End ? archives.Append(Current).ToArray() : archives.ToArray();
    });

    public IReadOnlyList<string> Lines(string feature, string generation, long fromOffset = 0) =>
        Use<IReadOnlyList<string>>(feature, (db, state) =>
        {
            var (start, end) = Range(state, generation);
            var from = Math.Max(0, fromOffset);
            if (from >= end - start) return [];
            return [.. db.Record<Line>().FindRange(Key(feature, start + from), Key(feature, end)).Select(row => row.Value.Text)];
        });

    public IReadOnlyList<(long NextOffset, string Line)> Page(string feature, string generation, long offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (offset < 0 || limit == 0) return [];
        return Use<IReadOnlyList<(long, string)>>(feature, (db, state) =>
        {
            var (start, end) = Range(state, generation);
            if (offset >= end - start) return [];
            var first = Key(feature, start + offset);
            var records = db.Record<Line>().FindRange(first, Key(feature, end), limit);
            if (records.Count == 0 || records[0].Key != first) return [];
            var page = new List<(long, string)>(records.Count);
            foreach (var record in records)
            {
                offset += SizeOf(record.Value.Text);
                page.Add((offset, record.Value.Text));
            }
            return page;
        });
    }

    public void ArchiveUpTo(string feature, long offset) => Use(feature, (db, state) =>
    {
        if (offset <= 0 || state.Start == state.End) return false;
        var cut = state.End;
        if (offset < state.End - state.Start)
        {
            var next = db.Record<Line>().FindRange(Key(feature, state.Start + offset), Key(feature, state.End), 1);
            if (next.Count > 0) cut = long.Parse(next[0].Key.AsSpan(Prefix(feature).Length), CultureInfo.InvariantCulture);
        }
        var number = state.Archives.Length == 0 ? 1 : state.Archives[^1].Number + 1;
        db.Record<State>().Key(feature).Value(state with { Start = cut, Archives = [.. state.Archives, new(number, state.Start, cut)] });
        return true;
    });

    public void Delete(string feature) => Use(feature, (db, state) =>
    {
        if (state.Deleted) return false;
        var keys = db.Record<Line>().Keys(Prefix(feature));
        db.WriteBatch(batch =>
        {
            foreach (var key in keys) batch.Delete<Line>(key);
            batch.Put(feature, Empty with { Deleted = true });
        });
        return true;
    });

    private static (long Start, long End) Range(State state, string generation)
    {
        if (generation == Current) return (state.Start, state.End);
        if (generation.StartsWith("arc.", StringComparison.Ordinal) && int.TryParse(generation.AsSpan(4), out int number) &&
            state.Archives.FirstOrDefault(archive => archive.Number == number) is { } match) return (match.Start, match.End);
        return default;
    }
}
