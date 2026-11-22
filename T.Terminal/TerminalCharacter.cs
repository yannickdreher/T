namespace T.VT;

/// <summary>
/// Represents a single character cell in the terminal buffer.
/// <para>
/// A double-width character (CJK, emoji) occupies two cells: the left one carries the
/// character and <see cref="CellAttributes.Wide"/>, the right one is empty and marked with
/// <see cref="CellAttributes.WideContinuation"/>.
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
    Justification = "Mutable value type copied and patched in the renderer's hot loops.")]
public struct TerminalCharacter
{
    /// <summary>
    /// Unicode scalar value of the cell, 0 for the right half of a wide character, or a
    /// <see cref="GraphemeTable"/> id (&gt;= <see cref="GraphemeTable.FirstId"/>) for a
    /// character with combining marks.
    /// </summary>
    public int CodePoint;
    public TerminalColor Foreground;
    public TerminalColor Background;
    public CellAttributes Attributes;

    public static readonly TerminalCharacter Blank = new()
    {
        CodePoint = ' ',
        Foreground = TerminalColor.Default,
        Background = TerminalColor.Default,
        Attributes = CellAttributes.None
    };

    public readonly bool IsBold => (Attributes & CellAttributes.Bold) != 0;
    public readonly bool IsDim => (Attributes & CellAttributes.Dim) != 0;
    public readonly bool IsItalic => (Attributes & CellAttributes.Italic) != 0;
    public readonly bool IsUnderline => (Attributes & CellAttributes.Underline) != 0;
    public readonly bool IsInverse => (Attributes & CellAttributes.Inverse) != 0;
    public readonly bool IsBlink => (Attributes & CellAttributes.Blink) != 0;
    public readonly bool IsHidden => (Attributes & CellAttributes.Hidden) != 0;
    public readonly bool IsStrikethrough => (Attributes & CellAttributes.Strikethrough) != 0;

    /// <summary>Left half of a double-width character.</summary>
    public readonly bool IsWide => (Attributes & CellAttributes.Wide) != 0;

    /// <summary>Right half of a double-width character (no content of its own).</summary>
    public readonly bool IsWideContinuation => (Attributes & CellAttributes.WideContinuation) != 0;
}
