namespace T.Models;

/// <summary>How the open terminals are arranged.</summary>
public enum TerminalLayout
{
    /// <summary>One terminal: the selected tab.</summary>
    One,

    /// <summary>Two terminals next to each other.</summary>
    SideBySide,

    /// <summary>Two terminals above each other.</summary>
    Stacked,

    /// <summary>Four terminals, two by two.</summary>
    Grid
}

/// <summary>Facts about a <see cref="TerminalLayout"/>.</summary>
public static class TerminalLayoutExtensions
{
    /// <summary>Number of terminals the layout shows at once.</summary>
    public static int PaneCount(this TerminalLayout layout) => layout switch
    {
        TerminalLayout.One => 1,
        TerminalLayout.Grid => 4,
        _ => 2
    };
}

/// <summary>
/// Decides which open items (sessions) are shown in the panes of a split layout. The panes keep
/// their content as long as possible: selecting a hidden item replaces the content of the pane
/// that was active, and nothing else moves.
/// </summary>
public sealed class PaneSet<T> where T : class
{
    private T?[] _panes = new T?[1];

    /// <summary>The content of each pane; null is an empty pane.</summary>
    public IReadOnlyList<T?> Panes => _panes;

    /// <summary>The pane that shows the active item.</summary>
    public int ActiveIndex { get; private set; }

    /// <summary>
    /// Brings the panes in line with the open items and the active one. Returns true when the
    /// content of a pane changed.
    /// </summary>
    public bool Update(int paneCount, IReadOnlyList<T> open, T? active)
    {
        paneCount = Math.Max(1, paneCount);
        var before = _panes;
        var panes = new T?[paneCount];

        // Keep what is still open, in its pane; panes beyond the new count are dropped.
        for (int i = 0; i < Math.Min(paneCount, before.Length); i++)
        {
            if (before[i] is { } item && open.Contains(item))
                panes[i] = item;
        }

        int activeIndex = Math.Min(ActiveIndex, paneCount - 1);
        if (active != null && open.Contains(active))
        {
            int shown = Array.IndexOf(panes, active);
            if (shown >= 0)
            {
                activeIndex = shown;
            }
            else
            {
                // A free pane first; otherwise the pane the user was working in.
                int free = Array.IndexOf(panes, null);
                activeIndex = free >= 0 ? free : activeIndex;
                panes[activeIndex] = active;
            }
        }

        // Fill the remaining panes with open items that are not shown yet.
        foreach (var item in open)
        {
            int free = Array.IndexOf(panes, null);
            if (free < 0) break;
            if (Array.IndexOf(panes, item) < 0)
                panes[free] = item;
        }

        bool changed = !panes.SequenceEqual(before);
        _panes = panes;
        ActiveIndex = activeIndex;
        return changed;
    }
}
