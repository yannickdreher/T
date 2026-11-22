using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using T.Models;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

/// <summary>
/// Shows the sessions of the terminal layout: one, or two to four panes with splitters between
/// them. The views come from the app's <see cref="SessionViewCache"/>, so a terminal keeps its
/// state when it moves to another pane or is hidden. Which session is in which pane is decided
/// by the view model (<see cref="SessionsTreeViewModel.Panes"/>); this control only arranges them.
/// </summary>
public class SessionPanesView : UserControl
{
    // Radius of the session surface (ControlCornerRadius).
    private const double OuterRadius = 4;

    // Width of the area around a dividing line that resizes the panes.
    private const double HandleSize = 7;

    public static readonly StyledProperty<IReadOnlyList<SessionViewModel?>?> PanesProperty =
        AvaloniaProperty.Register<SessionPanesView, IReadOnlyList<SessionViewModel?>?>(nameof(Panes));

    public static readonly StyledProperty<TerminalLayout> LayoutProperty =
        AvaloniaProperty.Register<SessionPanesView, TerminalLayout>(nameof(Layout));

    public static readonly StyledProperty<SessionViewModel?> ActiveSessionProperty =
        AvaloniaProperty.Register<SessionPanesView, SessionViewModel?>(nameof(ActiveSession));

    public static readonly StyledProperty<ICommand?> ActivateCommandProperty =
        AvaloniaProperty.Register<SessionPanesView, ICommand?>(nameof(ActivateCommand));

    private readonly Grid _grid = new();
    private readonly List<Border> _hosts = [];
    private TerminalLayout? _arrangedLayout;

    public SessionPanesView()
    {
        Content = _grid;
    }

    /// <summary>The session of each pane; null is an empty pane.</summary>
    public IReadOnlyList<SessionViewModel?>? Panes
    {
        get => GetValue(PanesProperty);
        set => SetValue(PanesProperty, value);
    }

    public TerminalLayout Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>The session that has the keyboard input; its pane is marked in a split layout.</summary>
    public SessionViewModel? ActiveSession
    {
        get => GetValue(ActiveSessionProperty);
        set => SetValue(ActiveSessionProperty, value);
    }

    /// <summary>Makes the session of a pane the active one (parameter: the session) when the pane is clicked.</summary>
    public ICommand? ActivateCommand
    {
        get => GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PanesProperty || change.Property == LayoutProperty)
            Arrange();
        else if (change.Property == ActiveSessionProperty)
            MarkActivePane();
    }

    private void Arrange()
    {
        var panes = Panes ?? [];
        // Layout and panes change one after the other; wait until they belong together.
        if (panes.Count != Layout.PaneCount()) return;

        // A view can only have one parent: take all out before they are placed again.
        foreach (var host in _hosts)
            host.Child = null;

        // Only the content changed: the hosts stay, and with them the sizes set with the splitters.
        if (_arrangedLayout != Layout)
            BuildGrid();

        for (int i = 0; i < _hosts.Count; i++)
            _hosts[i].Child = panes[i] is { } session ? ViewFor(session) : EmptyPane();
        MarkActivePane();
    }

    private void BuildGrid()
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        _hosts.Clear();
        _arrangedLayout = Layout;

        bool columns = Layout is TerminalLayout.SideBySide or TerminalLayout.Grid;
        bool rows = Layout is TerminalLayout.Stacked or TerminalLayout.Grid;
        bool split = columns || rows;

        // The top line of the panes lies on the line under the tab strip instead of below it.
        Margin = split ? new Thickness(0, -1, 0, 0) : default;

        if (columns)
        {
            _grid.ColumnDefinitions = new ColumnDefinitions("*,*");
            foreach (var column in _grid.ColumnDefinitions)
                column.MinWidth = 160;
        }

        if (rows)
        {
            _grid.RowDefinitions = new RowDefinitions("*,*");
            foreach (var row in _grid.RowDefinitions)
                row.MinHeight = 100;
        }

        // Reading order: left to right, then top to bottom.
        int lastRow = rows ? 1 : 0, lastColumn = columns ? 1 : 0;
        for (int row = 0; row <= lastRow; row++)
        {
            for (int column = 0; column <= lastColumn; column++)
            {
                var host = new Border();
                if (split)
                {
                    // Framed panes that overlap by their frame: the shared line is the divider, and
                    // the frame of the active pane (on top) is complete. Outer corners follow the
                    // session surface; the top left one is square because the first tab sits on it.
                    host.Classes.Add("pane");
                    host.Margin = new Thickness(column > 0 ? -1 : 0, row > 0 ? -1 : 0, 0, 0);
                    host.CornerRadius = new CornerRadius(
                        0,
                        row == 0 && column == lastColumn ? OuterRadius : 0,
                        row == lastRow && column == lastColumn ? OuterRadius : 0,
                        row == lastRow && column == 0 ? OuterRadius : 0);
                }

                Grid.SetRow(host, row);
                Grid.SetColumn(host, column);
                host.AddHandler(PointerPressedEvent, OnPanePointerPressed, RoutingStrategies.Tunnel);
                _hosts.Add(host);
                _grid.Children.Add(host);
            }
        }

        if (columns)
            _grid.Children.Add(Splitter(GridResizeDirection.Columns, spanBoth: rows));
        if (rows)
            _grid.Children.Add(Splitter(GridResizeDirection.Rows, spanBoth: columns));
    }

    /// <summary>
    /// Invisible handle on the dividing line. It is wider than the line, so it can be grabbed, and
    /// takes no space of its own.
    /// </summary>
    private static GridSplitter Splitter(GridResizeDirection direction, bool spanBoth)
    {
        var splitter = new GridSplitter
        {
            ResizeDirection = direction,
            ResizeBehavior = GridResizeBehavior.PreviousAndCurrent,
            Background = Brushes.Transparent,
            MinWidth = 0,
            MinHeight = 0,
            ZIndex = 2
        };

        // Centred on the line, which is the first pixel of the second column or row.
        double offset = -(HandleSize - 1) / 2;
        if (direction == GridResizeDirection.Columns)
        {
            Grid.SetColumn(splitter, 1);
            if (spanBoth) Grid.SetRowSpan(splitter, 2);
            splitter.Width = HandleSize;
            splitter.HorizontalAlignment = HorizontalAlignment.Left;
            splitter.Margin = new Thickness(offset - 1, 0, 0, 0);
        }
        else
        {
            Grid.SetRow(splitter, 1);
            if (spanBoth) Grid.SetColumnSpan(splitter, 2);
            splitter.Height = HandleSize;
            splitter.VerticalAlignment = VerticalAlignment.Top;
            splitter.Margin = new Thickness(0, offset - 1, 0, 0);
        }

        return splitter;
    }

    /// <summary>Marks the pane of the active session (frame in the accent colour, see Border.pane).</summary>
    private void MarkActivePane()
    {
        var panes = Panes;
        for (int i = 0; i < _hosts.Count; i++)
        {
            bool active = panes != null && i < panes.Count && panes[i] is { } session && session == ActiveSession;
            _hosts[i].Classes.Set("activePane", active);
            _hosts[i].ZIndex = active ? 1 : 0;
        }
    }

    /// <summary>A click anywhere in a pane selects its session (and with it the tab) without swallowing the click.</summary>
    private void OnPanePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        int index = sender is Border host ? _hosts.IndexOf(host) : -1;
        if (index < 0 || Panes is not { } panes || index >= panes.Count || panes[index] is not { } session) return;
        if (!session.IsActive && ActivateCommand?.CanExecute(session) == true)
            ActivateCommand.Execute(session);
    }

    private static Control? ViewFor(SessionViewModel session) =>
        Application.Current?.DataTemplates.OfType<SessionViewCache>().FirstOrDefault()?.Build(session);

    private static TextBlock EmptyPane()
    {
        var hint = new TextBlock
        {
            Text = "Open another terminal to show it in this pane.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16)
        };
        hint.Classes.Add("hint");
        return hint;
    }
}
