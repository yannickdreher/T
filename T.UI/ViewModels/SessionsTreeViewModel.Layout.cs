using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using T.Models;

namespace T.UI.ViewModels;

/// <summary>Arrangement of the open terminals: one, or several panes next to and above each other.</summary>
public partial class SessionsTreeViewModel
{
    private readonly PaneSet<SessionViewModel> _paneSet = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplit), nameof(IsSingleLayout), nameof(IsSideBySideLayout), nameof(IsStackedLayout), nameof(IsGridLayout))]
    private TerminalLayout _layout;

    /// <summary>The session of each pane (null: empty pane), in reading order.</summary>
    [ObservableProperty] private IReadOnlyList<SessionViewModel?> _panes = [null];

    /// <summary>False while another workspace (the tunnels) covers the terminals: nothing is on screen then.</summary>
    [ObservableProperty] private bool _terminalsShown = true;

    public bool IsSplit => Layout != TerminalLayout.One;

    // For the check marks of the layout menu.
    public bool IsSingleLayout => Layout == TerminalLayout.One;
    public bool IsSideBySideLayout => Layout == TerminalLayout.SideBySide;
    public bool IsStackedLayout => Layout == TerminalLayout.Stacked;
    public bool IsGridLayout => Layout == TerminalLayout.Grid;

    [RelayCommand]
    private void SetLayout(TerminalLayout layout) => Layout = layout;

    /// <summary>Selects an open session, for example when its pane is clicked.</summary>
    [RelayCommand]
    private void ActivateSession(SessionViewModel? session)
    {
        if (session != null && OpenSessions.Contains(session))
            ActiveSession = session;
    }

    partial void OnLayoutChanged(TerminalLayout value) => UpdatePanes();

    partial void OnTerminalsShownChanged(bool value) => UpdatePanes();

    /// <summary>Assigns the open sessions to the panes and tells every session whether it is on screen.</summary>
    private void UpdatePanes()
    {
        int count = Layout.PaneCount();
        bool changed = _paneSet.Update(count, OpenSessions, ActiveSession) || Panes.Count != count;

        foreach (var session in OpenSessions)
        {
            session.IsSplitPane = IsSplit;
            session.IsVisibleInLayout = TerminalsShown && _paneSet.Panes.Contains(session);
        }

        if (changed)
            Panes = _paneSet.Panes.ToArray();
    }
}
