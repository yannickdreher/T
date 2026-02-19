using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Renci.SshNet.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using T.Models;
using T.Services;
using T.Views;

namespace T.ViewModels;

public partial class SessionViewModel : ViewModelBase, IDisposable
{
    private SshService? _sshService;
    private readonly Dictionary<string, (string remotePath, FileSystemWatcher watcher)> _watchedFiles = [];
    private readonly Dictionary<string, DateTime> _lastUploadTimes = [];

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

    public event Action<string>? OutputReceived;
    public event Action<SessionViewModel>? SessionClosed;

    public string DisplayName => IsConnected ? $"{Session.Name} ●" : Session.Name;

    public SessionViewModel()
    {
        _session = new SshSession { Name = "Design Session", Host = "example.com", Username = "user" };
        LoadDesignTimeData();
    }

    public SessionViewModel(SshSession session)
    {
        _session = session;
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

        if (IsConnected)
            _sshService?.ResizeTerminal(columns, rows, pixelWidth, pixelHeight);
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    public async Task ConnectAsync()
    {
        if (IsConnected || IsConnecting) return;

        ShowOverlay = true;
        OverlayMessage = $"Connecting to {Session.Host}...";

        if (!_terminalSizeInitialized)
        {
            for (int i = 0; i < 10 && !_terminalSizeInitialized; i++)
            {
                await Task.Delay(50);
            }
        }

        _sshService?.Dispose();
        _sshService = new SshService(Session, _terminalColumns, _terminalRows, 
                                      _terminalPixelWidth, _terminalPixelHeight);

        _sshService.CredentialsRequired += async () =>
        {
            return await ShowCredentialsDialogAsync(null);
        };

        _sshService.AuthenticationFailed += async (errorMessage) =>
        {
            return await ShowCredentialsDialogAsync(errorMessage);
        };

        _sshService.HostKeyVerificationRequired += async (hostKeyInfo) =>
        {
            return await ShowHostKeyDialogAsync(hostKeyInfo);
        };

        _sshService.SftpStatusChanged += message =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                FileExplorerStatus = message;
                if (!message.Contains("available"))
                {
                    RemoteFiles.Clear();
                }
            });
        };

        _sshService.ShellDataReceived += output =>
            Dispatcher.UIThread.Post(() => OutputReceived?.Invoke(output));

        _sshService.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => OnConnectionStatusChanged(status));

        _sshService.ReconnectAttempt += attempt =>
            Dispatcher.UIThread.Post(() =>
            {
                OverlayMessage = $"Reconnecting to {Session.Host}...\nAttempt {attempt}";
            });

        _sshService.TransferProgressChanged += info =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                TransferProgress = info.ProgressPercent;
                TransferFileName = info.FileName;
                TransferSpeed = info.SpeedDisplay;
                TransferEta = info.EtaDisplay;
                TransferDirection = info.Direction == Models.TransferDirection.Download ? "↓" : "↑";
            });
        };

        try
        {
            await _sshService.ConnectAsync();

            _sshService.ResizeTerminal(_terminalColumns, _terminalRows, _terminalPixelWidth, _terminalPixelHeight);

            // Nur Directory laden wenn SFTP verfügbar ist
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
            StatusMessage = $"Authentication failed";
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
            if (!IsConnecting && ShowOverlay)
            {
                ShowOverlay = false;
            }
        }
    }

    private async Task<bool> ShowHostKeyDialogAsync(HostKeyInfo hostKeyInfo)
    {
        bool accepted = false;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var viewModel = new HostKeyDialogViewModel(hostKeyInfo);
            var content = new HostKeyDialog { DataContext = viewModel };
            
            var dialog = new ContentDialog
            {
                Title = "Host Key Verification",
                PrimaryButtonText = "Accept & Connect",
                SecondaryButtonText = "Reject",
                DefaultButton = ContentDialogButton.Secondary,
                Content = content
            };

            var mainWindow = GetMainWindow();
            if (mainWindow == null)
            {
                accepted = false;
                return;
            }

            var result = await dialog.ShowAsync(mainWindow);
            accepted = result == ContentDialogResult.Primary;
            
            if (!accepted)
            {
                StatusMessage = "Connection rejected by user";
                OutputReceived?.Invoke($"\r\n\x1b[33m⚠ Host key verification failed\x1b[0m\r\nConnection rejected by user.\r\n");
            }
        });

        return accepted;
    }

    private bool CanConnect() => !IsConnected && !IsConnecting;

    private async Task<SshCredentials?> ShowCredentialsDialogAsync(string? errorMessage)
    {
        SshCredentials? credentials = null;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var viewModel = new CredentialsDialogViewModel(
                Session.Host,
                Session.Username,
                Session.PrivateKeyPath,
                Session.PrivateKeyPassword,
                errorMessage);

            var content = new CredentialsDialog { DataContext = viewModel };
            
            var dialog = new ContentDialog
            {
                Title = "Login",
                PrimaryButtonText = "Connect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = content
            };

            var mainWindow = GetMainWindow();
            if (mainWindow == null)
            {
                credentials = null;
                return;
            }

            var result = await dialog.ShowAsync(mainWindow);
            
            if (result == ContentDialogResult.Primary && viewModel.Result != null)
            {
                credentials = viewModel.Result;

                if (viewModel.Result.SaveCredentials)
                {
                    Session.Username = viewModel.Result.Username;
                    Session.Password = viewModel.Result.Password;
                    Session.PrivateKeyPath = viewModel.Result.PrivateKeyPath;
                    Session.PrivateKeyPassword = viewModel.Result.PrivateKeyPassword;

                    try
                    {
                        await SqliteSessionStorageService.Current.UpdateSessionAsync(Session);
                    }
                    catch
                    { }
                }
            }
        });

        return credentials;
    }

    [RelayCommand]
    public void Disconnect()
    {
        _sshService?.Disconnect();
    }

    [RelayCommand]
    private void CancelReconnect()
    {
        _sshService?.CancelReconnect();
    }

    private void OnConnectionStatusChanged(ConnectionStatus status)
    {
        Session.ConnectionStatus = status;
        
        switch (status)
        {
            case ConnectionStatus.Connecting:
                IsConnecting = true;
                ShowOverlay = true;
                StatusMessage = $"Connecting to {Session.Host}...";
                OverlayMessage = $"Connecting to {Session.Host}...";
                FileExplorerStatus = "Connecting...";
                break;

            case ConnectionStatus.Connected:
                IsConnecting = false;
                IsReconnecting = false;
                IsConnected = true;
                ShowOverlay = false;
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
                IsConnected = false;
                IsReconnecting = true;
                ShowOverlay = true;
                OverlayMessage = $"Connection lost.\nReconnecting to {Session.Host}...";
                StatusMessage = "Reconnecting...";
                FileExplorerStatus = "Reconnecting...";
                OnPropertyChanged(nameof(DisplayName));
                break;

            case ConnectionStatus.Disconnected:
                IsConnecting = false;
                IsReconnecting = false;
                IsConnected = false;
                ShowOverlay = false;
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
        if (_sshService == null || !_sshService.IsSftpAvailable) return;

        try
        {
            var files = await _sshService.ListDirectoryAsync(CurrentPath);
            RemoteFiles = new ObservableCollection<RemoteFile>(files);
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
        if (_sshService == null || !_sshService.IsSftpAvailable)
        {
            FileExplorerStatus = "SFTP not available on this server";
            return;
        }

        try
        {
            var files = await _sshService.ListDirectoryAsync(CurrentPath);
            RemoteFiles = new ObservableCollection<RemoteFile>(files);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            FileExplorerStatus = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task NavigateToAsync(RemoteFile? file)
    {
        if (file == null || _sshService == null || !_sshService.IsSftpAvailable) return;
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
        if (SelectedFile == null || SelectedFile.IsDirectory || _sshService == null || !_sshService.IsSftpAvailable) return;
        var localPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", SelectedFile.Name);

        IsTransferring = true;
        TransferProgress = 0;
        StatusMessage = $"Downloading: {SelectedFile.Name}";
        try
        {
            await _sshService.DownloadFileAsync(SelectedFile.FullPath, localPath);
            StatusMessage = $"Download complete: {localPath}";
        }
        catch (Exception ex) { StatusMessage = $"Download failed: {ex.Message}"; }
        finally { IsTransferring = false; }
    }

    [RelayCommand]
    public async Task UploadFileAsync(string? localPath)
    {
        if (string.IsNullOrEmpty(localPath) || _sshService == null || !_sshService.IsSftpAvailable) return;
        var fileName = Path.GetFileName(localPath);
        var remotePath = $"{CurrentPath.TrimEnd('/')}/{fileName}";

        IsTransferring = true;
        TransferProgress = 0;
        StatusMessage = $"Uploading: {fileName}";
        try
        {
            await _sshService.UploadFileAsync(localPath, remotePath);
            await RefreshDirectoryAsync();
            StatusMessage = $"Upload complete: {fileName}";
        }
        catch (Exception ex) { StatusMessage = $"Upload failed: {ex.Message}"; }
        finally { IsTransferring = false; }
    }

    [RelayCommand]
    private async Task DeleteFileAsync()
    {
        if (SelectedFile == null || _sshService == null || !_sshService.IsSftpAvailable) return;
        
        var deletedFileName = SelectedFile.Name;
        
        try
        {
            await _sshService.DeleteAsync(SelectedFile.FullPath, SelectedFile.IsDirectory);
            await RefreshDirectoryAsync();
            StatusMessage = $"Deleted: {deletedFileName}";
        }
        catch (Exception ex) { StatusMessage = $"Delete failed: {ex.Message}"; }
    }

    public async Task ChangePermissionsAsync(short permissions)
    {
        if (SelectedFile == null || _sshService == null || !_sshService.IsSftpAvailable) return;
        
        var fileName = SelectedFile.Name;
        
        try
        {
            await _sshService.ChangePermissionsAsync(SelectedFile.FullPath, permissions);
            await RefreshDirectoryAsync();
            StatusMessage = $"Permissions changed: {fileName}";
        }
        catch (Exception ex) { StatusMessage = $"Change permissions failed: {ex.Message}"; }
    }

    public void SendTerminalInput(string text = "") => _sshService?.SendInput(text);

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    [RelayCommand]
    private async Task OpenFileAsync(RemoteFile? file)
    {
        if (file == null || file.IsDirectory || _sshService == null || !_sshService.IsSftpAvailable) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "T", Session.Host);
        Directory.CreateDirectory(tempDir);

        var timestamp = DateTime.Now.Ticks;
        var fileName = Path.GetFileNameWithoutExtension(file.Name);
        var extension = Path.GetExtension(file.Name);
        var localPath = Path.Combine(tempDir, $"{fileName}_{timestamp}{extension}");

        IsTransferring = true;
        TransferProgress = 0;
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
        finally
        {
            IsTransferring = false;
        }
    }

    private void SetupFileWatcher(string localPath, string remotePath, string displayName)
    {
        if (_watchedFiles.TryGetValue(localPath, out var existing))
        {
            existing.watcher.Dispose();
            _watchedFiles.Remove(localPath);
        }

        var directory = Path.GetDirectoryName(localPath);
        var fileName = Path.GetFileName(localPath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            return;

        var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        watcher.Changed += async (sender, e) =>
        {
            var now = DateTime.Now;
            if (_lastUploadTimes.TryGetValue(localPath, out var lastTime))
            {
                if ((now - lastTime).TotalSeconds < 2)
                    return;
            }

            _lastUploadTimes[localPath] = now;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await AutoUploadFileAsync(localPath, remotePath, displayName);
            });
        };

        _watchedFiles[localPath] = (remotePath, watcher);
    }

    private async Task AutoUploadFileAsync(string localPath, string remotePath, string displayName)
    {
        if (_sshService == null || !File.Exists(localPath) || !_sshService.IsSftpAvailable)
            return;

        await Task.Delay(100);

        IsTransferring = true;
        TransferProgress = 0;
        StatusMessage = $"Uploading: {displayName}...";

        try
        {
            await _sshService.UploadFileAsync(localPath, remotePath);
            
            if (Path.GetDirectoryName(remotePath)?.Replace("\\", "/") == CurrentPath)
            {
                await RefreshDirectoryAsync();
            }

            StatusMessage = $"Uploaded: {displayName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Upload failed: {ex.Message}";
        }
        finally
        {
            IsTransferring = false;
        }
    }

    private static void OpenFileWithDefaultApp(string filePath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            };

            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    System.Diagnostics.Process.Start("xdg-open", $"\"{filePath}\"");
                }
                catch
                {
                    throw new InvalidOperationException($"Could not open file: {ex.Message}", ex);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                try
                {
                    System.Diagnostics.Process.Start("open", $"\"{filePath}\"");
                }
                catch
                {
                    throw new InvalidOperationException($"Could not open file: {ex.Message}", ex);
                }
            }
            else
            {
                throw;
            }
        }
    }

    public void Dispose()
    {
        foreach (var (localPath, (_, watcher)) in _watchedFiles)
        {
            watcher.Dispose();
            
            try
            {
                if (File.Exists(localPath))
                    File.Delete(localPath);
            }
            catch { }
        }

        _watchedFiles.Clear();
        _lastUploadTimes.Clear();

        _sshService?.Dispose();
        GC.SuppressFinalize(this);
    }
}