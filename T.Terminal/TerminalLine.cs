namespace T.VT;

/// <summary>
/// Represents a single line in the terminal buffer
/// </summary>
public class TerminalLine
{
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
    }

    public void InsertCharacters(int column, int count)
    {
        for (int x = _characters.Length - 1; x >= column + count; x--)
            _characters[x] = _characters[x - count];
        for (int x = column; x < column + count && x < _characters.Length; x++)
            _characters[x] = TerminalCharacter.Blank;
    }

    public void DeleteCharacters(int column, int count)
    {
        for (int x = column; x < _characters.Length - count; x++)
            _characters[x] = _characters[x + count];
        for (int x = _characters.Length - count; x < _characters.Length; x++)
            _characters[x] = TerminalCharacter.Blank;
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