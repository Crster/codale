namespace Codale.Editor;

/// <summary>A position in the document: 0-based line and 0-based UTF-16 column.</summary>
public readonly record struct DocumentPosition(int Line, int Col) : IComparable<DocumentPosition>
{
    public int CompareTo(DocumentPosition other)
    {
        var byLine = Line.CompareTo(other.Line);
        return byLine != 0 ? byLine : Col.CompareTo(other.Col);
    }

    public static bool operator <(DocumentPosition left, DocumentPosition right) => left.CompareTo(right) < 0;
    public static bool operator >(DocumentPosition left, DocumentPosition right) => left.CompareTo(right) > 0;
    public static bool operator <=(DocumentPosition left, DocumentPosition right) => left.CompareTo(right) <= 0;
    public static bool operator >=(DocumentPosition left, DocumentPosition right) => left.CompareTo(right) >= 0;

    public static DocumentPosition Min(DocumentPosition a, DocumentPosition b) => a <= b ? a : b;
    public static DocumentPosition Max(DocumentPosition a, DocumentPosition b) => a >= b ? a : b;

    public override string ToString() => $"({Line},{Col})";
}

/// <summary>
/// One caret with its selection anchor. <see cref="PreferredX"/> is the horizontal pixel offset
/// vertical movement tries to keep (NaN when the next Up/Down should take it from the caret).
/// </summary>
public readonly record struct Selection(DocumentPosition Anchor, DocumentPosition Caret, double PreferredX = double.NaN)
{
    public Selection(DocumentPosition caret) : this(caret, caret)
    {
    }

    public bool IsEmpty => Anchor == Caret;

    public DocumentPosition Start => DocumentPosition.Min(Anchor, Caret);

    public DocumentPosition End => DocumentPosition.Max(Anchor, Caret);

    /// <summary>True when the caret sits before the anchor (the selection was made backwards).</summary>
    public bool IsReversed => Caret < Anchor;

    public bool Contains(DocumentPosition position) => !IsEmpty && position >= Start && position <= End;

    public Selection Collapse(DocumentPosition at) => new(at, at);

    public Selection WithCaret(DocumentPosition caret, bool extend) =>
        new(extend ? Anchor : caret, caret);

    public override string ToString() => IsEmpty ? Caret.ToString() : $"{Anchor}->{Caret}";
}
