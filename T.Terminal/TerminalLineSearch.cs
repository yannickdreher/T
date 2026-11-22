using System.Text;

namespace T.VT;

public readonly record struct TerminalSearchMatch(int StartColumn, int EndColumn);

/// <summary>Projects one rendered line to searchable text and maps matches back to terminal cells.</summary>
public sealed class TerminalLineSearch(string query, bool matchCase)
{
    private readonly StringComparison _comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private readonly StringBuilder _text = new();
    private readonly List<TerminalSearchMatch> _cells = [];
    private readonly List<TerminalSearchMatch> _matches = [];

    public TerminalSearchMatch[] Find(TerminalLine line, int visibleWidth, GraphemeTable graphemes)
    {
        if (query.Length == 0 || query.Contains('\0'))
            return [];

        _text.Clear();
        _cells.Clear();
        _matches.Clear();

        int width = Math.Min(visibleWidth, line.Length);
        for (int column = 0; column < width; column++)
        {
            var cell = line[column];
            if (cell.IsWideContinuation) continue;

            int start = _text.Length;
            int cellWidth = cell.IsWide && column + 1 < width && line[column + 1].IsWideContinuation ? 2 : 1;
            if (cell.IsHidden)
                _text.Append('\0');
            else
                graphemes.AppendText(_text, cell.CodePoint);

            var range = new TerminalSearchMatch(column, column + cellWidth);
            for (int i = start; i < _text.Length; i++)
                _cells.Add(range);
        }

        int length = _text.Length;
        while (length > 0 && _text[length - 1] == ' ')
            length--;
        if (length < query.Length)
            return [];

        string text = _text.ToString(0, length);
        int offset = 0;
        while (offset <= text.Length - query.Length)
        {
            int found = text.IndexOf(query, offset, _comparison);
            if (found < 0) break;
            int end = found + query.Length;

            // A match must cover whole cells: "fe" must not match the start of "é" written as
            // "e" + combining accent, nor half of a surrogate pair or an emoji sequence.
            bool startsAtCell = found == 0 || _cells[found - 1] != _cells[found];
            bool endsAtCell = end == text.Length || _cells[end] != _cells[end - 1];
            if (!startsAtCell || !endsAtCell)
            {
                offset = found + 1;
                continue;
            }

            offset = end;
            _matches.Add(new TerminalSearchMatch(_cells[found].StartColumn, _cells[end - 1].EndColumn));
        }
        return _matches.ToArray();
    }
}
