using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using T.Abstractions;
using T.Models;
using T.UI.Abstractions;
using T.UI.Models;
using T.UI.Services;
using T.UI.Views.Dialogs;

namespace T.UI.ViewModels;

public partial class SessionsTreeViewModel : ViewModelBase
{
    private readonly ISessionStorageService _storageService;
    private readonly ISettingsService? _settingsService;
    private readonly IWindowProvider _windowProvider;
    private readonly IServiceProvider _serviceProvider;

    private Window? Host => _windowProvider.MainWindow;

    [ObservableProperty] private ObservableCollection<SshSession> _sessions = [];
    [ObservableProperty] private SshSession? _selectedSession;
    [ObservableProperty] private ObservableCollection<ITreeNode> _treeNodes = [];
    [ObservableProperty] private ITreeNode? _selectedTreeNode;
    [ObservableProperty] private ObservableCollection<Folder> _folders = [];

    [ObservableProperty] private ObservableCollection<SessionViewModel> _openSessions = [];
    [ObservableProperty] private SessionViewModel? _activeSession;

    /// <summary>Filters the tree by session name, host or user name.</summary>
    [ObservableProperty] private string _searchText = "";

    public bool HasOpenSessions => OpenSessions.Count > 0;
    public bool IsEmpty => TreeNodes.Count == 0;
    public string EmptyText => string.IsNullOrWhiteSpace(SearchText)
        ? "No sessions yet.\nCreate one with the + button."
        : "No session matches the search.";

    partial void OnSearchTextChanged(string value) => BuildTree();

    partial void OnTreeNodesChanged(ObservableCollection<ITreeNode> value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    // Design-time constructor
    public SessionsTreeViewModel()
    {
        _storageService = null!;
        _windowProvider = null!;
        _serviceProvider = null!;
        LoadDesignTimeData();
    }

    // DI constructor
    public SessionsTreeViewModel(
        ISessionStorageService storageService,
        ISettingsService settingsService,
        IWindowProvider windowProvider,
        IServiceProvider serviceProvider)
    {
        _storageService = storageService;
        _settingsService = settingsService;
        _windowProvider = windowProvider;
        _serviceProvider = serviceProvider;

        OpenSessions.CollectionChanged += OnOpenSessionsCollectionChanged;

        if (Design.IsDesignMode)
            LoadDesignTimeData();
        else
            _loadTask = LoadAllAsync(showErrors: false);
    }

    private readonly Task _loadTask = Task.CompletedTask;

    // An error of the first load is shown once the main window is open (see RestoreLastSessionsAsync):
    // the load starts while the window is still being built, so there is no visible window for a dialog yet.
    private Exception? _initialLoadError;

    partial void OnSelectedTreeNodeChanged(ITreeNode? value) => SelectedSession = value is SessionTreeNode n ? n.Session : null;

    partial void OnOpenSessionsChanged(ObservableCollection<SessionViewModel>? oldValue, ObservableCollection<SessionViewModel> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= OnOpenSessionsCollectionChanged;
        newValue.CollectionChanged += OnOpenSessionsCollectionChanged;
        OnPropertyChanged(nameof(HasOpenSessions));
        UpdateOpenTabCounts();
    }

    private void OnOpenSessionsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateOpenTabCounts();

    /// <summary>The tree shows how many tabs of each saved session are open.</summary>
    private void UpdateOpenTabCounts()
    {
        var counts = OpenSessions.GroupBy(s => s.Session.Id).ToDictionary(g => g.Key, g => g.Count());
        foreach (var node in EnumerateSessionNodes(TreeNodes))
            node.OpenTabs = counts.GetValueOrDefault(node.Session.Id);
    }

    private static IEnumerable<SessionTreeNode> EnumerateSessionNodes(IEnumerable<ITreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is SessionTreeNode session)
                yield return session;
            foreach (var child in EnumerateSessionNodes(node.Children))
                yield return child;
        }
    }

    partial void OnActiveSessionChanged(SessionViewModel? value)
    {
        foreach (var session in OpenSessions)
            session.IsActive = session == value;
    }

    private void LoadDesignTimeData()
    {
        var prodFolder = new Folder { Name = "Production", IsExpanded = true };
        var devFolder = new Folder { Name = "Development", IsExpanded = true };
        var devSubFolder = new Folder { Name = "CI/CD", ParentId = devFolder.Id };

        Folders = [prodFolder, devFolder, devSubFolder];

        Sessions =
        [
            new SshSession { Name = "Production Server", Host = "prod.example.com", Username = "admin", FolderId = prodFolder.Id },
            new SshSession { Name = "Backup Server", Host = "backup.internal", Username = "root", FolderId = prodFolder.Id },
            new SshSession { Name = "Development", Host = "dev.example.com", Username = "developer", FolderId = devFolder.Id },
            new SshSession { Name = "Staging", Host = "staging.example.com", Username = "deploy", FolderId = devSubFolder.Id },
        ];

        SelectedSession = Sessions[0];

        var designSession = new SessionViewModel(Sessions[0]);
        OpenSessions = [designSession];
        ActiveSession = designSession;

        BuildTree();
    }

    /// <summary>
    /// Reopens the sessions of the last run (setting "ReconnectOnStartup"). Called once the
    /// main window is shown, because connecting may need dialogs (host key, login).
    /// </summary>
    public async Task RestoreLastSessionsAsync()
    {
        await _loadTask;
        if (_initialLoadError is { } error)
        {
            _initialLoadError = null;
            await ShowErrorAsync("Loading sessions failed", error.Message);
        }

        var general = _settingsService?.Current.General;
        if (general is { ReconnectOnStartup: true })
        {
            foreach (var id in general.LastOpenSessionIds.ToList())
            {
                var session = Sessions.FirstOrDefault(s => s.Id == id);
                if (session != null)
                    await ConnectToSessionAsync(session);
            }
        }
    }

    /// <summary>Remembers the open tabs so they can be restored on the next start.</summary>
    public void RememberOpenSessions()
    {
        if (_settingsService is null) return;
        // One entry per tab: a session that was open twice is reopened twice.
        _settingsService.Current.General.LastOpenSessionIds = OpenSessions.Select(s => s.Session.Id).ToList();
        _settingsService.Save();
    }

    private async Task LoadAllAsync(bool showErrors = true)
    {
        try
        {
            Sessions = new ObservableCollection<SshSession>(await _storageService.LoadSessionsAsync());
            Folders = new ObservableCollection<Folder>(await _storageService.LoadFoldersAsync());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionsTree] Loading sessions failed: {ex}");
            if (showErrors)
                await ShowErrorAsync("Loading sessions failed", ex.Message);
            else
                _initialLoadError = ex;
        }

        // Open tabs keep their SshSession instance: reuse it so edits in the tree reach them.
        foreach (var open in OpenSessions)
        {
            var index = Sessions.ToList().FindIndex(s => s.Id == open.Session.Id);
            if (index >= 0) Sessions[index] = open.Session;
        }

        BuildTree();
    }

    private void BuildTree()
    {
        var filter = SearchText?.Trim() ?? "";
        var filtering = filter.Length > 0;

        bool Matches(SshSession s) =>
            s.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            s.Host.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            s.Username.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var folderNodes = Folders.ToDictionary(f => f.Id, f =>
        {
            var node = FolderTreeNode.FromFolder(f);
            if (filtering) node.IsExpanded = true; // show every match (not persisted)
            return node;
        });
        var rootNodes = new List<ITreeNode>();

        foreach (var folder in Folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = folderNodes[folder.Id];
            if (folder.ParentId != null && folderNodes.TryGetValue(folder.ParentId, out var parent))
                parent.Children.Add(node);
            else
                rootNodes.Add(node);
        }

        foreach (var session in Sessions.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (filtering && !Matches(session))
                continue;

            var hostNode = SessionTreeNode.FromSession(session);
            if (session.FolderId != null && folderNodes.TryGetValue(session.FolderId, out var folderNode))
                folderNode.Children.Add(hostNode);
            else
                rootNodes.Add(hostNode);
        }

        if (filtering)
            PruneEmptyFolders(rootNodes);

        TreeNodes = new ObservableCollection<ITreeNode>(rootNodes);
        foreach (var node in TreeNodes)
            HookFolderExpansion(node);
        UpdateOpenTabCounts();
    }

    /// <summary>Removes folders without matching sessions (while searching).</summary>
    private static void PruneEmptyFolders(IList<ITreeNode> nodes)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i] is not FolderTreeNode folder) continue;
            PruneEmptyFolders(folder.Children);
            if (folder.Children.Count == 0) nodes.RemoveAt(i);
        }
    }

    private void HookFolderExpansion(ITreeNode node)
    {
        if (node is FolderTreeNode fn)
        {
            fn.PropertyChanged -= OnTreeNodePropertyChanged;
            fn.PropertyChanged += OnTreeNodePropertyChanged;
        }
        foreach (var child in node.Children)
            HookFolderExpansion(child);
    }

    private async void OnTreeNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ITreeNode.IsExpanded)) return;
        if (sender is not FolderTreeNode { Folder: not null } node) return;
        if (node.Folder.IsExpanded == node.IsExpanded) return;

        node.Folder.IsExpanded = node.IsExpanded;
        try
        {
            await _storageService.UpdateFolderAsync(node.Folder);
        }
        catch (Exception ex)
        {
            // async void: never let a storage error crash the app.
            Debug.WriteLine($"[SessionsTree] Saving folder state failed: {ex.Message}");
        }
    }

    [RelayCommand] private async Task ExpandAllFoldersAsync() => await SetAllFoldersExpandedAsync(true);
    [RelayCommand] private async Task CollapseAllFoldersAsync() => await SetAllFoldersExpandedAsync(false);

    private async Task SetAllFoldersExpandedAsync(bool expanded)
    {
        foreach (var node in EnumerateFolderNodes(TreeNodes))
            node.IsExpanded = expanded; // persisted by OnTreeNodePropertyChanged
        await Task.CompletedTask;
    }

    private static IEnumerable<FolderTreeNode> EnumerateFolderNodes(IEnumerable<ITreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is FolderTreeNode fn)
            {
                yield return fn;
                foreach (var child in EnumerateFolderNodes(fn.Children))
                    yield return child;
            }
        }
    }

    // ── Sessions ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (Host is null) return;

        var session = new SshSession
        {
            FolderId = SelectedTreeNode switch
            {
                FolderTreeNode fn => fn.Folder.Id,
                SessionTreeNode sn => sn.Session.FolderId,
                _ => null
            }
        };

        if (!await ShowSessionEditorAsync(session, Sessions))
            return;

        try
        {
            await _storageService.AddSessionAsync(session);
            Sessions.Add(session);
            BuildTree();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Saving the session failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task EditSessionAsync()
    {
        var original = SelectedSession;
        if (Host is null || original is null) return;

        // Edit a copy so "Cancel" really discards the changes.
        var copy = original.Clone();
        if (!await ShowSessionEditorAsync(copy, Sessions.Where(s => s.Id != copy.Id)))
            return;

        try
        {
            await _storageService.UpdateSessionAsync(copy);
            // Apply to the shared instance: open tabs and the tree keep pointing at it.
            original.CopyFrom(copy);
            BuildTree();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Saving the session failed", ex.Message);
        }
    }

    /// <summary>Shows the session editor; returns true when the user saved valid input.</summary>
    private async Task<bool> ShowSessionEditorAsync(SshSession session, IEnumerable<SshSession> proxyCandidates)
    {
        var dialogContent = _serviceProvider.GetRequiredService<SessionEditorDialog>();
        dialogContent.DataContext = session;

        // Sessions that (transitively) jump through this one would create a loop.
        dialogContent.AddProxySessions(proxyCandidates.Where(s => !JumpsThrough(s, session.Id)));
        dialogContent.AddStepProfiles(_settingsService?.Current.Step.Profiles ?? []);

        var dialog = new FAContentDialog
        {
            Title = "Session",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
            Content = dialogContent
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var error = ValidateSession(session);
            dialogContent.ValidationError = error;
            args.Cancel = error != null;
        };

        if (await dialog.ShowAsync(Host!) != FAContentDialogResult.Primary)
            return false;

        session.ProxyJumpSessionId = dialogContent.SelectedProxySessionId;
        session.StepProfileId = dialogContent.SelectedStepProfileId;
        session.Host = session.Host.Trim();
        session.Username = session.Username.Trim();
        if (string.IsNullOrWhiteSpace(session.Name))
            session.Name = session.Host;
        return true;
    }

    private static string? ValidateSession(SshSession session)
    {
        var host = session.Host?.Trim() ?? "";
        if (host.Length == 0)
            return "Please enter a host name or IP address.";
        if (host.Any(char.IsWhiteSpace) || host.Contains('@'))
            return "The host must not contain spaces or '@' (enter the user name separately).";
        if (session.Port is < 1 or > 65535)
            return "The port must be between 1 and 65535.";
        return null;
    }

    /// <summary>True when <paramref name="session"/> uses <paramref name="targetId"/> somewhere in its jump host chain.</summary>
    private bool JumpsThrough(SshSession session, string targetId)
    {
        var visited = new HashSet<string>();
        var current = session;
        while (current?.ProxyJumpSessionId is { } next && visited.Add(current.Id))
        {
            if (next == targetId) return true;
            current = Sessions.FirstOrDefault(s => s.Id == next);
        }
        return false;
    }

    // ── Folders ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (Host is null) return;

        var folder = new Folder
        {
            ParentId = SelectedTreeNode is FolderTreeNode fn ? fn.Folder.Id : null
        };

        if (!await ShowFolderEditorAsync(folder))
            return;

        try
        {
            await _storageService.AddFolderAsync(folder);
            Folders.Add(folder);
            BuildTree();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Saving the folder failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task EditFolderAsync()
    {
        if (Host is null || SelectedTreeNode is not FolderTreeNode { Folder: not null } selected) return;

        var copy = selected.Folder.Clone();
        if (!await ShowFolderEditorAsync(copy))
            return;

        try
        {
            await _storageService.UpdateFolderAsync(copy);
            selected.Folder.Name = copy.Name;
            BuildTree();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Saving the folder failed", ex.Message);
        }
    }

    private async Task<bool> ShowFolderEditorAsync(Folder folder)
    {
        var dialogContent = _serviceProvider.GetRequiredService<FolderEditorDialog>();
        dialogContent.DataContext = folder;

        var dialog = new FAContentDialog
        {
            Title = "Folder",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
            Content = dialogContent
        };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = string.IsNullOrWhiteSpace(folder.Name);

        if (await dialog.ShowAsync(Host!) != FAContentDialogResult.Primary)
            return false;

        folder.Name = folder.Name.Trim();
        return true;
    }

    [RelayCommand]
    private async Task EditSelectedAsync()
    {
        if (SelectedTreeNode is FolderTreeNode) await EditFolderAsync();
        else if (SelectedTreeNode is SessionTreeNode) await EditSessionAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (Host is null || SelectedTreeNode is null) return;

        try
        {
            if (SelectedTreeNode is FolderTreeNode { Folder: not null } folderNode)
            {
                var folderIds = GetAllChildFolderIds(folderNode.Folder.Id);
                folderIds.Add(folderNode.Folder.Id);
                var contained = Sessions.Where(s => s.FolderId != null && folderIds.Contains(s.FolderId)).ToList();

                var message = contained.Count == 0
                    ? $"Delete folder \"{folderNode.Name}\"?"
                    : $"Delete folder \"{folderNode.Name}\" including {contained.Count} session(s)?";
                if (!await DialogService.ConfirmAsync(Host, "Delete Folder", message)) return;

                foreach (var session in contained)
                    CloseSessionById(session.Id);

                await _storageService.DeleteFolderAsync(folderNode.Folder.Id);
                await LoadAllAsync();
            }
            else if (SelectedTreeNode is SessionTreeNode { Session: not null } sessionNode)
            {
                var session = sessionNode.Session;
                var dependents = Sessions.Count(s => s.ProxyJumpSessionId == session.Id);
                var message = dependents == 0
                    ? $"Delete session \"{session.Name}\"?"
                    : $"Delete session \"{session.Name}\"? It is used as jump host by {dependents} other session(s), which will then connect directly.";
                if (!await DialogService.ConfirmAsync(Host, "Delete Session", message)) return;

                CloseSessionById(session.Id);
                await _storageService.DeleteSessionAsync(session.Id);

                foreach (var dependent in Sessions.Where(s => s.ProxyJumpSessionId == session.Id))
                    dependent.ProxyJumpSessionId = null;

                Sessions.Remove(session);
                BuildTree();
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Delete failed", ex.Message);
        }
    }

    private HashSet<string> GetAllChildFolderIds(string parentId)
    {
        var result = new HashSet<string>();
        foreach (var folder in Folders.Where(f => f.ParentId == parentId))
        {
            if (!result.Add(folder.Id)) continue;
            result.UnionWith(GetAllChildFolderIds(folder.Id));
        }
        return result;
    }

    // ── Open sessions (tabs) ─────────────────────────────────────────────

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedSession is null) return;
        await ConnectToSessionAsync(SelectedSession);
    }

    /// <summary>Opens (or activates) the tab of a saved session - used by the "+" menu of the tab strip.</summary>
    [RelayCommand]
    private Task ConnectTo(SshSession? session) => session is null ? Task.CompletedTask : ConnectToSessionAsync(session);

    /// <summary>Opens a new tab with its own connection - also when the session is already open in another tab.</summary>
    private async Task ConnectToSessionAsync(SshSession session)
    {
        var sessionVm = ActivatorUtilities.CreateInstance<SessionViewModel>(_serviceProvider, session);
        sessionVm.SessionClosed += OnSessionClosed;

        // "Server", "Server (2)", ...: the lowest number not used by another open tab of this session.
        var used = OpenSessions.Where(s => s.Session.Id == session.Id).Select(s => s.InstanceNumber).ToHashSet();
        int number = 1;
        while (used.Contains(number)) number++;
        sessionVm.InstanceNumber = number;

        OpenSessions.Add(sessionVm);
        ActiveSession = sessionVm;
        OnPropertyChanged(nameof(HasOpenSessions));

        await sessionVm.ConnectAsync();
    }

    [RelayCommand]
    private void CloseSession(SessionViewModel? session)
    {
        var index = session is null ? -1 : OpenSessions.IndexOf(session);
        if (index < 0) return;

        session!.SessionClosed -= OnSessionClosed;
        OpenSessions.RemoveAt(index);
        // Like a browser: the neighbor to the right (or the new last tab) becomes active.
        if (ActiveSession == session)
            ActiveSession = OpenSessions.Count == 0 ? null : OpenSessions[Math.Min(index, OpenSessions.Count - 1)];
        OnPropertyChanged(nameof(HasOpenSessions));

        session.Dispose();
    }

    /// <summary>Opens the session of <paramref name="session"/> once more in a new tab.</summary>
    [RelayCommand]
    private Task DuplicateSession(SessionViewModel? session) =>
        session is null ? Task.CompletedTask : ConnectToSessionAsync(session.Session);

    [RelayCommand]
    private void CloseOtherSessions(SessionViewModel? keep)
    {
        if (keep is null) return;
        foreach (var session in OpenSessions.Where(s => s != keep).ToList())
            CloseSession(session);
        ActiveSession = keep;
    }

    [RelayCommand]
    private void CloseSessionsToTheRight(SessionViewModel? session)
    {
        var index = session is null ? -1 : OpenSessions.IndexOf(session);
        if (index < 0) return;
        foreach (var right in OpenSessions.Skip(index + 1).ToList())
            CloseSession(right);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: activates the next (+1) or previous (-1) tab, wrapping around.</summary>
    public void ActivateAdjacentSession(int direction)
    {
        if (OpenSessions.Count < 2) return;
        var index = ActiveSession is null ? 0 : OpenSessions.IndexOf(ActiveSession);
        ActiveSession = OpenSessions[(((index + direction) % OpenSessions.Count) + OpenSessions.Count) % OpenSessions.Count];
    }

    [RelayCommand]
    private void DisconnectSelected()
    {
        var selected = SelectedSession;
        if (selected is null) return;
        CloseSessionById(selected.Id);
    }

    /// <summary>Closes all tabs (used on application shutdown).</summary>
    public void CloseAllSessions()
    {
        foreach (var session in OpenSessions.ToList())
            CloseSession(session);
    }

    /// <summary>Closes every tab of a saved session (it was deleted, or "Disconnect" in the tree).</summary>
    private void CloseSessionById(string sessionId)
    {
        foreach (var open in OpenSessions.Where(s => s.Session.Id == sessionId).ToList())
            CloseSession(open);
    }

    private void OnSessionClosed(SessionViewModel session) => CloseSession(session);

    [RelayCommand]
    private async Task MoveNodeAsync(object? parameter)
    {
        if (parameter is not (ITreeNode dragged, ITreeNode target)) return;
        if (dragged is FolderTreeNode && IsDescendantOf(target, dragged)) return;

        var targetFolderId = target switch
        {
            FolderTreeNode fn => fn.Folder?.Id,
            SessionTreeNode sn => sn.Session?.FolderId,
            _ => null
        };

        try
        {
            if (dragged is FolderTreeNode { Folder: not null } df)
            {
                df.Folder.ParentId = targetFolderId;
                await _storageService.UpdateFolderAsync(df.Folder);
            }
            else if (dragged is SessionTreeNode { Session: not null } ds)
            {
                ds.Session.FolderId = targetFolderId;
                await _storageService.UpdateSessionAsync(ds.Session);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Moving failed", ex.Message);
        }

        BuildTree();
    }

    public static bool IsDescendantOf(ITreeNode node, ITreeNode potentialAncestor)
    {
        if (node == potentialAncestor) return true;
        return potentialAncestor.Children.Any(child => IsDescendantOf(node, child));
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        if (Host is not null)
            await DialogService.ShowMessageAsync(Host, title, message);
    }
}
