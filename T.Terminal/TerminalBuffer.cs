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

        // Remove excess lines (keep the top of the screen stable)
        if (_lines.Count > height)
            _lines.RemoveRange(height, _lines.Count - height);
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

    // O(1) per line: moves line references instead of copying every cell.
    public void ScrollUp(int top, int bottom, int count = 1)
    {
        EnsureRow(bottom);
        if (top < 0 || top > bottom) return;
        count = Math.Min(count, bottom - top + 1);
        for (int n = 0; n < count; n++)
        {
            var line = _lines[top];
            _lines.RemoveAt(top);
            line.Clear();
            _lines.Insert(bottom, line);
        }
    }

    /// <summary>
    /// Scrolls up by one line and returns the detached top line (for scrollback)
    /// without cloning. A fresh line is inserted at the bottom.
    /// </summary>
    public TerminalLine ScrollUpDetach(int top, int bottom)
    {
        EnsureRow(bottom);
        var line = _lines[top];
        _lines.RemoveAt(top);
        _lines.Insert(bottom, new TerminalLine(_width));
        return line;
    }

    public void ScrollDown(int top, int bottom, int count = 1)
    {
        EnsureRow(bottom);
        if (top < 0 || top > bottom) return;
        count = Math.Min(count, bottom - top + 1);
        for (int n = 0; n < count; n++)
        {
            var line = _lines[bottom];
            _lines.RemoveAt(bottom);
            line.Clear();
            _lines.Insert(top, line);
        }
    }

    public void InsertLines(int row, int count, int scrollBottom)
    {
        EnsureRow(scrollBottom);
        if (row < 0 || row > scrollBottom) return;
        count = Math.Min(count, scrollBottom - row + 1);
        for (int n = 0; n < count; n++)
        {
            var line = _lines[scrollBottom];
            _lines.RemoveAt(scrollBottom);
            line.Clear();
            _lines.Insert(row, line);
        }
    }

    public void DeleteLines(int row, int count, int scrollBottom)
    {
        EnsureRow(scrollBottom);
        if (row < 0 || row > scrollBottom) return;
        count = Math.Min(count, scrollBottom - row + 1);
        for (int n = 0; n < count; n++)
        {
            var line = _lines[row];
            _lines.RemoveAt(row);
            line.Clear();
            _lines.Insert(scrollBottom, line);
        }
    }
}