using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using T.Abstractions;
using T.Models;
using T.UI.Abstractions;
using T.UI.Services;
using T.UI.Models;
using T.UI.Views.Dialogs;

namespace T.UI.ViewModels;

public partial class SessionsTreeViewModel : ViewModelBase
{
    private readonly ISessionStorageService _storageService;
    private readonly ISshManager _sshManager;
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

    public bool HasOpenSessions => OpenSessions.Count > 0;
    public bool CanEditSelected => SelectedTreeNode != null;
    public bool CanDisconnectSelected => OpenSessions.Any(s => s.Session.Id == SelectedSession?.Id);

    // DI constructor
    public SessionsTreeViewModel(
        ISessionStorageService storageService,
        ISshManager sshManager,
        IWindowProvider windowProvider,
        IServiceProvider serviceProvider)
    {
        _storageService = storageService;
        _sshManager = sshManager;
        _windowProvider = windowProvider;
        _serviceProvider = serviceProvider;

        if (Design.IsDesignMode)
            LoadDesignTimeData();
        else
            _ = LoadAllAsync();
    }

    partial void OnSelectedTreeNodeChanged(ITreeNode? value) => SelectedSession = value is SessionTreeNode n ? n.Session : null;

    partial void OnOpenSessionsChanged(ObservableCollection<SessionViewModel> value) => OnPropertyChanged(nameof(HasOpenSessions));

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

    private async Task LoadAllAsync()
    {
        var sessions = await _storageService.LoadSessionsAsync();
        Sessions = new ObservableCollection<SshSession>(sessions);

        var folders = await _storageService.LoadFoldersAsync();
        Folders = new ObservableCollection<Folder>(folders);

        BuildTree();
    }

    private void BuildTree()
    {
        var folderNodes = Folders.ToDictionary(f => f.Id, FolderTreeNode.FromFolder);
        var rootNodes = new List<ITreeNode>();

        foreach (var folder in Folders)
        {
            var node = folderNodes[folder.Id];
            if (folder.ParentId != null && folderNodes.TryGetValue(folder.ParentId, out var parent))
                parent.Children.Add(node);
            else
                rootNodes.Add(node);
        }

        foreach (var session in Sessions)
        {
            var hostNode = SessionTreeNode.FromSession(session);
            if (session.FolderId != null && folderNodes.TryGetValue(session.FolderId, out var folderNode))
                folderNode.Children.Add(hostNode);
            else
                rootNodes.Add(hostNode);
        }

        TreeNodes = new ObservableCollection<ITreeNode>(rootNodes);
        foreach (var node in TreeNodes)
            HookFolderExpansion(node);
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

        node.Folder.IsExpanded = node.IsExpanded;
        await _storageService.UpdateFolderAsync(node.Folder);
    }

    [RelayCommand] private async Task ExpandAllFoldersAsync() => await SetAllFoldersExpandedAsync(true);
    [RelayCommand] private async Task CollapseAllFoldersAsync() => await SetAllFoldersExpandedAsync(false);

    private async Task SetAllFoldersExpandedAsync(bool expanded)
    {
        foreach (var node in EnumerateFolderNodes(TreeNodes))
        {
            node.IsExpanded = expanded;
            if (node.Folder != null)
            {
                node.Folder.IsExpanded = expanded;
                await _storageService.UpdateFolderAsync(node.Folder);
            }
        }
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

        var dialogContent = _serviceProvider.GetRequiredService<SessionEditorDialog>();
        dialogContent.DataContext = session;

        var dialog = new ContentDialog
        {
            Title = "Session", PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = dialogContent
        };

        if (await dialog.ShowAsync(Host) == ContentDialogResult.Primary)
        {
            Sessions.Add(session);
            await _storageService.AddSessionAsync(session);
            BuildTree();
        }
    }

    [RelayCommand]
    private async Task EditSessionAsync()
    {
        if (Host is null || SelectedSession is null) return;

        var copy = new SshSession
        {
            Id = SelectedSession.Id, Name = SelectedSession.Name, Host = SelectedSession.Host,
            Port = SelectedSession.Port, Username = SelectedSession.Username,
            Password = SelectedSession.Password, PrivateKeyPath = SelectedSession.PrivateKeyPath,
            PrivateKeyPassword = SelectedSession.PrivateKeyPassword,
            FolderId = SelectedSession.FolderId, Description = SelectedSession.Description
        };

        var dialogContent = _serviceProvider.GetRequiredService<SessionEditorDialog>();
        dialogContent.DataContext = copy;

        var dialog = new ContentDialog
        {
            Title = "Session", PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = dialogContent
        };

        if (await dialog.ShowAsync(Host) == ContentDialogResult.Primary)
        {
            var index = Sessions.IndexOf(SelectedSession);
            if (index >= 0) Sessions[index] = copy;
            await _storageService.UpdateSessionAsync(copy);
            BuildTree();
        }
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (Host is null) return;

        var folder = new Folder
        {
            ParentId = SelectedTreeNode is FolderTreeNode fn ? fn.Folder.Id : null
        };

        var dialogContent = _serviceProvider.GetRequiredService<FolderEditorDialog>();
        dialogContent.DataContext = folder;

        var dialog = new ContentDialog
        {
            Title = "Folder", PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = dialogContent
        };

        if (await dialog.ShowAsync(Host) == ContentDialogResult.Primary)
        {
            Folders.Add(folder);
            await _storageService.AddFolderAsync(folder);
            BuildTree();
        }
    }

    [RelayCommand]
    private async Task EditFolderAsync()
    {
        if (Host is null || SelectedTreeNode is not FolderTreeNode { Folder: not null } selected) return;

        var dialogContent = _serviceProvider.GetRequiredService<FolderEditorDialog>();
        dialogContent.DataContext = selected.Folder;

        var dialog = new ContentDialog
        {
            Title = "Folder", PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = dialogContent
        };

        if (await dialog.ShowAsync(Host) == ContentDialogResult.Primary)
        {
            await _storageService.UpdateFolderAsync(selected.Folder);
            BuildTree();
        }
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

        if (SelectedTreeNode is FolderTreeNode { Folder: not null } folderNode)
        {
            if (!await DialogService.ConfirmAsync(Host, "Delete Folder",
                    $"Delete folder \"{folderNode.Name}\" and all its contents?")) return;

            var folderId = folderNode.Folder.Id;
            var folderIds = GetAllChildFolderIds(folderId);
            folderIds.Add(folderId);

            foreach (var session in Sessions.Where(s => s.FolderId != null && folderIds.Contains(s.FolderId)).ToList())
                CloseSessionById(session.Id);

            await _storageService.DeleteFolderAsync(folderId);
            await LoadAllAsync();
        }
        else if (SelectedTreeNode is SessionTreeNode { Session: not null } sessionNode)
        {
            if (!await DialogService.ConfirmAsync(Host, "Delete Session",
                    $"Delete session \"{sessionNode.Session.Name}\"?")) return;

            CloseSessionById(sessionNode.Session.Id);

            await _storageService.DeleteSessionAsync(sessionNode.Session.Id);
            Sessions.Remove(sessionNode.Session);
            BuildTree();
        }
    }

    private HashSet<string> GetAllChildFolderIds(string parentId)
    {
        var result = new HashSet<string>();
        foreach (var folder in Folders.Where(f => f.ParentId == parentId))
        {
            result.Add(folder.Id);
            result.UnionWith(GetAllChildFolderIds(folder.Id));
        }
        return result;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedSession is null) return;
        await ConnectToSessionAsync(SelectedSession);
    }

    private async Task ConnectToSessionAsync(SshSession session)
    {
        var existing = OpenSessions.FirstOrDefault(s => s.Session.Id == session.Id);
        if (existing != null) { ActiveSession = existing; return; }

        var sessionVm = ActivatorUtilities.CreateInstance<SessionViewModel>(_serviceProvider, session);
        sessionVm.SessionClosed += OnSessionClosed;

        OpenSessions.Add(sessionVm);
        ActiveSession = sessionVm;
        OnPropertyChanged(nameof(HasOpenSessions));

        await sessionVm.ConnectAsync();
    }

    [RelayCommand]
    private void CloseSession(SessionViewModel? session)
    {
        if (session is null) return;
        session.SessionClosed -= OnSessionClosed;
        session.Disconnect();
        session.Dispose();
        OpenSessions.Remove(session);
        if (ActiveSession == session) ActiveSession = OpenSessions.FirstOrDefault();
        OnPropertyChanged(nameof(HasOpenSessions));
    }

    [RelayCommand]
    private void DisconnectSelected()
    {
        var selected = SelectedSession;
        if (selected is null) return;

        var open = OpenSessions.FirstOrDefault(s => s.Session.Id == selected.Id);
        if (open != null) CloseSession(open);
    }

    private void CloseSessionById(string sessionId)
    {
        var open = OpenSessions.FirstOrDefault(s => s.Session.Id == sessionId);
        if (open != null) CloseSession(open);
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

        BuildTree();
    }

    public static bool IsDescendantOf(ITreeNode node, ITreeNode potentialAncestor)
    {
        if (node == potentialAncestor) return true;
        return potentialAncestor.Children.Any(child => IsDescendantOf(node, child));
    }
}