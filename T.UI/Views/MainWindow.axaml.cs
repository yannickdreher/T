using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using T.Abstractions;
using T.Models;
using T.UI.Services;
using T.UI.ViewModels;

namespace T.UI.Views;

public partial class MainWindow : FAAppWindow
{
    private readonly ISettingsService? _settingsService;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.Height = 32;

        SessionTabs.TabCloseRequested += OnTabCloseRequested;
        SessionTabs.AddTabButtonClick += OnAddTabButtonClick;
        SessionTabs.AddHandler(ContextRequestedEvent, OnTabContextRequested);
        SessionTabs.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        // Tunnel: Ctrl+Tab switches tabs before the terminal sees the key.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    public MainWindow(MainWindowViewModel viewModel, ISettingsService settingsService) : this()
    {
        DataContext = viewModel;
        _settingsService = settingsService;
        Closing += OnClosing;
        Opened += async (_, _) => await viewModel.SessionsTree.RestoreLastSessionsAsync();
    }

    private SessionsTreeViewModel? Sessions => (DataContext as MainWindowViewModel)?.SessionsTree;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || _closeConfirmed)
            return;

        var tree = vm.SessionsTree;
        var connected = tree.OpenSessions.Count(s => s.IsConnected || s.IsReconnecting);

        if (connected > 0 && _settingsService?.Current.General.ConfirmOnClose == true)
        {
            // Closing is async (dialog), so cancel now and close again after confirmation.
            e.Cancel = true;
            if (!await DialogService.ConfirmAsync(this, "Quit",
                    $"{connected} session(s) are still connected. Close all connections and quit?", "Quit"))
                return;
        }

        _closeConfirmed = true;
        tree.RememberOpenSessions();
        tree.CloseAllSessions();

        if (e.Cancel)
            Close();
    }

    // ── Session tabs ─────────────────────────────────────────────────────

    private void OnTabCloseRequested(FATabView sender, FATabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is SessionViewModel session)
            Sessions?.CloseSessionCommand.Execute(session);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control) && Sessions is { } sessions)
        {
            sessions.ActivateAdjacentSession(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
    }

    /// <summary>Middle click on a tab closes it (like browsers).</summary>
    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Middle) return;
        if (TabAt(e.Source)?.DataContext is SessionViewModel session)
        {
            Sessions?.CloseSessionCommand.Execute(session);
            e.Handled = true;
        }
    }

    private void OnTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var tab = TabAt(e.Source);
        if (tab?.DataContext is not SessionViewModel session || Sessions is not { } sessions) return;

        int index = sessions.OpenSessions.IndexOf(session);
        var menu = new ContextMenu
        {
            ItemsSource = new List<Control>
            {
                new MenuItem
                {
                    Header = "Reconnect",
                    Icon = MenuIcon("arrow_clockwise_regular"),
                    Command = session.RetryConnectCommand,
                    IsEnabled = !session.IsConnected && !session.IsConnecting
                },
                new MenuItem
                {
                    Header = "Duplicate tab",
                    Icon = MenuIcon("add_regular"),
                    Command = sessions.DuplicateSessionCommand,
                    CommandParameter = session
                },
                new Separator(),
                new MenuItem { Header = "Close tab", Icon = MenuIcon("dismiss_regular"), Command = sessions.CloseSessionCommand, CommandParameter = session },
                new MenuItem
                {
                    Header = "Close other tabs",
                    Command = sessions.CloseOtherSessionsCommand,
                    CommandParameter = session,
                    IsEnabled = sessions.OpenSessions.Count > 1
                },
                new MenuItem
                {
                    Header = "Close tabs to the right",
                    Command = sessions.CloseSessionsToTheRightCommand,
                    CommandParameter = session,
                    IsEnabled = index >= 0 && index < sessions.OpenSessions.Count - 1
                }
            }
        };

        tab.ContextMenu = menu;
        menu.Open(tab);
        e.Handled = true;
    }

    /// <summary>"+" button: menu of all saved sessions (grouped by folder) to open in a new tab.</summary>
    private void OnAddTabButtonClick(FATabView sender, EventArgs args)
    {
        if (Sessions is not { } sessions) return;

        var items = new List<Control>
        {
            new MenuItem { Header = "New session…", Icon = MenuIcon("plug_connected_add_regular"), Command = sessions.NewSessionCommand },
            new Separator()
        };
        items.AddRange(BuildSessionItems(sessions, parentFolderId: null));
        if (sessions.Sessions.Count == 0)
            items.Add(new MenuItem { Header = "No saved sessions", IsEnabled = false });

        var flyout = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
        var anchor = sender.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "AddButton") ?? (Control)sender;
        flyout.ShowAt(anchor);
    }

    private static IEnumerable<Control> BuildSessionItems(SessionsTreeViewModel sessions, string? parentFolderId)
    {
        foreach (var folder in sessions.Folders.Where(f => f.ParentId == parentFolderId).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var children = BuildSessionItems(sessions, folder.Id).ToList();
            if (children.Count == 0) continue;
            yield return new MenuItem { Header = folder.Name, Icon = FolderIcon(), ItemsSource = children };
        }

        foreach (var session in sessions.Sessions.Where(s => s.FolderId == parentFolderId).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            yield return SessionItem(sessions, session);
    }

    private static MenuItem SessionItem(SessionsTreeViewModel sessions, SshSession session)
    {
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(new TextBlock { Text = session.Name });
        var address = new TextBlock { Text = session.Address, Opacity = 0.6 };
        header.Children.Add(address);

        return new MenuItem
        {
            Header = header,
            Icon = MenuIcon("window_console_regular"),
            Command = sessions.ConnectToCommand,
            CommandParameter = session
        };
    }

    private static FATabViewItem? TabAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<FATabViewItem>(includeSelf: true);

    private static PathIcon? MenuIcon(string key) =>
        Application.Current?.TryGetResource(key, null, out var geometry) == true && geometry is Geometry g
            ? new PathIcon { Data = g, Width = 14, Height = 14 }
            : null;

    private static Image FolderIcon() => new() { Source = FileIcons.GetFolder(16), Width = 16, Height = 16 };
}
