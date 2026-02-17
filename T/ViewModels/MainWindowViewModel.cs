using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using T.Models;
using T.Services;
using T.Views;
using T.Views.Dialogs;
using Velopack;
using Velopack.Sources;

namespace T.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISessionStorageService _storageService;
    private readonly UpdateManager? _updateManager;
    private Window? _host;

    [ObservableProperty] private ObservableCollection<SshSession> _sessions = [];
    [ObservableProperty] private SshSession? _selectedSession;

    // Tree
    [ObservableProperty] private ObservableCollection<TreeNode> _treeNodes = [];
    [ObservableProperty] private TreeNode? _selectedTreeNode;
    [ObservableProperty] private ObservableCollection<Folder> _folders = [];

    // Active session tabs
    [ObservableProperty] private ObservableCollection<SessionViewModel> _openSessions = [];
    [ObservableProperty] private SessionViewModel? _activeSession;

    public bool HasOpenSessions => OpenSessions.Count > 0;

    public static AppSettings Settings => SettingsService.Current;

    public MainWindowViewModel() : this(SqliteSessionStorageService.Current)
    {
    }

    public MainWindowViewModel(ISessionStorageService storageService)
    {
        _storageService = storageService;

        // Velopack Update Manager initialisieren
        _updateManager = new UpdateManager(
            new GithubSource("https://github.com/yannickdreher/T", null, false));
        
        SettingsService.Load();
        OnPropertyChanged(nameof(Settings));

        if (Design.IsDesignMode)
            LoadDesignTimeData();
        else
            _ = LoadAllAsync();

        // Automatisch nach Updates suchen beim Start
        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// Must be called from MainWindow after construction to enable dialogs.
    /// </summary>
    public void SetHost(Window host) => _host = host;

    private void LoadDesignTimeData()
    {
        var prodFolder = new Folder { Name = "Production", IsExpanded = true };
        var devFolder = new Folder { Name = "Development", IsExpanded = true };
        var devSubFolder = new Folder { Name = "CI/CD", ParentId = devFolder.Id, IsExpanded = false };

        Folders = [prodFolder, devFolder, devSubFolder];

        Sessions =
        [
            new SshSession { Name = "Production Server", Host = "prod.example.com", Username = "admin", ConnectionStatus = ConnectionStatus.Connected, FolderId = prodFolder.Id },
            new SshSession { Name = "Backup Server", Host = "backup.internal", Username = "root", ConnectionStatus = ConnectionStatus.Connecting, FolderId = prodFolder.Id },
            new SshSession { Name = "Development", Host = "dev.example.com", Username = "developer", FolderId = devFolder.Id },
            new SshSession { Name = "Staging", Host = "staging.example.com", Username = "deploy", FolderId = devSubFolder.Id },
            new SshSession { Name = "This is an extrem long name of a host", Host = "this.is.an.extrem.long.url.for.a.host.com", Username = "deploy", FolderId = devSubFolder.Id },
        ];

        SelectedSession = Sessions[0];

        var designSession = new SessionViewModel(Sessions[0])
        {
            IsConnected = true,
            CurrentPath = "/home/admin",
            StatusMessage = "Connected to prod.example.com",
            RemoteFiles =
            [
                new RemoteFile { Name = "..", IsDirectory = true, Permissions = "755" },
                new RemoteFile { Name = "etc", IsDirectory = true, Permissions = "755" },
                new RemoteFile { Name = "home", IsDirectory = true, Permissions = "755" },
                new RemoteFile { Name = "config.json", IsDirectory = false, Size = 1024, Permissions = "644" },
            ]
        };

        OpenSessions = [designSession];
        ActiveSession = designSession;

        BuildTree();
    }

    partial void OnOpenSessionsChanged(ObservableCollection<SessionViewModel> value)
    {
        OnPropertyChanged(nameof(HasOpenSessions));
    }

    partial void OnSelectedTreeNodeChanged(TreeNode? value)
    {
        SelectedSession = value?.Session;
    }

    private async Task LoadAllAsync()
    {
        var loadedSessions = await _storageService.LoadSessionsAsync();
        Sessions = new ObservableCollection<SshSession>(loadedSessions);

        var loadedFolders = await _storageService.LoadFoldersAsync();
        Folders = new ObservableCollection<Folder>(loadedFolders);

        BuildTree();
    }

    private void BuildTree()
    {
        var folderNodes = Folders.ToDictionary(f => f.Id, TreeNode.FromFolder);

        var rootNodes = new List<TreeNode>();
        foreach (var folder in Folders)
        {
            var node = folderNodes[folder.Id];
            if (folder.ParentId != null && folderNodes.TryGetValue(folder.ParentId, out var parentNode))
            {
                parentNode.Children.Add(node);
            }
            else
            {
                rootNodes.Add(node);
            }
        }

        foreach (var session in Sessions)
        {
            var hostNode = TreeNode.FromSession(session);
            if (session.FolderId != null && folderNodes.TryGetValue(session.FolderId, out var folderNode))
            {
                folderNode.Children.Add(hostNode);
            }
            else
            {
                rootNodes.Add(hostNode);
            }
        }

        TreeNodes = new ObservableCollection<TreeNode>(rootNodes);

        foreach (var node in TreeNodes)
        {
            HookFolderExpansion(node);
        }
    }

    private void HookFolderExpansion(TreeNode node)
    {
        if (node.IsFolder)
        {
            node.PropertyChanged -= OnTreeNodePropertyChanged;
            node.PropertyChanged += OnTreeNodePropertyChanged;
        }

        foreach (var child in node.Children)
        {
            HookFolderExpansion(child);
        }
    }

    private async void OnTreeNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TreeNode.IsExpanded)) return;
        if (sender is not TreeNode { IsFolder: true, Folder: not null } node) return;

        node.Folder.IsExpanded = node.IsExpanded;
        await _storageService.UpdateFolderAsync(node.Folder);
    }

    [RelayCommand]
    private async Task ExpandAllFoldersAsync() => await SetAllFoldersExpandedAsync(true);

    [RelayCommand]
    private async Task CollapseAllFoldersAsync() => await SetAllFoldersExpandedAsync(false);

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

    private static IEnumerable<TreeNode> EnumerateFolderNodes(IEnumerable<TreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder)
                yield return node;

            foreach (var child in EnumerateFolderNodes(node.Children))
                yield return child;
        }
    }

    // ── Session CRUD ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        if (_host == null) return;

        var session = new SshSession
        {
            FolderId = SelectedTreeNode?.IsFolder == true
                ? SelectedTreeNode.Folder?.Id
                : SelectedTreeNode?.Session?.FolderId
        };

        var content = new SessionEditorDialog { DataContext = session };
        var dialog = new ContentDialog
        {
            Title = "Session",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content
        };

        var result = await dialog.ShowAsync(_host);

        if (result == ContentDialogResult.Primary)
        {
            Sessions.Add(session);
            await _storageService.AddSessionAsync(session);
            BuildTree();
        }
    }

    [RelayCommand]
    private async Task EditSessionAsync()
    {
        if (_host == null || SelectedSession == null) return;

        var copy = new SshSession
        {
            Id = SelectedSession.Id,
            Name = SelectedSession.Name,
            Host = SelectedSession.Host,
            Port = SelectedSession.Port,
            Username = SelectedSession.Username,
            Password = SelectedSession.Password,
            PrivateKeyPath = SelectedSession.PrivateKeyPath,
            PrivateKeyPassword = SelectedSession.PrivateKeyPassword,
            FolderId = SelectedSession.FolderId,
            Description = SelectedSession.Description
        };

        var content = new SessionEditorDialog { DataContext = copy };
        var dialog = new ContentDialog
        {
            Title = "Session",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content
        };

        var result = await dialog.ShowAsync(_host);

        if (result == ContentDialogResult.Primary)
        {
            var index = Sessions.IndexOf(SelectedSession);
            if (index >= 0) Sessions[index] = copy;
            await _storageService.UpdateSessionAsync(copy);
            BuildTree();
        }
    }

    // ── Folder CRUD ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (_host == null) return;

        var folder = new Folder
        {
            ParentId = SelectedTreeNode?.IsFolder == true ? SelectedTreeNode.Folder?.Id : null
        };

        var content = new FolderEditorDialog { DataContext = folder };
        var dialog = new ContentDialog
        {
            Title = "Folder",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content
        };

        var result = await dialog.ShowAsync(_host);

        if (result == ContentDialogResult.Primary)
        {
            Folders.Add(folder);
            await _storageService.AddFolderAsync(folder);
            BuildTree();
        }
    }

    [RelayCommand]
    private async Task EditFolderAsync()
    {
        if (_host == null || SelectedTreeNode is not { IsFolder: true, Folder: not null }) return;

        var folder = SelectedTreeNode.Folder;
        var content = new FolderEditorDialog { DataContext = folder };
        var dialog = new ContentDialog
        {
            Title = "Folder",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content
        };

        var result = await dialog.ShowAsync(_host);

        if (result == ContentDialogResult.Primary)
        {
            await _storageService.UpdateFolderAsync(folder);
            BuildTree();
        }
    }

    // ── Unified Delete ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (_host == null || SelectedTreeNode == null) return;

        if (SelectedTreeNode.IsFolder && SelectedTreeNode.Folder != null)
        {
            var confirmed = await DialogService.ConfirmAsync(
                _host,
                "Delete Folder",
                $"Delete folder \"{SelectedTreeNode.Name}\" and all its contents?");

            if (!confirmed) return;

            var folderId = SelectedTreeNode.Folder.Id;
            var folderIds = GetAllChildFolderIds(folderId);
            folderIds.Add(folderId);

            foreach (var session in Sessions.Where(s => s.FolderId != null && folderIds.Contains(s.FolderId)).ToList())
            {
                var openSession = OpenSessions.FirstOrDefault(s => s.Session.Id == session.Id);
                if (openSession != null) CloseSession(openSession);
            }

            await _storageService.DeleteFolderAsync(folderId);
            await LoadAllAsync();
        }
        else if (SelectedTreeNode.Session is { } session)
        {
            var confirmed = await DialogService.ConfirmAsync(
                _host,
                "Delete Session",
                $"Delete session \"{session.Name}\"?");

            if (!confirmed) return;

            var openSession = OpenSessions.FirstOrDefault(s => s.Session.Id == session.Id);
            if (openSession != null) CloseSession(openSession);

            await _storageService.DeleteSessionAsync(session.Id);
            Sessions.Remove(session);
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

    // ── Connect / Disconnect ────────────────────────────────────────────

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedSession == null) return;

        var existing = OpenSessions.FirstOrDefault(s => s.Session.Id == SelectedSession.Id);
        if (existing != null)
        {
            ActiveSession = existing;
            return;
        }

        var sessionVm = new SessionViewModel(SelectedSession);
        sessionVm.SessionClosed += OnSessionClosed;

        OpenSessions.Add(sessionVm);
        ActiveSession = sessionVm;
        OnPropertyChanged(nameof(HasOpenSessions));

        await sessionVm.ConnectAsync();
    }

    private void OnSessionClosed(SessionViewModel session) => CloseSession(session);

    [RelayCommand]
    private void CloseSession(SessionViewModel? session)
    {
        if (session == null) return;

        session.SessionClosed -= OnSessionClosed;
        session.Disconnect();
        session.Dispose();
        OpenSessions.Remove(session);

        if (ActiveSession == session)
            ActiveSession = OpenSessions.FirstOrDefault();

        OnPropertyChanged(nameof(HasOpenSessions));
    }

    [RelayCommand]
    private void DisconnectSelected()
    {
        if (SelectedSession == null) return;
        var openSession = OpenSessions.FirstOrDefault(s => s.Session.Id == SelectedSession.Id);
        if (openSession != null) CloseSession(openSession);
    }

    public bool CanDisconnectSelected => OpenSessions.Any(s => s.Session.Id == SelectedSession?.Id);

    // ── Settings ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ShowSettingsAsync()
    {
        if (_host == null) return;

        var window = new SettingsWindow
        {
            DataContext = Settings
        };

        var result = await window.ShowDialog<bool>(_host);

        if (result)
        {
            await SettingsService.SaveAsync();
        }
        else
        {
            SettingsService.Load();
            OnPropertyChanged(nameof(Settings));
        }
    }

    // ── Permissions ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ShowPermissionDialogAsync()
    {
        if (_host == null || ActiveSession?.SelectedFile == null) return;

        var vm = PermissionDialogViewModel.FromOctal(ActiveSession.SelectedFile.Permissions);
        var content = new PermissionDialog { DataContext = vm };
        var dialog = new ContentDialog
        {
            Title = "Permissions",
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content
        };

        var result = await dialog.ShowAsync(_host);

        if (result == ContentDialogResult.Primary)
        {
            await ActiveSession.ChangePermissionsAsync(vm.ToPermissions());
        }
    }

    partial void OnActiveSessionChanged(SessionViewModel? value)
    {
        foreach (var session in OpenSessions)
        {
            session.IsActive = session == value;
        }
    }

    // ── Drag & Drop ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task MoveNodeAsync(object? parameter)
    {
        if (parameter is not (TreeNode dragged, TreeNode target)) return;

        if (dragged.IsFolder && target != null && IsDescendantOf(target, dragged))
            return;

        var targetFolderId = target switch
        {
            { IsFolder: true } => target.Folder?.Id,
            { IsFolder: false } => target.Session?.FolderId,
            _ => null
        };

        if (dragged.IsFolder && dragged.Folder != null)
        {
            dragged.Folder.ParentId = targetFolderId;
            await _storageService.UpdateFolderAsync(dragged.Folder);
        }
        else if (dragged.Session != null)
        {
            dragged.Session.FolderId = targetFolderId;
            await _storageService.UpdateSessionAsync(dragged.Session);
        }

        BuildTree();
    }

    public static bool IsDescendantOf(TreeNode node, TreeNode potentialAncestor)
    {
        if (node == potentialAncestor) return true;

        foreach (var child in potentialAncestor.Children)
        {
            if (IsDescendantOf(node, child))
                return true;
        }

        return false;
    }

    [RelayCommand]
    private async Task ShowAboutAsync()
    {
        if (_host == null) return;

        var content = new AboutDialog();
        var dialog = new ContentDialog
        {
            Title = "About",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            Content = content
        };

        await dialog.ShowAsync(_host);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_updateManager == null) return;

        try
        {
            var updateInfo = await _updateManager.CheckForUpdatesAsync();
            if (updateInfo != null)
            {
                // Update verfügbar - herunterladen und installieren
                await _updateManager.DownloadUpdatesAsync(updateInfo);
                _updateManager.ApplyUpdatesAndRestart(updateInfo);
            }
        }
        catch (Exception ex)
        {
            // Update-Fehler behandeln (optional)
            Console.WriteLine($"Update check failed: {ex.Message}");
        }
    }
}
