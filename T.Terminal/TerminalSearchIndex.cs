using System.Diagnostics;

namespace T.VT;

/// <summary>
/// Incrementally searches the active screen and its history on the terminal's owning thread.
/// Detached scrollback rows remain unchanged until evicted, so only new history is projected
/// on each refresh. Screen results are checked against the live row and its revision.
/// </summary>
public sealed class TerminalSearchIndex(string query, bool matchCase)
{
    public readonly record struct Hit(TerminalLine Line, long Row, TerminalSearchMatch Columns);

    private sealed record RowMatches(TerminalLine Line, int Revision, long Row, TerminalSearchMatch[] Matches, long FirstIndex)
    {
        public long EndIndex => FirstIndex + Matches.Length;
    }

    private readonly TerminalLineSearch _search = new(query, matchCase);
    private readonly List<RowMatches> _history = [];
    private readonly List<RowMatches> _screen = [];
    private TerminalBuffer? _buffer;
    private int _width;
    private long _nextHistoryRow;
    private long _nextHistoryMatch;

    public int Count => HistoryMatchCount + (_screen.Count == 0 ? 0 : (int)_screen[^1].EndIndex);

    private long FirstHistoryMatch => _history.Count == 0 ? _nextHistoryMatch : _history[0].FirstIndex;
    private int HistoryMatchCount => (int)(_nextHistoryMatch - FirstHistoryMatch);

    public static long ScreenStart(VirtualTerminal terminal) =>
        terminal.IsAlternateScreen ? 0 : terminal.ScrollbackLinesAdded;

    public static long HistoryStart(VirtualTerminal terminal) =>
        terminal.IsAlternateScreen ? 0 : ScreenStart(terminal) - terminal.Scrollback.Count;

    public bool Update(VirtualTerminal terminal, TimeSpan budget)
    {
        long firstHistoryRow = HistoryStart(terminal);
        long screenStart = ScreenStart(terminal);
        if (_buffer != terminal.Buffer || _width != terminal.Width)
        {
            _buffer = terminal.Buffer;
            _width = terminal.Width;
            _history.Clear();
            _screen.Clear();
            _nextHistoryRow = firstHistoryRow;
            _nextHistoryMatch = 0;
        }

        var started = Stopwatch.GetTimestamp();
        int evicted = FirstRowAtOrAfter(_history, firstHistoryRow);
        if (evicted > 0) _history.RemoveRange(0, evicted);
        _nextHistoryRow = Math.Max(_nextHistoryRow, firstHistoryRow);

        while (_nextHistoryRow < screenStart)
        {
            var line = terminal.Scrollback[(int)(_nextHistoryRow - firstHistoryRow)];
            var matches = _search.Find(line, _width, terminal.Graphemes);
            if (matches.Length > 0)
            {
                _history.Add(new RowMatches(line, line.Revision, _nextHistoryRow, matches, _nextHistoryMatch));
                _nextHistoryMatch += matches.Length;
            }
            _nextHistoryRow++;
            if (_nextHistoryRow < screenStart && Stopwatch.GetElapsedTime(started) >= budget)
            {
                // The previous screen matches may belong to lines that are history now:
                // drop them until the scan has caught up, so count and navigation stay valid.
                _screen.Clear();
                return false;
            }
        }

        _screen.Clear();
        long matchIndex = 0;
        for (int row = 0; row < terminal.Height; row++)
        {
            var line = terminal.Buffer[row];
            var matches = _search.Find(line, _width, terminal.Graphemes);
            if (matches.Length == 0) continue;
            _screen.Add(new RowMatches(line, line.Revision, screenStart + row, matches, matchIndex));
            matchIndex += matches.Length;
        }

        return true;
    }

    public int IndexAtOrAfter(long row)
    {
        int index = FirstRowAtOrAfter(_history, row);
        if (index < _history.Count)
            return (int)(_history[index].FirstIndex - FirstHistoryMatch);
        index = FirstRowAtOrAfter(_screen, row);
        return index < _screen.Count ? HistoryMatchCount + (int)_screen[index].FirstIndex : 0;
    }

    public int IndexOf(VirtualTerminal terminal, Hit hit)
    {
        var row = GetCurrentRow(terminal, hit.Row);
        // A screen row can move within the active buffer. Retain a selected hit
        // on the same line as long as the cells covering it still match.
        if (row is null || row.Line != hit.Line)
        {
            row = _screen.Find(candidate => candidate.Line == hit.Line);
            if (row is null || GetCurrentRow(terminal, row.Row) != row)
                return -1;
        }

        int offset = Array.IndexOf(row.Matches, hit.Columns);
        if (offset < 0) return -1;
        return (int)(row.FirstIndex + offset + (row.Row < ScreenStart(terminal) ? -FirstHistoryMatch : HistoryMatchCount));
    }

    public Hit? GetHit(VirtualTerminal terminal, int index)
    {
        if ((uint)index >= (uint)Count) return null;
        bool inHistory = index < HistoryMatchCount;
        var rows = inHistory ? _history : _screen;
        long absoluteIndex = inHistory ? FirstHistoryMatch + index : index - HistoryMatchCount;
        int start = 0;
        int end = rows.Count;
        while (start < end)
        {
            int middle = start + (end - start) / 2;
            if (rows[middle].EndIndex <= absoluteIndex) start = middle + 1;
            else end = middle;
        }

        var row = rows[start];
        return GetCurrentRow(terminal, row.Row) == row
            ? new Hit(row.Line, row.Row, row.Matches[(int)(absoluteIndex - row.FirstIndex)])
            : null;
    }

    public ReadOnlySpan<TerminalSearchMatch> GetRowMatches(VirtualTerminal terminal, long row) =>
        GetCurrentRow(terminal, row)?.Matches;

    private RowMatches? GetCurrentRow(VirtualTerminal terminal, long row)
    {
        if (_buffer != terminal.Buffer || _width != terminal.Width) return null;
        long start = HistoryStart(terminal);
        long screenStart = ScreenStart(terminal);
        if (row < start || row >= screenStart + terminal.Height) return null;

        bool inHistory = row < screenStart;
        var rows = inHistory ? _history : _screen;
        int index = FirstRowAtOrAfter(rows, row);
        if (index == rows.Count || rows[index].Row != row) return null;

        var result = rows[index];
        var line = inHistory
            ? terminal.Scrollback[(int)(row - start)]
            : terminal.Buffer[(int)(row - screenStart)];
        return result.Line == line && result.Revision == line.Revision ? result : null;
    }

    private static int FirstRowAtOrAfter(List<RowMatches> rows, long row)
    {
        int start = 0;
        int end = rows.Count;
        while (start < end)
        {
            int middle = start + (end - start) / 2;
            if (rows[middle].Row < row) start = middle + 1;
            else end = middle;
        }
        return start;
    }
}
