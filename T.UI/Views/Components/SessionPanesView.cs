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
    // Same distance as between the other panes of the window.
    private const double Gap = 4;

    public static readonly StyledProperty<IReadOnlyList<SessionViewModel?>?> PanesProperty =
        AvaloniaProperty.Register<SessionPanesView, IReadOnlyList<SessionViewModel?>?>(nameof(Panes));

    public static readonly StyledProperty<TerminalLayout> LayoutProperty =
        AvaloniaProperty.Register<SessionPanesView, TerminalLayout>(nameof(Layout));

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

        if (columns)
        {
            _grid.ColumnDefinitions = new ColumnDefinitions($"*,{Gap},*");
            foreach (var column in new[] { _grid.ColumnDefinitions[0], _grid.ColumnDefinitions[2] })
                column.MinWidth = 160;
        }

        if (rows)
        {
            _grid.RowDefinitions = new RowDefinitions($"*,{Gap},*");
            foreach (var row in new[] { _grid.RowDefinitions[0], _grid.RowDefinitions[2] })
                row.MinHeight = 100;
        }

        // Reading order: left to right, then top to bottom.
        for (int row = 0; row < (rows ? 2 : 1); row++)
        {
            for (int column = 0; column < (columns ? 2 : 1); column++)
            {
                var host = new Border();
                Grid.SetRow(host, row * 2);
                Grid.SetColumn(host, column * 2);
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

    private static GridSplitter Splitter(GridResizeDirection direction, bool spanBoth)
    {
        var splitter = new GridSplitter
        {
            ResizeDirection = direction,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = Brushes.Transparent,
            MinWidth = 0,
            MinHeight = 0
        };

        if (direction == GridResizeDirection.Columns)
        {
            Grid.SetColumn(splitter, 1);
            if (spanBoth) Grid.SetRowSpan(splitter, 3);
        }
        else
        {
            Grid.SetRow(splitter, 1);
            if (spanBoth) Grid.SetColumnSpan(splitter, 3);
        }

        return splitter;
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

    private static Border EmptyPane()
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

        var pane = new Border { Child = hint };
        pane.Classes.Add("Panel");
        return pane;
    }
}
