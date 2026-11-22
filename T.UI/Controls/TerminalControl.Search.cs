using System;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using T.VT;

namespace T.UI.Controls;

public partial class TerminalControl
{
    private static readonly TimeSpan SearchWorkBudget = TimeSpan.FromMilliseconds(2);
    private static readonly SolidColorBrush SearchMatchBrush = new(Color.FromRgb(0xF2, 0xB1, 0x34));
    private static readonly SolidColorBrush SearchCurrentBrush = new(Color.FromRgb(0xFF, 0x8C, 0x1A));
    private static readonly Pen SearchCurrentPen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0xA8, 0x4A)), 1.5);
    private readonly DispatcherTimer _searchTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };
    private TerminalSearchIndex? _searchIndex;
    private TerminalSearchIndex.Hit? _selectedSearchHit;
    private bool _searchDirty;
    private bool _searchScanning;
    private bool _searchNeedsInitialScroll;
    private long _nextSearchRefresh;

    internal event Action? SearchRequested;
    internal event Action<int, int, bool>? SearchUpdated;

    internal static bool IsSearchQueryValid(string? query) =>
        query is { Length: >= 2 } && Rune.TryGetRuneAt(query, 0, out var first) && query.Length > first.Utf16SequenceLength;

    internal void SetSearch(string? query, bool matchCase)
    {
        if (query is null || !IsSearchQueryValid(query))
        {
            ClearSearch();
            return;
        }

        _searchIndex = new TerminalSearchIndex(query, matchCase);
        _selectedSearchHit = null;
        _searchDirty = true;
        _searchScanning = false;
        _searchNeedsInitialScroll = true;
        _nextSearchRefresh = 0;
        SearchUpdated?.Invoke(0, 0, true);
        _selectionLayer.InvalidateVisual();
        ScheduleSearch();
    }

    internal void ClearSearch()
    {
        _searchTimer.Stop();
        _searchIndex = null;
        _selectedSearchHit = null;
        _searchDirty = false;
        _searchScanning = false;
        SearchUpdated?.Invoke(0, 0, false);
        _selectionLayer.InvalidateVisual();
    }

    internal void MoveSearch(int direction)
    {
        if (_terminal is not { } terminal || _searchIndex is not { Count: > 0 } index || _searchScanning) return;

        int selected = _selectedSearchHit is { } hit ? index.IndexOf(terminal, hit) : -1;
        int next = selected >= 0
            ? (selected + direction + index.Count) % index.Count
            : index.IndexAtOrAfter(VisibleSearchStart(terminal));
        if (index.GetHit(terminal, next) is not { } target)
        {
            _searchDirty = true;
            ScheduleSearch();
            return;
        }

        _selectedSearchHit = target;
        ScrollToSearchHit(terminal, target.Row);
        SearchUpdated?.Invoke(next + 1, index.Count, false);
        _selectionLayer.InvalidateVisual();
    }

    private void ScheduleSearch()
    {
        if (_searchIndex != null && _searchDirty && _isAttached && IsEffectivelyVisible && !_isShutdown)
            _searchTimer.Start();
        else
            _searchTimer.Stop();
    }

    private void ProcessSearch()
    {
        if (!IsEffectivelyVisible)
        {
            _searchTimer.Stop();
            return;
        }
        if (_terminal is not { } terminal || _searchIndex is not { } index || !_searchDirty)
            return;
        long now = Stopwatch.GetTimestamp();
        if (!_searchScanning && now < _nextSearchRefresh)
            return;

        _searchScanning = !index.Update(terminal, SearchWorkBudget);
        if (_searchScanning)
            return;

        _searchDirty = false;
        _searchTimer.Stop();
        _nextSearchRefresh = now + Stopwatch.Frequency / 4;

        int selected = _selectedSearchHit is { } hit ? index.IndexOf(terminal, hit) : -1;
        if (selected < 0)
            selected = index.IndexAtOrAfter(VisibleSearchStart(terminal));
        _selectedSearchHit = index.GetHit(terminal, selected);
        if (_searchNeedsInitialScroll && _selectedSearchHit is { } first)
            ScrollToSearchHit(terminal, first.Row);
        _searchNeedsInitialScroll = false;

        SearchUpdated?.Invoke(_selectedSearchHit.HasValue ? selected + 1 : 0, index.Count, false);
        _selectionLayer.InvalidateVisual();
    }

    private long VisibleSearchStart(VirtualTerminal terminal) =>
        TerminalSearchIndex.ScreenStart(terminal) -
        (terminal.IsAlternateScreen ? 0 : Math.Min(_scrollOffset, terminal.Scrollback.Count));

    private void ScrollToSearchHit(VirtualTerminal terminal, long row)
    {
        long top = VisibleSearchStart(terminal);
        if (row >= top && row < top + terminal.Height) return;

        _lastScrollbackLinesAdded = terminal.ScrollbackLinesAdded;
        long offset = TerminalSearchIndex.ScreenStart(terminal) - row + terminal.Height / 2;
        SetScrollOffset((int)Math.Clamp(offset, 0, terminal.Scrollback.Count));
    }

    private void RenderSearchHighlights(DrawingContext context)
    {
        if (_terminal is not { } terminal || _searchIndex is not { } index) return;
        if (_searchDirty && !_searchTimer.IsEnabled) ScheduleSearch();

        // Amber like the find highlights of VS Code / Windows Terminal: blue is already the selection.
        long top = VisibleSearchStart(terminal);
        Rect? selectedRect = null;
        using (context.PushOpacity(0.35))
        {
            for (int row = 0; row < terminal.Height; row++)
            {
                long position = top + row;
                foreach (var match in index.GetRowMatches(terminal, position))
                {
                    var rect = new Rect(Padding.Left + match.StartColumn * _charWidth, Padding.Top + row * _lineHeight,
                        (match.EndColumn - match.StartColumn) * _charWidth, _lineHeight);
                    if (_selectedSearchHit is { } current && current.Row == position && current.Columns == match)
                        selectedRect = rect;
                    else
                        context.FillRectangle(SearchMatchBrush, rect);
                }
            }
        }

        // The current match: stronger fill and an outline, so it stands out among the others.
        if (selectedRect is { } outline)
        {
            using (context.PushOpacity(0.5))
                context.FillRectangle(SearchCurrentBrush, outline);
            context.DrawRectangle(null, SearchCurrentPen, outline);
        }
    }
}
