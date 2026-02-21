using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using T.Abstractions;
using T.Models;
using T.UI.Abstractions;
using T.UI.Views.Dialogs;

namespace T.UI.ViewModels;

public partial class SessionViewModel : ViewModelBase, IDisposable
{
    private ISshService? _sshService;
    private readonly ISshManager? _sshManager;
    private readonly ISessionStorageService? _storageService;
    private readonly ISettingsService? _settingsService;
    private readonly IWindowProvider? _windowProvider;
    private readonly IServiceProvider? _serviceProvider;
    private readonly Dictionary<string, (string remotePath, FileSystemWatcher watcher)> _watchedFiles = [];
    private readonly Dictionary<string, DateTime> _lastUploadTimes = [];
    private readonly TerminalSettings _terminalSettingsFallback = new();

    private Window? Host => _windowProvider?.MainWindow;

    public TerminalSettings TerminalSettings => _settingsService?.Current.Terminal ?? _terminalSettingsFallback;

    [ObservableProperty] private SshSession _session = new();
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isReconnecting;
    [ObservableProperty] private bool _showOverlay;
    [ObservableProperty] private string _overlayMessage = "";
    [ObservableProperty] private string _commandInput = "";
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

    public event Action<string>? OutputReceived;
    public event Action<SessionViewModel>? SessionClosed;

    public string DisplayName => IsConnected ? $"{Session.Name} ●" : Session.Name;

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
    }

    private void LoadDesignTimeData()
    {
        IsConnected = true;
        CurrentPath = "/home/user";
        StatusMessage = "Connected to example.com";
        RemoteFiles =
        [
            new RemoteFile { Name = "..", IsDirectory = true, Permissions = "755" },
            new RemoteFile { Name = "Documents", IsDirectory = true, Permissions = "755" },
            new RemoteFile { Name = "config.json", IsDirectory = false, Size = 1024, Permissions = "644" },
        ];
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        OnPropertyChanged(nameof(TerminalSettings));
    }

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
        if (IsConnected) _sshService?.ResizeTerminal(columns, rows, pixelWidth, pixelHeight);
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    public async Task ConnectAsync()
    {
        if (IsConnected || IsConnecting) return;

        ShowOverlay = true;
        OverlayMessage = $"Connecting to {Session.Host}...";

        if (!_terminalSizeInitialized)
            for (int i = 0; i < 10 && !_terminalSizeInitialized; i++)
                await Task.Delay(50);

        _sshService?.Dispose();

        if (_sshManager is null)
            throw new InvalidOperationException("ISshManager is not available.");

        _sshService = _sshManager.Create(
            Session,
            _terminalColumns,
            _terminalRows,
            _terminalPixelWidth,
            _terminalPixelHeight);

        _sshService.CredentialsRequired += async () => await ShowCredentialsDialogAsync(null);
        _sshService.AuthenticationFailed += async (err) => await ShowCredentialsDialogAsync(err);
        _sshService.HostKeyVerificationRequired += async (info) => await ShowHostKeyDialogAsync(info);

        _sshService.SftpStatusChanged += message => Dispatcher.UIThread.Post(() =>
        {
            FileExplorerStatus = message;
            if (!message.Contains("available")) RemoteFiles.Clear();
        });

        _sshService.ShellDataReceived += output =>
            Dispatcher.UIThread.Post(() => OutputReceived?.Invoke(output));

        _sshService.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => OnConnectionStatusChanged(status));

        _sshService.ReconnectAttempt += attempt =>
            Dispatcher.UIThread.Post(() => OverlayMessage = $"Reconnecting to {Session.Host}...\nAttempt {attempt}");

        _sshService.TransferProgressChanged += info => Dispatcher.UIThread.Post(() =>
        {
            TransferProgress = info.ProgressPercent;
            TransferFileName = info.FileName;
            TransferSpeed = info.SpeedDisplay;
            TransferEta = info.EtaDisplay;
            TransferDirection = info.Direction == T.Models.TransferDirection.Download ? "↓" : "↑";
        });

        try
        {
            await _sshService.ConnectAsync();
            _sshService.ResizeTerminal(_terminalColumns, _terminalRows, _terminalPixelWidth, _terminalPixelHeight);
            if (_sshService.IsSftpAvailable)
            {
                CurrentPath = _sshService.CurrentDirectory;
                await RefreshDirectoryAsync();
            }
        }
        catch (SshConnectionException ex)
        {
            ShowOverlay = false;
            StatusMessage = $"Connection failed: {ex.Message}";
            OutputReceived?.Invoke($"\r\n\x1b[31m✗ Connection failed\x1b[0m\r\n{ex.Message}\r\n");
            FileExplorerStatus = "Not connected";
            RemoteFiles.Clear();
        }
        catch (SshAuthenticationException ex)
        {
            ShowOverlay = false;
            StatusMessage = "Authentication failed";
            OutputReceived?.Invoke($"\r\n\x1b[31m✗ Authentication failed\x1b[0m\r\n{ex.Message}\r\n");
            FileExplorerStatus = "Not connected";
            RemoteFiles.Clear();
        }
        catch (OperationCanceledException)
        {
            ShowOverlay = false;
            StatusMessage = "Connection canceled";
            OutputReceived?.Invoke("\r\n\x1b[33m⚠ Connection canceled by user\x1b[0m\r\n");
            FileExplorerStatus = "Not connected";
            RemoteFiles.Clear();
        }
        catch (Exception ex)
        {
            ShowOverlay = false;
            StatusMessage = $"Error: {ex.Message}";
            OutputReceived?.Invoke($"\r\n\x1b[31m✗ Connection error\x1b[0m\r\n{ex.Message}\r\n");
            FileExplorerStatus = "Not connected";
            RemoteFiles.Clear();
        }
        finally
        {
            if (!IsConnecting && ShowOverlay) ShowOverlay = false;
        }
    }

    private async Task<bool> ShowHostKeyDialogAsync(HostKeyInfo hostKeyInfo)
    {
        bool accepted = false;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Host is null || _serviceProvider is null) { accepted = false; return; }

            var vm = ActivatorUtilities.CreateInstance<HostKeyDialogViewModel>(_serviceProvider, hostKeyInfo);
            var view = _serviceProvider.GetRequiredService<HostKeyDialog>();
            view.DataContext = vm;

            var dialog = new ContentDialog
            {
                Title = "Host Key Verification",
                PrimaryButtonText = "Accept & Connect",
                SecondaryButtonText = "Reject",
                DefaultButton = ContentDialogButton.Secondary,
                Content = view
            };

            accepted = await dialog.ShowAsync(Host) == ContentDialogResult.Primary;
            if (!accepted)
            {
                StatusMessage = "Connection rejected by user";
                OutputReceived?.Invoke($"\r\n\x1b[33m⚠ Host key verification failed\x1b[0m\r\nConnection rejected by user.\r\n");
            }
        });
        return accepted;
    }

    private async Task<SshCredentials?> ShowCredentialsDialogAsync(string? errorMessage)
    {
        SshCredentials? credentials = null;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Host is null || _serviceProvider is null) { credentials = null; return; }

            var vm = ActivatorUtilities.CreateInstance<CredentialsDialogViewModel>(
                _serviceProvider,
                Session.Host,
                Session.Username,
                Session.PrivateKeyPath,
                Session.PrivateKeyPassword,
                errorMessage ?? "");

            var view = _serviceProvider.GetRequiredService<CredentialsDialog>();
            view.DataContext = vm;

            var dialog = new ContentDialog
            {
                Title = "Login", PrimaryButtonText = "Connect", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = view
            };

            if (await dialog.ShowAsync(Host) == ContentDialogResult.Primary && vm.Result != null)
            {
                credentials = vm.Result;
                if (vm.Result.SaveCredentials)
                {
                    Session.Username = vm.Result.Username;
                    Session.Password = vm.Result.Password;
                    Session.PrivateKeyPath = vm.Result.PrivateKeyPath;
                    Session.PrivateKeyPassword = vm.Result.PrivateKeyPassword;

                    if (_storageService is not null)
                        try { await _storageService.UpdateSessionAsync(Session); } catch { }
                }
            }
        });
        return credentials;
    }

    private bool CanConnect() => !IsConnected && !IsConnecting;

    [RelayCommand] public void Disconnect() => _sshService?.Disconnect();
    [RelayCommand] private void CancelReconnect() => _sshService?.CancelReconnect();

    private void OnConnectionStatusChanged(ConnectionStatus status)
    {
        Session.ConnectionStatus = status;
        switch (status)
        {
            case ConnectionStatus.Connecting:
                IsConnecting = true; ShowOverlay = true;
                StatusMessage = OverlayMessage = $"Connecting to {Session.Host}...";
                FileExplorerStatus = "Connecting...";
                break;
            case ConnectionStatus.Connected:
                IsConnecting = false; IsReconnecting = false; IsConnected = true; ShowOverlay = false;
                StatusMessage = $"Connected to {Session.Host}";
                OnPropertyChanged(nameof(DisplayName));
                ConnectCommand.NotifyCanExecuteChanged();
                _ = RestoreSessionAsync();
                break;
            case ConnectionStatus.Disconnecting:
                StatusMessage = "Disconnecting...";
                FileExplorerStatus = "Disconnecting...";
                break;
            case ConnectionStatus.Reconnecting:
                IsConnected = false; IsReconnecting = true; ShowOverlay = true;
                OverlayMessage = $"Connection lost.\nReconnecting to {Session.Host}...";
                StatusMessage = "Reconnecting...";
                FileExplorerStatus = "Reconnecting...";
                OnPropertyChanged(nameof(DisplayName));
                break;
            case ConnectionStatus.Disconnected:
                IsConnecting = false; IsReconnecting = false; IsConnected = false; ShowOverlay = false;
                StatusMessage = "Disconnected";
                FileExplorerStatus = "Not connected - Click 'Connect' to establish a connection";
                RemoteFiles.Clear();
                OnPropertyChanged(nameof(DisplayName));
                ConnectCommand.NotifyCanExecuteChanged();
                SessionClosed?.Invoke(this);
                break;
        }
    }

    private async Task RestoreSessionAsync()
    {
        if (_sshService is null || !_sshService.IsSftpAvailable) return;
        try
        {
            RemoteFiles = new ObservableCollection<RemoteFile>(await _sshService.ListDirectoryAsync(CurrentPath));
        }
        catch
        {
            CurrentPath = _sshService.CurrentDirectory;
            await RefreshDirectoryAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshDirectoryAsync()
    {
        if (_sshService is null || !_sshService.IsSftpAvailable)
        {
            FileExplorerStatus = "SFTP not available on this server";
            return;
        }
        try
        {
            RemoteFiles = new ObservableCollection<RemoteFile>(await _sshService.ListDirectoryAsync(CurrentPath));
        }
        catch (Exception ex)
        {
            StatusMessage = FileExplorerStatus = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task NavigateToAsync(RemoteFile? file)
    {
        if (file is null || _sshService is null || !_sshService.IsSftpAvailable) return;
        if (file.IsDirectory)
        {
            CurrentPath = file.Name == ".."
                ? (Path.GetDirectoryName(CurrentPath.TrimEnd('/'))?.Replace("\\", "/") ?? "/")
                : file.FullPath;
            await RefreshDirectoryAsync();
        }
    }

    [RelayCommand]
    private async Task DownloadFileAsync()
    {
        if (SelectedFile is null || SelectedFile.IsDirectory || _sshService is null || !_sshService.IsSftpAvailable) return;
        var localPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", SelectedFile.Name);
        IsTransferring = true; TransferProgress = 0;
        StatusMessage = $"Downloading: {SelectedFile.Name}";
        try { await _sshService.DownloadFileAsync(SelectedFile.FullPath, localPath); StatusMessage = $"Download complete: {localPath}"; }
        catch (Exception ex) { StatusMessage = $"Download failed: {ex.Message}"; }
        finally { IsTransferring = false; }
    }

    [RelayCommand]
    public async Task UploadFileAsync(string? localPath)
    {
        if (string.IsNullOrEmpty(localPath) || _sshService is null || !_sshService.IsSftpAvailable) return;
        var fileName = Path.GetFileName(localPath);
        var remotePath = $"{CurrentPath.TrimEnd('/')}/{fileName}";
        IsTransferring = true; TransferProgress = 0;
        StatusMessage = $"Uploading: {fileName}";
        try { await _sshService.UploadFileAsync(localPath, remotePath); await RefreshDirectoryAsync(); StatusMessage = $"Upload complete: {fileName}"; }
        catch (Exception ex) { StatusMessage = $"Upload failed: {ex.Message}"; }
        finally { IsTransferring = false; }
    }

    [RelayCommand]
    private async Task DeleteFileAsync()
    {
        if (SelectedFile is null || _sshService is null || !_sshService.IsSftpAvailable) return;
        var name = SelectedFile.Name;
        try { await _sshService.DeleteAsync(SelectedFile.FullPath, SelectedFile.IsDirectory); await RefreshDirectoryAsync(); StatusMessage = $"Deleted: {name}"; }
        catch (Exception ex) { StatusMessage = $"Delete failed: {ex.Message}"; }
    }

    public async Task ChangePermissionsAsync(short permissions)
    {
        if (SelectedFile is null || _sshService is null || !_sshService.IsSftpAvailable) return;
        var name = SelectedFile.Name;
        try { await _sshService.ChangePermissionsAsync(SelectedFile.FullPath, permissions); await RefreshDirectoryAsync(); StatusMessage = $"Permissions changed: {name}"; }
        catch (Exception ex) { StatusMessage = $"Change permissions failed: {ex.Message}"; }
    }

    public void SendTerminalInput(string text = "") => _sshService?.SendInput(text);

    private static Window? GetMainWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.MainWindow : null;

    [RelayCommand]
    private async Task OpenFileAsync(RemoteFile? file)
    {
        if (file is null || file.IsDirectory || _sshService is null || !_sshService.IsSftpAvailable) return;
        var tempDir = Path.Combine(Path.GetTempPath(), "T", Session.Host);
        Directory.CreateDirectory(tempDir);
        var localPath = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(file.Name)}_{DateTime.Now.Ticks}{Path.GetExtension(file.Name)}");
        IsTransferring = true; TransferProgress = 0;
        StatusMessage = $"Downloading: {file.Name}";
        try
        {
            await _sshService.DownloadFileAsync(file.FullPath, localPath);
            StatusMessage = $"Opening: {file.Name}";
            SetupFileWatcher(localPath, file.FullPath, file.Name);
            OpenFileWithDefaultApp(localPath);
            StatusMessage = $"Opened: {file.Name} (auto-upload enabled)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open: {ex.Message}";
            try { File.Delete(localPath); } catch { }
        }
        finally { IsTransferring = false; }
    }

    private void SetupFileWatcher(string localPath, string remotePath, string displayName)
    {
        if (_watchedFiles.TryGetValue(localPath, out var existing)) { existing.watcher.Dispose(); _watchedFiles.Remove(localPath); }
        var dir = Path.GetDirectoryName(localPath);
        var fn = Path.GetFileName(localPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fn)) return;
        var watcher = new FileSystemWatcher(dir, fn) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
        watcher.Changed += async (_, _) =>
        {
            var now = DateTime.Now;
            if (_lastUploadTimes.TryGetValue(localPath, out var last) && (now - last).TotalSeconds < 2) return;
            _lastUploadTimes[localPath] = now;
            await Dispatcher.UIThread.InvokeAsync(async () => await AutoUploadFileAsync(localPath, remotePath, displayName));
        };
        _watchedFiles[localPath] = (remotePath, watcher);
    }

    private async Task AutoUploadFileAsync(string localPath, string remotePath, string displayName)
    {
        if (_sshService is null || !File.Exists(localPath) || !_sshService.IsSftpAvailable) return;
        await Task.Delay(100);
        IsTransferring = true; TransferProgress = 0;
        StatusMessage = $"Uploading: {displayName}...";
        try
        {
            await _sshService.UploadFileAsync(localPath, remotePath);
            if (Path.GetDirectoryName(remotePath)?.Replace("\\", "/") == CurrentPath) await RefreshDirectoryAsync();
            StatusMessage = $"Uploaded: {displayName}";
        }
        catch (Exception ex) { StatusMessage = $"Upload failed: {ex.Message}"; }
        finally { IsTransferring = false; }
    }

    private static void OpenFileWithDefaultApp(string filePath)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = filePath, UseShellExecute = true }); }
        catch (Exception ex)
        {
            if (OperatingSystem.IsLinux())
                try { System.Diagnostics.Process.Start("xdg-open", $"\"{filePath}\""); }
                catch { throw new InvalidOperationException($"Could not open file: {ex.Message}", ex); }
            else if (OperatingSystem.IsMacOS())
                try { System.Diagnostics.Process.Start("open", $"\"{filePath}\""); }
                catch { throw new InvalidOperationException($"Could not open file: {ex.Message}", ex); }
            else throw;
        }
    }

    public void Dispose()
    {
        if (_settingsService is not null)
            _settingsService.SettingsChanged -= OnSettingsChanged;

        foreach (var (localPath, (_, watcher)) in _watchedFiles)
        {
            watcher.Dispose();
            try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
        }
        _watchedFiles.Clear();
        _lastUploadTimes.Clear();

        // Release via manager if injected, otherwise dispose directly
        if (_sshManager != null)
            _sshManager.Release(Session.Id);
        else
            _sshService?.Dispose();

        GC.SuppressFinalize(this);
    }
}