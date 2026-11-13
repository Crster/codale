using System.Collections;

using Codale.Git;

using Microsoft.UI.Xaml;

namespace Codale.App.ViewModels;

/// <summary>One row of the diff pane: a hunk's @@ header, or one diff line.</summary>
public sealed class DiffRow
{
    private static readonly Thickness FirstHeaderMargin = new(0);
    private static readonly Thickness HeaderMargin = new(0, 10, 0, 0);

    private DiffRow(string? header, DiffLine? line, bool first)
    {
        Header = header ?? "";
        Line = line;
        HeaderSpacing = first ? FirstHeaderMargin : HeaderMargin;
    }

    public static DiffRow ForHeader(string header, bool first) => new(header, null, first);

    public static DiffRow ForLine(DiffLine line) => new(null, line, false);

    public DiffLine? Line { get; }

    public string Header { get; }

    public Thickness HeaderSpacing { get; }

    public Visibility HeaderVisibility => Line is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LineVisibility => Line is null ? Visibility.Collapsed : Visibility.Visible;

    public DiffLineKind Kind => Line?.Kind ?? DiffLineKind.Context;

    public string OldText => Line?.OldText ?? "";

    public string NewText => Line?.NewText ?? "";

    public string Sign => Line?.Sign ?? "";

    public string Text => Line?.Text ?? "";
}

/// <summary>
/// A file's hunks as one flat, indexable list of rows for a virtualizing ItemsRepeater. Rows are
/// made on demand from an index (a binary search over the hunks' starting rows), so a 200,000-line
/// diff costs one small array up front and only the rows on screen ever exist as objects.
/// </summary>
public sealed class DiffRowList : IList, IReadOnlyList<DiffRow>
{
    public static readonly DiffRowList Empty = new([]);

    private readonly IReadOnlyList<DiffHunk> _hunks;

    /// <summary>Row index of each hunk's header; the hunk's lines follow it.</summary>
    private readonly int[] _starts;

    public DiffRowList(IReadOnlyList<DiffHunk> hunks)
    {
        _hunks = hunks;
        _starts = new int[hunks.Count];
        var row = 0;
        for (var i = 0; i < hunks.Count; i++)
        {
            _starts[i] = row;
            row += 1 + hunks[i].Lines.Count;
        }

        Count = row;
    }

    public int Count { get; }

    public DiffRow this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var hunk = Array.BinarySearch(_starts, index);
            if (hunk >= 0)
            {
                return DiffRow.ForHeader(_hunks[hunk].Header, hunk == 0);
            }

            hunk = ~hunk - 1;
            return DiffRow.ForLine(_hunks[hunk].Lines[index - _starts[hunk] - 1]);
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public bool IsFixedSize => true;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public IEnumerator<DiffRow> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Rows are made fresh per index, so identity lookups have nothing to find.
    public int IndexOf(object? value) => -1;

    public bool Contains(object? value) => false;

    public void CopyTo(Array array, int index)
    {
        foreach (var row in this)
        {
            array.SetValue(row, index++);
        }
    }

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();
}
