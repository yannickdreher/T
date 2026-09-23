namespace T.VT;

/// <summary>
/// Represents a single line in the terminal buffer
/// </summary>
public class TerminalLine
{
    private const CellAttributes WideFlags = CellAttributes.Wide | CellAttributes.WideContinuation;

    private TerminalCharacter[] _characters;

    public int Length => _characters.Length;
    public bool DoubleWidth { get; set; }
    public bool DoubleHeightTop { get; set; }
    public bool DoubleHeightBottom { get; set; }

    public TerminalLine(int width)
    {
        _characters = new TerminalCharacter[width];
        Clear();
    }

    public TerminalCharacter this[int index]
    {
        get => index >= 0 && index < _characters.Length ? _characters[index] : TerminalCharacter.Blank;
        set { if (index >= 0 && index < _characters.Length) _characters[index] = value; }
    }

    public void Clear()
    {
        Array.Fill(_characters, TerminalCharacter.Blank);
        DoubleWidth = false;
        DoubleHeightTop = false;
        DoubleHeightBottom = false;
    }

    public void Clear(int startColumn, int endColumn)
    {
        for (int i = startColumn; i <= endColumn && i < _characters.Length; i++)
            _characters[i] = TerminalCharacter.Blank;
    }

    public void Resize(int newWidth)
    {
        var newChars = new TerminalCharacter[newWidth];
        Array.Fill(newChars, TerminalCharacter.Blank);
        Array.Copy(_characters, newChars, Math.Min(_characters.Length, newWidth));
        _characters = newChars;
        if (newWidth > 0) RepairWide(newWidth - 1); // a wide character cut in half at the new right edge
    }

    /// <summary>
    /// Call before cells [<paramref name="start"/>, <paramref name="end"/>) are overwritten:
    /// a double-width character that is only partly covered loses its other half as well.
    /// </summary>
    public void PrepareOverwrite(int start, int end)
    {
        var chars = _characters;
        if (start > 0 && start < chars.Length && (chars[start].Attributes & CellAttributes.WideContinuation) != 0)
            EraseHalf(start - 1);
        if (end > 0 && end < chars.Length && (chars[end].Attributes & CellAttributes.WideContinuation) != 0)
            EraseHalf(end);
    }

    public void InsertCharacters(int column, int count)
    {
        if (column < 0 || column >= _characters.Length || count <= 0) return;
        count = Math.Min(count, _characters.Length - column);

        // Inserting between the halves of a wide character splits it: erase it.
        if (column > 0 && _characters[column].IsWideContinuation)
        {
            EraseHalf(column - 1);
            EraseHalf(column);
        }

        Array.Copy(_characters, column, _characters, column + count, _characters.Length - column - count);
        Array.Fill(_characters, TerminalCharacter.Blank, column, count);
        RepairWide(_characters.Length - 1); // the right half may have been pushed off the line
    }

    public void DeleteCharacters(int column, int count)
    {
        if (column < 0 || column >= _characters.Length || count <= 0) return;
        count = Math.Min(count, _characters.Length - column);

        if (column > 0 && _characters[column].IsWideContinuation)
            EraseHalf(column - 1);

        Array.Copy(_characters, column + count, _characters, column, _characters.Length - column - count);
        Array.Fill(_characters, TerminalCharacter.Blank, _characters.Length - count, count);
        RepairWide(column); // the right half of a deleted wide character may have moved here
    }

    /// <summary>Erases a wide-character half at <paramref name="column"/> whose partner cell is missing.</summary>
    private void RepairWide(int column)
    {
        if ((uint)column >= (uint)_characters.Length) return;
        var cell = _characters[column];
        if (cell.IsWideContinuation && (column == 0 || !_characters[column - 1].IsWide))
            EraseHalf(column);
        else if (cell.IsWide && (column + 1 >= _characters.Length || !_characters[column + 1].IsWideContinuation))
            EraseHalf(column);
    }

    /// <summary>Replaces one half of a wide character with a space (colors are kept, like xterm).</summary>
    private void EraseHalf(int column)
    {
        ref var cell = ref _characters[column];
        cell.CodePoint = ' ';
        cell.Attributes &= ~WideFlags;
    }

    public TerminalLine Clone()
    {
        var clone = new TerminalLine(_characters.Length)
        {
            DoubleWidth = DoubleWidth,
            DoubleHeightTop = DoubleHeightTop,
            DoubleHeightBottom = DoubleHeightBottom
        };
        Array.Copy(_characters, clone._characters, _characters.Length);
        return clone;
    }
}
