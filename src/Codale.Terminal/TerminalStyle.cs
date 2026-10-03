namespace Codale.Terminal;

/// <summary>
/// A cell colour: the default (no SGR colour set), a 0-255 palette index, or an
/// exact RGB value from true-colour SGR. The terminal theme resolves Default and
/// Indexed at draw time so the palette lives with the renderer.
/// </summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    public enum Kinds : byte
    {
        Default,
        Indexed,
        Rgb,
    }

    public TerminalColor(Kinds kind, byte index, byte r, byte g, byte b)
    {
        Kind = kind;
        Index = index;
        R = r;
        G = g;
        B = b;
    }

    public static TerminalColor Default { get; } = new(Kinds.Default, 0, 0, 0, 0);

    public static TerminalColor Indexed(byte index) => new(Kinds.Indexed, index, 0, 0, 0);

    public static TerminalColor FromRgb(byte r, byte g, byte b) => new(Kinds.Rgb, 0, r, g, b);

    public Kinds Kind { get; }

    /// <summary>Palette index when <see cref="Kind"/> is <see cref="Kinds.Indexed"/>.</summary>
    public byte Index { get; }

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }

    public bool IsDefault => Kind == Kinds.Default;

    public bool Equals(TerminalColor other) =>
        Kind == other.Kind && Index == other.Index && R == other.R && G == other.G && B == other.B;

    public override bool Equals(object? obj) => obj is TerminalColor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine((byte)Kind, Index, R, G, B);

    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);

    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);
}

/// <summary>SGR character attributes that shape or decorate a run of cells.</summary>
[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1 << 0,
    Faint = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    /// <summary>Foreground and background are swapped at draw time.</summary>
    Reverse = 1 << 5,
    Hidden = 1 << 6,
    Strikeout = 1 << 7,
}

/// <summary>
/// The full look of one terminal cell: colours plus attribute flags. Style structs
/// compare by value so the renderer can coalesce cells into runs.
/// </summary>
public readonly struct CellStyle : IEquatable<CellStyle>
{
    public CellStyle(TerminalColor fg, TerminalColor bg, CellFlags flags)
    {
        Fg = fg;
        Bg = bg;
        Flags = flags;
    }

    public static CellStyle Default { get; } = new(TerminalColor.Default, TerminalColor.Default, CellFlags.None);

    public TerminalColor Fg { get; }

    public TerminalColor Bg { get; }

    public CellFlags Flags { get; }

    public bool Equals(CellStyle other) => Fg == other.Fg && Bg == other.Bg && Flags == other.Flags;

    public override bool Equals(object? obj) => obj is CellStyle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Fg, Bg, Flags);

    public static bool operator ==(CellStyle left, CellStyle right) => left.Equals(right);

    public static bool operator !=(CellStyle left, CellStyle right) => !left.Equals(right);
}

/// <summary>One cell of the terminal grid.</summary>
public struct TerminalCell
{
    /// <summary>The character shown; a space on blank cells.</summary>
    public char Code;

    public CellStyle Style;

    /// <summary>
    /// True on the second half of a double-width character: the cell renders as
    /// blank and the previous cell's glyph spans both columns.
    /// </summary>
    public bool WideTail;

    public static TerminalCell Blank(CellStyle style) => new()
    {
        Code = ' ',
        Style = style,
        WideTail = false,
    };
}
