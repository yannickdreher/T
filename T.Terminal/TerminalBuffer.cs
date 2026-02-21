namespace T.VT;

/// <summary>
/// The screen buffer containing all terminal lines
/// </summary>
public class TerminalBuffer
{
    private readonly List<TerminalLine> _lines = [];
    private int _width;
    private int _height;

    public int Width => _width;
    public int Height => _height;
    public int Count => _lines.Count;

    public TerminalBuffer(int width, int height)
    {
        Resize(width, height);
    }

    public TerminalLine this[int row]
    {
        get
        {
            EnsureRow(row);
            return _lines[row];
        }
    }

    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;

        // Adjust existing lines
        foreach (var line in _lines)
            line.Resize(width);

        // Add lines if needed
        while (_lines.Count < height)
            _lines.Add(new TerminalLine(width));
    }

    public void Clear()
    {
        foreach (var line in _lines)
            line.Clear();
    }

    public void EnsureRow(int row)
    {
        while (_lines.Count <= row)
            _lines.Add(new TerminalLine(_width));
    }

    public void ScrollUp(int top, int bottom, int count = 1)
    {
        for (int n = 0; n < count; n++)
        {
            for (int y = top; y < bottom; y++)
            {
                for (int x = 0; x < _width; x++)
                    _lines[y][x] = _lines[y + 1][x];
            }
            _lines[bottom].Clear();
        }
    }

    public void ScrollDown(int top, int bottom, int count = 1)
    {
        for (int n = 0; n < count; n++)
        {
            for (int y = bottom; y > top; y--)
            {
                for (int x = 0; x < _width; x++)
                    _lines[y][x] = _lines[y - 1][x];
            }
            _lines[top].Clear();
        }
    }

    public void InsertLines(int row, int count, int scrollBottom)
    {
        for (int n = 0; n < count; n++)
        {
            for (int y = scrollBottom; y > row; y--)
            {
                for (int x = 0; x < _width; x++)
                    _lines[y][x] = _lines[y - 1][x];
            }
            _lines[row].Clear();
        }
    }

    public void DeleteLines(int row, int count, int scrollBottom)
    {
        for (int n = 0; n < count; n++)
        {
            for (int y = row; y < scrollBottom; y++)
            {
                for (int x = 0; x < _width; x++)
                    _lines[y][x] = _lines[y + 1][x];
            }
            _lines[scrollBottom].Clear();
        }
    }
}