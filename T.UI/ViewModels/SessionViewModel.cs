using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
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

public partial class SessionViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan AutoUploadDebounce = TimeSpan.FromMilliseconds(600);

    private ISshService? _sshService;
    private readonly ISshManager? _sshManager;
    private readonly ISessionStorageService? _storageService;
    private readonly ISettingsService? _settingsService;
    private readonly IWindowProvider? _windowProvider;
    private readonly IServiceProvider? _serviceProvider;
    private readonly Dictionary<string, WatchedFile> _watchedFiles = [];
    private readonly TerminalSettings _terminalSettingsFallback = new();
    private readonly ExplorerSettings _explorerSettingsFallback = new();
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();
    private string _homeDirectory = "/";

    private List<RemoteFile> _lastListing = [];
    private CancellationTokenSource? _transferCts;
    private int _activeTransfers;
    private bool _suppressNodeNavigation;
    private bool _hasBrowsed;
    private bool _disposed;

    private Window? Host => _windowProvider?.MainWindow;

    public TerminalSettings TerminalSettings => _settingsService?.Current.Terminal ?? _terminalSettingsFallback;
    private ExplorerSettings ExplorerSettings => _settingsService?.Current.Explorer ?? _explorerSettingsFallback;

    [ObservableProperty] private SshSession _session = new();
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isReconnecting;
    [ObservableProperty] private bool _showOverlay;
    [ObservableProperty] private string _overlayMessage = "";
    [ObservableProperty] private string _currentPath = "/";
    [ObservableProperty] private ObservableCollection<RemoteFile> _remoteFiles = [];
    [ObservableProperty] private RemoteFile? _selectedFile;
    [ObservableProperty] private string _statusMessage = "Not connected";
    [ObservableProperty] private double _transferProgress;
    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private string _transferFileName = "";
    [ObservableProperty] private string _transferSpeed = "";
    [ObservableProperty] private string _transferEta = "";
    [ObservableProperty] private string _transferDirection = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _fileExplorerStatus = "Not connected";
    [ObservableProperty] private TerminalStatsViewModel _terminalStats = new();
    [ObservableProperty] private bool _showTerminalStatsOverlay;
    [ObservableProperty] private int _selectedSessionTab;
    [ObservableProperty] private int _explorerViewMode; // 0 = Details, 1 = List, 2 = Icons
    [ObservableProperty] private ObservableCollection<DirectoryNode> _rootNodes = [];
    [ObservableProperty] private DirectoryNode? _selectedNode;
    [ObservableProperty] private SystemMonitorViewModel _systemMonitor = new();
    [ObservableProperty] private string _pathInput = "/";

    /// <summary>Address bar segments of <see cref="CurrentPath"/> (server root first).</summary>
    [ObservableProperty] private ObservableCollection<PathSegment> _pathSegments = [];

    /// <summary>Filters the current directory by name (search box).</summary>
    [ObservableProperty] private string _explorerFilter = "";

    /// <summary>Explorer status bar: "12 items".</summary>
    [ObservableProperty] private string _explorerSummary = "";

    /// <summary>Explorer status bar: "1 item selected  4.2 KB".</summary>
    [ObservableProperty] private string _selectionSummary = "";

    /// <summary>The listing has no entries (besides ".."): shows the empty-folder hint.</summary>
    [ObservableProperty] private bool _isFolderEmpty;

    public string SortBy => ExplorerSettings.SortBy;
    public bool SortDescending => ExplorerSettings.SortDescending;
    public bool ShowHiddenFiles => ExplorerSettings.ShowHiddenFiles;

    /// <summary>Name of the current directory (search box placeholder).</summary>
    public string CurrentFolderName => CurrentPath.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed[(trimmed.LastIndexOf('/') + 1)..] : "/";

    /// <summary>Set when the connection failed or broke permanently; shows the error panel with Retry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionError))]
    private string? _connectionError;

    public bool HasConnectionError => !string.IsNullOrEmpty(ConnectionError);

    /// <summary>State of this tab's connection (a saved session can be open in several tabs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;

    /// <summary>Connecting, reconnecting or disconnecting (shown as "in progress").</summary>
    public bool IsBusy => ConnectionStatus is ConnectionStatus.Connecting or ConnectionStatus.Reconnecting or ConnectionStatus.Disconnecting;

    /// <summary>1 for the first tab of a saved session, 2 for the second one that is open at the same time, ...</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private int _instanceNumber = 1;

    /// <summary>"Server", or "Server (2)" when the same session is open more than once.</summary>
    public string TabTitle => InstanceNumber > 1 ? $"{Session.Name} ({InstanceNumber})" : Session.Name;

    public event Action<string>? OutputReceived;
    public event Action<SessionViewModel>? SessionClosed;

    /// <summary>
    /// Raised when this session is disposed (tab closed). Views bound to this
    /// view model use this - rather than visual-tree detach - to tear down the
    /// terminal, so the terminal survives being re-parented across tab switches.
    /// </summary>
    public event Action<SessionViewModel>? Disposed;

    // Design-time constructor
    public SessionViewModel()
    {
        _session = new SshSession { Name = "Design Session", Host = "example.com", Username = "user" };
        LoadDesignTimeData();
    }

    // Standard constructor — no DI (e.g. design-time data in SessionsTreeViewModel)
    public SessionViewModel(SshSession session)
    {
        _session = session;
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    // DI constructor — ActivatorUtilities picks this
    public SessionViewModel(
        SshSession session,
        ISshManager sshManager,
        ISessionStorageService storageService,
        ISettingsService settingsService,
        IWindowProvider windowProvider,
        IServiceProvider serviceProvider)
    {
        _session = session;
        _sshManager = sshManager;
        _storageService = storageService;
        _settingsService = settingsService;
        _windowProvider = windowProvider;
        _serviceProvider = serviceProvider;

        _settingsService.SettingsChanged += OnSettingsChanged;
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SshSession.Name))
            OnPropertyChanged(nameof(TabTitle));
    }

    private void LoadDesignTimeData()
    {
        ConnectionStatus = ConnectionStatus.Connected;
        IsConnected = true;
        StatusMessage = "Connected to example.com";
        FileExplorerStatus = "SFTP ready";
        CurrentPath = "/home/user";

        IsTransferring = true;
        TransferProgress = 42;
        TransferFileName = "logs.tar.gz";
        TransferSpeed = "2.4 MB/s";
        TransferEta = "00:12";
        TransferDirection = "↓";

        PathInput = CurrentPath;
        SystemMonitor.LoadDesignTimeData();
        RootNodes =
        [
            new DirectoryNode("/", "/", withPlaceholder: false)
            {
                IsExpanded = true,
                Children =
                [
                    new DirectoryNode("home", "/home"),
                    new DirectoryNode("etc", "/etc"),
                    new DirectoryNode("var", "/var")
                ]
            }
        ];

        RemoteFiles =
        [
            new RemoteFile { Name = "..", IsDirectory = true, Permissions = "755" },
            new RemoteFile { Name = "Documents", IsDirectory = true, Permissions = "755" },
            new RemoteFile { Name = "config.json", IsDirectory = false, Size = 1024, Permissions = "644" },
            new RemoteFile { Name = "logs.tar.gz", IsDirectory = false, Size = 10_485_760, Permissions = "600" }
        ];

        SelectedFile = RemoteFiles.Count > 2 ? RemoteFiles[2] : null;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(TerminalSettings));
            OnPropertyChanged(nameof(SortBy));
            OnPropertyChanged(nameof(SortDescending));
            OnPropertyChanged(nameof(ShowHiddenFiles));

            // Hidden-file and sort settings apply to the current listing immediately.
            PublishListing(_lastListing);
        });
    }

    // ── Terminal ─────────────────────────────────────────────────────────

    private uint _terminalColumns = 80;
    private uint _terminalRows = 24;
    private uint _terminalPixelWidth = 640;
    private uint _terminalPixelHeight = 480;
    private bool _terminalSizeInitialized;

    public void SetTerminalSize(uint columns, uint rows, uint pixelWidth, uint pixelHeight)
    {
        if (columns < 20 || rows < 5) return;
        _terminalColumns = columns;
        _terminalRows = rows;
        _terminalPixelWidth = pixelWidth;
        _terminalPixelHeight = pixelHeight;
        _terminalSizeInitialized = true;
        _sshService?.ResizeTerminal(columns, rows, pixelWidth, pixelHeight);
    }

    public void SendTerminalInput(string text = "") => _sshService?.SendInput(text);

    private void WriteToTerminal(string text) => OutputReceived?.Invoke(text);

    // ── Connection ───────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanConnect))]
    public async Task ConnectAsync()
    {
        if (IsConnected || IsConnecting || _disposed) return;

        ConnectionError = null;
        ShowOverlay = true;
        OverlayMessage = $"Connecting to {Session.Host}...";

        // Give the terminal a moment to report its real size so the PTY starts with it.
        for (int i = 0; i < 10 && !_terminalSizeInitialized; i++)
            await Task.Delay(50);

        var service = EnsureService();
        service.ResizeTerminal(_terminalColumns, _terminalRows, _terminalPixelWidth, _terminalPixelHeight);

        try
        {
            await service.ConnectAsync();
        }
        catch (OperationCanceledException)
        {
            WriteToTerminal("\r\n\x1b[33m⚠ Connection canceled\x1b[0m\r\n");
        }
        catch (Exception ex)
        {
            // The error panel shows the details (see OnConnectionStatusChanged).
            WriteToTerminal($"\r\n\x1b[31m✗ Connection failed\x1b[0m\r\n{ex.Message}\r\n");
        }
    }

    private bool CanConnect() => !IsConnected && !IsConnecting && !_disposed;

    [RelayCommand]
    private async Task RetryConnectAsync()
    {
        ConnectionError = null;
        await ConnectAsync();
    }

    [RelayCommand]
    private void CloseSession() => SessionClosed?.Invoke(this);

    [RelayCommand] public void Disconnect() => _sshService?.Disconnect();
    [RelayCommand] private void CancelReconnect() => _sshService?.CancelReconnect();

    /// <summary>Creates the SSH service once per tab and wires its events (marshaled to the UI thread).</summary>
    private ISshService EnsureService()
    {
        if (_sshService != null)
            return _sshService;

        if (_sshManager is null)
            throw new InvalidOperationException("ISshManager is not available.");

        var service = _sshManager.Create(Session, _terminalColumns, _terminalRows, _terminalPixelWidth, _terminalPixelHeight);

        service.CredentialsRequired += () => ShowCredentialsDialogAsync(null);
        service.AuthenticationFailed += ShowCredentialsDialogAsync;
        service.HostKeyVerificationRequired += ShowHostKeyDialogAsync;
        service.AuthPromptRequired += ShowAuthPromptAsync;

        service.ShellDataReceived += output =>
            Dispatcher.UIThread.Post(() => { if (!_disposed) WriteToTerminal(output); });

        service.StatusChanged += (status, error) =>
            Dispatcher.UIThread.Post(() => OnConnectionStatusChanged(status, error));

        service.SftpStatusChanged += (available, message) =>
            Dispatcher.UIThread.Post(() => _ = OnSftpStatusChangedAsync(available, message));

        service.ReconnectAttempt += attempt =>
            Dispatcher.UIThread.Post(() => OverlayMessage = $"Connection lost.\nReconnecting to {Session.Host}... (attempt {attempt})");

        service.TransferProgressChanged += info => Dispatcher.UIThread.Post(() =>
        {
            TransferProgress = info.ProgressPercent;
            TransferFileName = info.FileName;
            TransferSpeed = info.SpeedDisplay;
            TransferEta = info.EtaDisplay;
            TransferDirection = info.Direction == T.Models.TransferDirection.Download ? "↓" : "↑";
        });

        _sshService = service;
        return service;
    }

    private void OnConnectionStatusChanged(ConnectionStatus status, string? error)
    {
        if (_disposed) return;

        ConnectionStatus = status;
        switch (status)
        {
            case ConnectionStatus.Connecting:
                IsConnecting = true;
                ShowOverlay = true;
                StatusMessage = OverlayMessage = $"Connecting to {Session.Host}...";
                FileExplorerStatus = "Connecting...";
                break;

            case ConnectionStatus.Connected:
                IsConnecting = false;
                IsReconnecting = false;
                IsConnected = true;
                ShowOverlay = false;
                ConnectionError = null;
                StatusMessage = $"Connected to {Session.Host}";
                if (_sshService is not null) SystemMonitor.Start(_sshService);
                break;

            case ConnectionStatus.Disconnecting:
                StatusMessage = "Disconnecting...";
                FileExplorerStatus = "Disconnecting...";
                break;

            case ConnectionStatus.Reconnecting:
                IsConnected = false;
                IsReconnecting = true;
                ShowOverlay = true;
                OverlayMessage = $"Connection lost.\nReconnecting to {Session.Host}...";
                StatusMessage = "Reconnecting...";
                FileExplorerStatus = "Reconnecting...";
                SystemMonitor.Stop();
                break;

            case ConnectionStatus.Disconnected:
                IsConnecting = false;
                IsReconnecting = false;
                IsConnected = false;
                ShowOverlay = false;
                SystemMonitor.Stop();
                ClearExplorer();
                FileExplorerStatus = "Not connected";

                if (error != null)
                {
                    // Keep the tab open so the user can read the error and retry.
                    ConnectionError = error;
                    StatusMessage = error;
                    WriteToTerminal($"\r\n\x1b[31m✗ {error}\x1b[0m\r\n");
                }
                else
                {
                    StatusMessage = "Disconnected";
                    SessionClosed?.Invoke(this);
                }
                break;
        }

        ConnectCommand.NotifyCanExecuteChanged();
    }

    private void ClearExplorer()
    {
        RemoteFiles.Clear();
        _lastListing = [];
        RootNodes.Clear();
        _backHistory.Clear();
        _forwardHistory.Clear();
        NotifyHistoryChanged();
        ExplorerSummary = SelectionSummary = "";
        IsFolderEmpty = false;
        _publishedPath = null;
        SetSelection([]);
    }

    // ── Dialogs requested by the SSH service (called from background threads) ─

    private async Task<bool> ShowHostKeyDialogAsync(HostKeyInfo hostKeyInfo)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Host is null || _serviceProvider is null || _disposed) return false;

            var vm = ActivatorUtilities.CreateInstance<HostKeyDialogViewModel>(_serviceProvider, hostKeyInfo);
            var view = _serviceProvider.GetRequiredService<HostKeyDialog>();
            view.DataContext = vm;

            var changed = hostKeyInfo.Status == HostKeyStatus.Changed;
            var dialog = new FAContentDialog
            {
                Title = changed ? "Host Key Changed" : "Host Key Verification",
                PrimaryButtonText = changed ? "Replace Key & Connect" : "Accept & Connect",
                SecondaryButtonText = "Reject",
                DefaultButton = FAContentDialogButton.Secondary,
                Content = view
            };

            return await dialog.ShowAsync(Host) == FAContentDialogResult.Primary;
        });
    }

    private async Task<string?> ShowAuthPromptAsync(AuthPromptRequest request)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Host is null || _disposed) return null;

            var message = string.IsNullOrWhiteSpace(request.Instruction)
                ? request.Prompt
                : $"{request.Instruction}\n\n{request.Prompt}";

            return await DialogService.PromptAsync(Host, $"Authentication – {request.Host}", message,
                isSecret: !request.IsEchoed, trim: false, cancellationToken: request.CancellationToken);
        });
    }

    private async Task<SshCredentials?> ShowCredentialsDialogAsync(string? errorMessage)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Host is null || _serviceProvider is null || _disposed) return null;

            var vm = ActivatorUtilities.CreateInstance<CredentialsDialogViewModel>(
                _serviceProvider,
                Session.Host,
                Session.Username,
                Session.PrivateKeyPath,
                Session.PrivateKeyPassword,
                errorMessage ?? "");
            vm.SetOwner(Host);

            var view = _serviceProvider.GetRequiredService<CredentialsDialog>();
            view.DataContext = vm;

            var dialog = new FAContentDialog
            {
                Title = "Login",
                PrimaryButtonText = "Connect",
                CloseButtonText = "Cancel",
                DefaultButton = FAContentDialogButton.Primary,
                Content = view
            };

            if (await dialog.ShowAsync(Host) != FAContentDialogResult.Primary || vm.Result is null)
                return null;

            var credentials = vm.Result;
            if (credentials.SaveCredentials)
            {
                // Only persisted when explicitly requested - otherwise the credentials stay
                // inside the SSH service and never touch the shared session object.
                Session.Username = credentials.Username;
                Session.Password = credentials.Password;
                Session.PrivateKeyPath = credentials.PrivateKeyPath;
                Session.PrivateKeyPassword = credentials.PrivateKeyPassword;

                if (_storageService is not null)
                {
                    try { await _storageService.UpdateSessionAsync(Session); }
                    catch (Exception ex) { StatusMessage = $"Could not save credentials: {ex.Message}"; }
                }
            }

            return credentials;
        });
    }

    // ── SFTP explorer ────────────────────────────────────────────────────

    private bool SftpReady => _sshService is { IsSftpAvailable: true };

    private async Task OnSftpStatusChangedAsync(bool available, string message)
    {
        if (_disposed) return;
        FileExplorerStatus = message;

        if (!available || _sshService is null)
            return;

        // After a reconnect continue in the directory the user was browsing.
        var home = _sshService.CurrentDirectory;
        _homeDirectory = home;
        CurrentPath = _hasBrowsed ? CurrentPath : home;
        if (!await TryLoadDirectoryAsync(CurrentPath) && CurrentPath != home)
        {
            CurrentPath = home;
            await TryLoadDirectoryAsync(home);
        }

        UpdatePathSegments();
        RootNodes.Clear();
        await InitializeTreeAsync();
    }

    private async Task<bool> TryLoadDirectoryAsync(string path)
    {
        if (_sshService is null || !SftpReady) return false;
        try
        {
            var files = await _sshService.ListDirectoryAsync(path);
            PublishListing(files);
            _hasBrowsed = true;
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = FileExplorerStatus = $"Error: {ex.Message}";
            return false;
        }
    }

    /// <summary>Applies the hidden-file and sort settings and publishes the listing.</summary>
    private void PublishListing(List<RemoteFile> files)
    {
        var keepSelected = _selectAfterRefresh
            ?? (_publishedPath == CurrentPath ? _selectedFiles.Select(f => f.Name).ToHashSet(StringComparer.Ordinal) : []);
        _selectAfterRefresh = null;
        _publishedPath = CurrentPath;

        _lastListing = files;
        var explorer = ExplorerSettings;
        var isRoot = CurrentPath == "/";

        var filter = ExplorerFilter.Trim();

        var visible = files.Where(f =>
            f.Name == ".." ? !isRoot && filter.Length == 0 : (explorer.ShowHiddenFiles || !f.Name.StartsWith('.')) &&
                (filter.Length == 0 || f.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)));

        // Like Windows Explorer: ".." first, then folders, then files - in the chosen order.
        IOrderedEnumerable<RemoteFile> ordered = visible
            .OrderByDescending(f => f.Name == "..")
            .ThenByDescending(f => f.IsDirectory);

        bool descending = explorer.SortDescending;
        ordered = explorer.SortBy switch
        {
            "Size" => descending ? ordered.ThenByDescending(f => f.Size) : ordered.ThenBy(f => f.Size),
            "Date" => descending ? ordered.ThenByDescending(f => f.LastModified) : ordered.ThenBy(f => f.LastModified),
            "Type" => descending ? ordered.ThenByDescending(f => f.Extension, StringComparer.Ordinal) : ordered.ThenBy(f => f.Extension, StringComparer.Ordinal),
            "Permissions" => descending ? ordered.ThenByDescending(f => f.Permissions, StringComparer.Ordinal) : ordered.ThenBy(f => f.Permissions, StringComparer.Ordinal),
            _ => ordered
        };
        ordered = explorer.SortBy == "Name" && descending
            ? ordered.ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            : ordered.ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

        RemoteFiles = new ObservableCollection<RemoteFile>(ordered);
        MarkCutItems();

        int count = RemoteFiles.Count(f => !f.IsParentLink);
        ExplorerSummary = count == 1 ? "1 item" : $"{count} items";
        IsFolderEmpty = count == 0;

        var selection = RemoteFiles.Where(f => !f.IsParentLink && keepSelected.Contains(f.Name)).ToList();
        SetSelection(selection);
        SelectionRestoreRequested?.Invoke(selection);
    }

    partial void OnExplorerFilterChanged(string value) => PublishListing(_lastListing);

    /// <summary>Sorts by <paramref name="key"/> (Name, Date, Type, Size, Permissions); the same key again reverses the order.</summary>
    [RelayCommand]
    private void SortExplorer(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var explorer = ExplorerSettings;
        if (explorer.SortBy == key)
        {
            explorer.SortDescending = !explorer.SortDescending;
        }
        else
        {
            explorer.SortBy = key;
            explorer.SortDescending = key is "Date" or "Size"; // newest / largest first, like Explorer
        }
        ApplyExplorerSettings();
    }

    [RelayCommand]
    private void SetSortDescending(object? descending)
    {
        ExplorerSettings.SortDescending = descending is true || (descending is string s && bool.TryParse(s, out var b) && b);
        ApplyExplorerSettings();
    }

    [RelayCommand]
    private async Task ToggleHiddenFilesAsync()
    {
        ExplorerSettings.ShowHiddenFiles = !ExplorerSettings.ShowHiddenFiles;
        ApplyExplorerSettings();

        // The folder tree lists hidden directories too.
        await RefreshTreeAsync();
    }

    /// <summary>Reloads the folder tree (after folders were created, moved or deleted).</summary>
    private async Task RefreshTreeAsync()
    {
        RootNodes.Clear();
        await InitializeTreeAsync();
    }

    /// <summary>Shows a changed sort / hidden-files choice right away and saves it (other tabs follow via SettingsChanged).</summary>
    private void ApplyExplorerSettings()
    {
        PublishListing(_lastListing);
        OnPropertyChanged(nameof(SortBy));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(ShowHiddenFiles));
        _settingsService?.Save();
    }

    private async Task InitializeTreeAsync()
    {
        if (!SftpReady || RootNodes.Count > 0) return;
        var root = new DirectoryNode("/", "/", withPlaceholder: false);
        await LoadNodeChildrenAsync(root);
        root.IsExpanded = true;
        RootNodes = [root];
        await ExpandTreeToPathAsync(CurrentPath);
    }

    public async Task LoadNodeChildrenAsync(DirectoryNode node)
    {
        if (_sshService is null || !SftpReady || node.HasLoadedChildren) return;
        node.IsLoading = true;
        try
        {
            var showHidden = ExplorerSettings.ShowHiddenFiles;
            var entries = await _sshService.ListDirectoryAsync(node.FullPath);
            node.SetChildren(entries
                .Where(f => f.IsDirectory && f.Name != ".." && (showHidden || !f.Name.StartsWith('.')))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => CreateNode(f.Name, f.FullPath)));
        }
        catch (Exception)
        {
            node.SetChildren([]);
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    private DirectoryNode CreateNode(string name, string fullPath)
    {
        var node = new DirectoryNode(name, fullPath);
        node.ExpandRequested += n => _ = LoadNodeChildrenAsync(n);
        return node;
    }

    private async Task ExpandTreeToPathAsync(string path)
    {
        if (RootNodes.Count == 0) return;
        var current = RootNodes[0];
        _suppressNodeNavigation = true;
        try
        {
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!current.HasLoadedChildren) await LoadNodeChildrenAsync(current);
                var next = current.Children.FirstOrDefault(c => c.Name == segment);
                if (next is null) break;
                current.IsExpanded = true;
                current = next;
            }
            if (!current.HasLoadedChildren) await LoadNodeChildrenAsync(current);
            SelectedNode = current;
            current.IsSelected = true;
        }
        finally
        {
            _suppressNodeNavigation = false;
        }
    }

    partial void OnSelectedNodeChanged(DirectoryNode? value)
    {
        if (value is null || _suppressNodeNavigation || value.IsPlaceholder) return;
        if (value.FullPath == CurrentPath) return;
        _ = NavigateToPathAsync(value.FullPath);
    }

    partial void OnCurrentPathChanged(string value)
    {
        PathInput = value;
        OnPropertyChanged(nameof(CurrentFolderName));
        NavigateUpCommand.NotifyCanExecuteChanged();
        UpdatePathSegments();
    }

    private void UpdatePathSegments()
    {
        var segments = new List<PathSegment> { new(string.IsNullOrEmpty(Session.Host) ? "/" : Session.Host, "/", IsRoot: true) };
        var current = "";
        foreach (var part in CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            segments.Add(new PathSegment(part, current));
        }
        PathSegments = new ObservableCollection<PathSegment>(segments);
    }

    /// <summary>Opens <paramref name="path"/>; returns false (and stays) when it cannot be listed.</summary>
    public async Task<bool> NavigateToPathAsync(string path, bool recordHistory = true)
    {
        if (!SftpReady || string.IsNullOrWhiteSpace(path)) return false;

        var previous = CurrentPath;
        if (previous != path)
            ExplorerFilter = ""; // like Explorer: a search belongs to the folder it was typed in

        CurrentPath = path;
        if (!await TryLoadDirectoryAsync(path))
        {
            CurrentPath = previous;
            return false;
        }

        if (recordHistory && !string.IsNullOrEmpty(previous) && previous != path)
        {
            _backHistory.Push(previous);
            _forwardHistory.Clear();
            NotifyHistoryChanged();
        }

        await ExpandTreeToPathAsync(path);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanNavigateBack))]
    private async Task NavigateBackAsync()
    {
        if (_backHistory.Count == 0) return;
        var current = CurrentPath;
        var path = _backHistory.Pop();
        if (await NavigateToPathAsync(path, recordHistory: false))
            _forwardHistory.Push(current);
        NotifyHistoryChanged();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateForward))]
    private async Task NavigateForwardAsync()
    {
        if (_forwardHistory.Count == 0) return;
        var current = CurrentPath;
        var path = _forwardHistory.Pop();
        if (await NavigateToPathAsync(path, recordHistory: false))
            _backHistory.Push(current);
        NotifyHistoryChanged();
    }

    [RelayCommand]
    private async Task NavigateHomeAsync() => await NavigateToPathAsync(_homeDirectory);

    private bool CanNavigateBack() => _backHistory.Count > 0;
    private bool CanNavigateForward() => _forwardHistory.Count > 0;
    private bool CanNavigateUp() => CurrentPath != "/";

    private void NotifyHistoryChanged()
    {
        NavigateBackCommand.NotifyCanExecuteChanged();
        NavigateForwardCommand.NotifyCanExecuteChanged();
    }

    private static string GetParentPath(string path) => RemotePath.GetParent(path);

    private static string CombineRemote(string directory, string name) => RemotePath.Combine(directory, name);

    [RelayCommand(CanExecute = nameof(CanNavigateUp))]
    private async Task NavigateUpAsync()
    {
        var parent = GetParentPath(CurrentPath);
        if (parent == CurrentPath) return;
        await NavigateToPathAsync(parent);
    }

    [RelayCommand]
    private async Task NavigateToInputAsync()
    {
        var path = PathInput?.Trim();
        if (string.IsNullOrEmpty(path)) return;
        await NavigateToPathAsync(path);
    }

    [RelayCommand]
    private void SetExplorerViewMode(object? mode)
    {
        if (mode is string s && int.TryParse(s, out var m)) ExplorerViewMode = m;
        else if (mode is int i) ExplorerViewMode = i;
    }

    [RelayCommand]
    private async Task RefreshDirectoryAsync()
    {
        if (!SftpReady)
        {
            FileExplorerStatus = "SFTP not available on this server";
            return;
        }
        await TryLoadDirectoryAsync(CurrentPath);
    }

    /// <summary>Opens a directory, or a symbolic link that points to one.</summary>
    [RelayCommand]
    public async Task NavigateToAsync(RemoteFile? file)
    {
        if (file is null || !SftpReady) return;

        if (file.Name == "..")
            await NavigateToPathAsync(GetParentPath(CurrentPath));
        else if (file.IsDirectory || file.IsSymbolicLink)
            await NavigateToPathAsync(file.FullPath);
    }

    /// <summary>Double-click / Enter: directories are opened, files are opened for editing.</summary>
    public async Task ActivateAsync(RemoteFile? file)
    {
        if (file is null || !SftpReady) return;

        if (file.IsDirectory || file.Name == "..")
        {
            await NavigateToAsync(file);
            return;
        }

        if (file.IsSymbolicLink)
        {
            // The target type is unknown: try it as a directory first.
            var previous = CurrentPath;
            await NavigateToPathAsync(file.FullPath);
            if (CurrentPath != previous) return;
        }

        await OpenFileAsync(file);
    }

    public bool OpenDirectoriesWithSingleClick => !ExplorerSettings.DoubleClickToOpen;

    public async Task CreateDirectoryAsync(string name)
    {
        if (_sshService is null || !SftpReady || !IsValidRemoteName(name)) return;
        try
        {
            await _sshService.CreateDirectoryAsync(CombineRemote(CurrentPath, name.Trim()));
            _selectAfterRefresh = [name.Trim()];
            await RefreshDirectoryAsync();
            await RefreshTreeAsync();
            StatusMessage = $"Folder created: {name}";
        }
        catch (Exception ex) { StatusMessage = $"Create folder failed: {ex.Message}"; }
    }

    public async Task RenameAsync(RemoteFile file, string newName)
    {
        if (_sshService is null || !SftpReady || !IsValidRemoteName(newName)) return;
        var newPath = CombineRemote(GetParentPath(file.FullPath), newName.Trim());
        try
        {
            await _sshService.RenameAsync(file.FullPath, newPath);
            _selectAfterRefresh = [newName.Trim()];
            await RefreshDirectoryAsync();
            if (file.IsDirectory) await RefreshTreeAsync();
            StatusMessage = $"Renamed to: {newName}";
        }
        catch (Exception ex) { StatusMessage = $"Rename failed: {ex.Message}"; }
    }

    private bool IsValidRemoteName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed is "." or ".." || trimmed.Contains('/'))
        {
            StatusMessage = "Invalid name.";
            return false;
        }
        return true;
    }

    // ── Transfers ────────────────────────────────────────────────────────

    private CancellationToken BeginTransfer(string status)
    {
        if (Interlocked.Increment(ref _activeTransfers) == 1)
            _transferCts = new CancellationTokenSource();

        IsTransferring = true;
        TransferProgress = 0;
        StatusMessage = status;
        return _transferCts!.Token;
    }

    private void EndTransfer()
    {
        if (Interlocked.Decrement(ref _activeTransfers) > 0) return;
        IsTransferring = false;
        _transferCts?.Dispose();
        _transferCts = null;
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        try { _transferCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    [RelayCommand]
    public async Task UploadFileAsync(string? localPath)
    {
        if (string.IsNullOrEmpty(localPath) || _sshService is null || !SftpReady) return;
        var fileName = Path.GetFileName(localPath);
        var remotePath = CombineRemote(CurrentPath, fileName);

        if (_lastListing.Any(f => f.Name == fileName) && Host is not null &&
            !await DialogService.ConfirmAsync(Host, "Upload", $"\"{fileName}\" already exists. Overwrite it?", "Overwrite"))
            return;

        var token = BeginTransfer($"Uploading: {fileName}");
        try
        {
            await _sshService.UploadFileAsync(localPath, remotePath, token);
            await RefreshDirectoryAsync();
            StatusMessage = $"Upload complete: {fileName}";
        }
        catch (OperationCanceledException) { StatusMessage = "Upload canceled"; }
        catch (Exception ex) { StatusMessage = $"Upload failed: {ex.Message}"; }
        finally { EndTransfer(); }
    }

    /// <summary>
    /// Uploads local files and folders (file picker, drag and drop) into <paramref name="targetDirectory"/>
    /// (default: the current directory).
    /// </summary>
    public async Task UploadPathsAsync(IReadOnlyList<string> localPaths, string? targetDirectory = null)
    {
        if (_sshService is null || !SftpReady || localPaths.Count == 0) return;

        var target = RemotePath.Normalize(targetDirectory ?? CurrentPath);
        List<RemoteFile> targetListing;
        try { targetListing = target == CurrentPath ? _lastListing : await _sshService.ListDirectoryAsync(target); }
        catch (Exception ex)
        {
            StatusMessage = $"Upload failed: {ex.Message}";
            return;
        }

        var names = localPaths.Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))).ToList();
        var existing = names.Where(n => targetListing.Any(f => f.Name == n)).ToList();
        if (existing.Count > 0 && Host is not null)
        {
            var question = existing.Count == 1
                ? $"\"{existing[0]}\" already exists. Overwrite it?"
                : $"{existing.Count} items already exist. Overwrite them?";
            if (!await DialogService.ConfirmAsync(Host, "Upload", question, "Overwrite"))
                return;
        }

        bool anyDirectory = localPaths.Any(Directory.Exists);
        var token = BeginTransfer(names.Count == 1 ? $"Uploading: {names[0]}" : $"Uploading {names.Count} items...");
        try
        {
            foreach (var localPath in localPaths)
                await UploadRecursiveAsync(localPath, target, targetListing, token);
            StatusMessage = names.Count == 1 ? $"Upload complete: {names[0]}" : $"Upload complete: {names.Count} items";
        }
        catch (OperationCanceledException) { StatusMessage = "Upload canceled"; }
        catch (Exception ex) { StatusMessage = $"Upload failed: {ex.Message}"; }
        finally { EndTransfer(); }

        if (target == CurrentPath)
            _selectAfterRefresh = names.ToHashSet(StringComparer.Ordinal);
        await RefreshDirectoryAsync();
        if (anyDirectory)
            await RefreshTreeAsync();
    }

    private async Task UploadRecursiveAsync(string localPath, string remoteDirectory, List<RemoteFile> topLevelListing, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var remotePath = CombineRemote(remoteDirectory, name);

        if (Directory.Exists(localPath))
        {
            // Junctions and symbolic links could point back up the tree: not followed.
            if (new DirectoryInfo(localPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return;

            try { await _sshService!.CreateDirectoryAsync(remotePath, token); }
            catch (Exception) when (topLevelListing.Count == 0 || topLevelListing.Any(f => f.Name == name && f.IsDirectory))
            {
                // Already exists: upload into it.
            }

            // Below the top level nothing is known about the remote side: existing folders are reused.
            foreach (var entry in Directory.EnumerateFileSystemEntries(localPath))
                await UploadRecursiveAsync(entry, remotePath, [], token);
        }
        else if (File.Exists(localPath))
        {
            StatusMessage = $"Uploading: {name}";
            await _sshService!.UploadFileAsync(localPath, remotePath, token);
        }
    }

    private string GetDownloadFolder()
    {
        var configured = ExplorerSettings.DefaultDownloadPath;
        var folder = !string.IsNullOrWhiteSpace(configured)
            ? Environment.ExpandEnvironmentVariables(configured.Trim())
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Never overwrite an existing local file or folder: "name (1).ext", "name (2).ext", ...</summary>
    private static string GetUniqueLocalPath(string folder, string fileName, bool isDirectory = false)
    {
        var path = Path.Combine(folder, fileName);
        var name = isDirectory ? fileName : Path.GetFileNameWithoutExtension(fileName);
        var extension = isDirectory ? "" : Path.GetExtension(fileName);
        for (int i = 1; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(folder, $"{name} ({i}){extension}");
        return path;
    }

    /// <summary>
    /// Remote names may contain characters that are invalid (or path separators) on the
    /// local file system - e.g. a server could send "..\\evil" to a Windows client.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) || c == '\\' || c == '/' ? '_' : c).ToArray()).Trim();
        return sanitized is "" or "." or ".." ? "download" : sanitized;
    }

    // ── Edit remote files locally (download, open, auto-upload on save) ──

    [RelayCommand]
    private async Task OpenFileAsync(RemoteFile? file)
    {
        if (file is null || file.IsDirectory || _sshService is null || !SftpReady) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "T", SanitizeFileName($"{Session.Host}_{Session.Port}"));
        Directory.CreateDirectory(tempDir);
        var safeName = SanitizeFileName(file.Name);
        var localPath = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(safeName)}_{DateTime.Now.Ticks}{Path.GetExtension(safeName)}");

        var token = BeginTransfer($"Downloading: {file.Name}");
        try
        {
            await _sshService.DownloadFileAsync(file.FullPath, localPath, token);
            StatusMessage = $"Opening: {file.Name}";
            SetupFileWatcher(localPath, file.FullPath, file.Name);
            OpenFileWithDefaultApp(localPath);
            StatusMessage = $"Opened: {file.Name} (changes are uploaded automatically)";
        }
        catch (OperationCanceledException) { StatusMessage = "Download canceled"; }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open: {ex.Message}";
            try { File.Delete(localPath); } catch (IOException) { }
        }
        finally { EndTransfer(); }
    }

    private sealed class WatchedFile(string localPath, string remotePath, string displayName, FileSystemWatcher watcher) : IDisposable
    {
        public string LocalPath { get; } = localPath;
        public string RemotePath { get; } = remotePath;
        public string DisplayName { get; } = displayName;
        public FileSystemWatcher Watcher { get; } = watcher;
        public Timer? Debounce { get; set; }
        public (DateTime WriteTime, long Length) LastUploaded { get; set; }

        public void Dispose()
        {
            Watcher.Dispose();
            Debounce?.Dispose();
        }
    }

    private void SetupFileWatcher(string localPath, string remotePath, string displayName)
    {
        if (_watchedFiles.Remove(localPath, out var existing))
            existing.Dispose();

        var dir = Path.GetDirectoryName(localPath);
        var fileName = Path.GetFileName(localPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName)) return;

        var watcher = new FileSystemWatcher(dir, fileName)
        {
            // Editors save in different ways: in place, or via temp file + rename.
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };

        var watched = new WatchedFile(localPath, remotePath, displayName, watcher)
        {
            LastUploaded = GetFileStamp(localPath)
        };

        // Debounce: editors raise several events per save and may still be writing.
        watched.Debounce = new Timer(_ => Dispatcher.UIThread.Post(() => _ = AutoUploadFileAsync(watched)));
        void OnChanged(object? sender, FileSystemEventArgs e) => watched.Debounce?.Change(AutoUploadDebounce, Timeout.InfiniteTimeSpan);
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Renamed += (s, e) => OnChanged(s, e);
        watcher.EnableRaisingEvents = true;

        _watchedFiles[localPath] = watched;
    }

    private static (DateTime, long) GetFileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1);
        }
        catch (IOException) { return (DateTime.MinValue, -1); }
    }

    private async Task AutoUploadFileAsync(WatchedFile watched)
    {
        if (_disposed || _sshService is null || !SftpReady || !File.Exists(watched.LocalPath)) return;

        var stamp = GetFileStamp(watched.LocalPath);
        if (stamp == watched.LastUploaded) return; // metadata-only event, content unchanged
        watched.LastUploaded = stamp;

        var token = BeginTransfer($"Uploading: {watched.DisplayName}...");
        try
        {
            await _sshService.UploadFileAsync(watched.LocalPath, watched.RemotePath, token);
            if (GetParentPath(watched.RemotePath) == CurrentPath) await RefreshDirectoryAsync();
            StatusMessage = $"Uploaded: {watched.DisplayName}";
        }
        catch (OperationCanceledException) { StatusMessage = "Upload canceled"; }
        catch (Exception ex)
        {
            watched.LastUploaded = default; // retry on the next save
            StatusMessage = $"Upload of {watched.DisplayName} failed: {ex.Message}";
        }
        finally { EndTransfer(); }
    }

    private static void OpenFileWithDefaultApp(string filePath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = filePath, UseShellExecute = true });
        }
        catch (Exception ex) when (!OperatingSystem.IsWindows())
        {
            var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(opener) { UseShellExecute = false };
                psi.ArgumentList.Add(filePath);
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException($"Could not open file: {ex.Message}", inner);
            }
        }
    }

    [RelayCommand]
    private void ToggleTerminalStatsOverlay()
    {
        ShowTerminalStatsOverlay = !ShowTerminalStatsOverlay;
    }

    // ── Dispose ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_settingsService is not null)
            _settingsService.SettingsChanged -= OnSettingsChanged;
        Session.PropertyChanged -= OnSessionPropertyChanged;

        Disposed?.Invoke(this);

        SystemMonitor.Dispose();
        CancelTransfer();

        foreach (var watched in _watchedFiles.Values)
        {
            watched.Dispose();
            try { if (File.Exists(watched.LocalPath)) File.Delete(watched.LocalPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _watchedFiles.Clear();

        // Only this tab's connection: other tabs of the same session stay connected.
        if (_sshService != null)
        {
            if (_sshManager != null) _sshManager.Release(_sshService);
            else _sshService.Dispose();
        }
        _sshService = null;

        ConnectionStatus = ConnectionStatus.Disconnected;
        GC.SuppressFinalize(this);
    }
}
