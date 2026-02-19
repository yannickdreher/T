using Polly;
using Polly.Retry;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using T.Models;

namespace T.Services;

public class SshService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SshSession _session;
    private SshClient? _sshClient;
    private SftpClient? _sftpClient;
    private ShellStream? _shellStream;
    private CancellationTokenSource? _reconnectCts;

    // Terminal dimensions
    private uint _terminalColumns;
    private uint _terminalRows;
    private uint _pixelWidth;
    private uint _pixelHeight;

    // Output batching für bessere Performance
    private readonly System.Timers.Timer _outputBatchTimer;
    private readonly StringBuilder _outputBuffer = new();
    private readonly Lock _outputLock = new();

    public event Action<string>? ShellDataReceived;
    public event Action<int>? ReconnectAttempt;
    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<TransferInfo>? TransferProgressChanged;
    public event Func<Task<SshCredentials?>>? CredentialsRequired;
    public event Func<string, Task<SshCredentials?>>? AuthenticationFailed;
    public event Func<HostKeyInfo, Task<bool>>? HostKeyVerificationRequired;
    public event Action<string>? SftpStatusChanged;

    public bool IsConnected => _sshClient?.IsConnected ?? false;
    public bool IsSftpAvailable => _sftpClient?.IsConnected ?? false;
    public string CurrentDirectory => _sftpClient?.WorkingDirectory ?? "/";

    public SshService(SshSession session, uint columns = 120, uint rows = 30, uint pixelWidth = 960, uint pixelHeight = 480)
    {
        _session = session;
        _terminalColumns = columns;
        _terminalRows = rows;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        
        _outputBatchTimer = new System.Timers.Timer(16);
        _outputBatchTimer.Elapsed += FlushOutputBuffer;
        _outputBatchTimer.AutoReset = true;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken == default ? _cts.Token : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken).Token;
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(_session.Username))
        {
            if (CredentialsRequired == null)
            {
                throw new InvalidOperationException("Username is required but no CredentialsRequired handler is registered.");
            }

            var credentials = await CredentialsRequired.Invoke();
            if (credentials == null || string.IsNullOrWhiteSpace(credentials.Username))
            {
                StatusChanged?.Invoke(ConnectionStatus.Disconnected);
                return;
            }

            UpdateSessionCredentials(credentials);
        }

        InitializeClients();

        try
        {
            await ConnectInternalAsync(cancellationToken);
        }
        catch (SshAuthenticationException)
        {
            if (AuthenticationFailed != null)
            {
                var credentials = await AuthenticationFailed.Invoke("Authentication failed. Please check your credentials.");
                
                if (credentials != null)
                {
                    UpdateSessionCredentials(credentials);
                    InitializeClients();
                    await ConnectInternalAsync(cancellationToken);
                }
                else
                {
                    StatusChanged?.Invoke(ConnectionStatus.Disconnected);
                    throw;
                }
            }
            else
            {
                throw;
            }
        }
    }

    private void UpdateSessionCredentials(SshCredentials credentials)
    {
        if (!string.IsNullOrEmpty(credentials.Username))
            _session.Username = credentials.Username;
        if (!string.IsNullOrEmpty(credentials.Password))
            _session.Password = credentials.Password;
        if (!string.IsNullOrEmpty(credentials.PrivateKeyPath))
            _session.PrivateKeyPath = credentials.PrivateKeyPath;
        if (!string.IsNullOrEmpty(credentials.PrivateKeyPassword))
            _session.PrivateKeyPassword = credentials.PrivateKeyPassword;
    }

    private void InitializeClients()
    {
        var settings = SettingsService.Current;
        var connectionInfo = CreateConnectionInfo(_session);
        connectionInfo.Timeout = TimeSpan.FromSeconds(settings.General.ConnectionTimeout);

        _sshClient?.ErrorOccurred -= OnSshClientError;
        _sshClient?.HostKeyReceived -= OnHostKeyReceived;
        _sftpClient?.ErrorOccurred -= OnSftpClientError;
        _sftpClient?.HostKeyReceived -= OnHostKeyReceived;

        _sshClient = new SshClient(connectionInfo);
        _sftpClient = new SftpClient(connectionInfo);
        _sshClient.KeepAliveInterval = TimeSpan.FromSeconds(settings.General.KeepAliveInterval);

        _sshClient.ErrorOccurred += OnSshClientError;
        _sshClient.HostKeyReceived += OnHostKeyReceived;
        _sftpClient.ErrorOccurred += OnSftpClientError;
        _sftpClient.HostKeyReceived += OnHostKeyReceived;
    }

    public void Disconnect()
    {
        StatusChanged?.Invoke(ConnectionStatus.Disconnecting);

        _reconnectCts?.Dispose();

        if (_shellStream != null)
        {
            _shellStream.Closed -= OnShellStreamClosed;
            _shellStream.ErrorOccurred -= OnShellStreamError;
            _shellStream.DataReceived -= OnShellStreamDataRecived;

            try { _shellStream.Close(); } catch { }
            _shellStream.Dispose();
            _shellStream = null;
        }

        if (_sshClient?.IsConnected == true)
        {
            _sshClient.Disconnect();
        }

        if (_sftpClient?.IsConnected == true)
        {
            _sftpClient.Disconnect();
        }

        StatusChanged?.Invoke(ConnectionStatus.Disconnected);
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_session == null || _sshClient == null || _sftpClient == null) return;

        try
        {
            StatusChanged?.Invoke(ConnectionStatus.Connecting);

            // SSH Verbindung (Terminal) - essentiell
            await _sshClient.ConnectAsync(cancellationToken);

            _shellStream?.Closed -= OnShellStreamClosed;
            _shellStream?.ErrorOccurred -= OnShellStreamError;
            _shellStream?.DataReceived -= OnShellStreamDataRecived;

            _shellStream?.Close();
            _shellStream?.Dispose();

            _shellStream = _sshClient.CreateShellStream(
                terminalName: "xterm-256color",
                columns: _terminalColumns,
                rows: _terminalRows,
                width: _pixelWidth,
                height: _pixelHeight,
                bufferSize: 65536);

            _shellStream.Closed += OnShellStreamClosed;
            _shellStream.ErrorOccurred += OnShellStreamError;
            _shellStream.DataReceived += OnShellStreamDataRecived;

            StatusChanged?.Invoke(ConnectionStatus.Connected);

            _ = Task.Run(async () =>
            {
                try
                {
                    SftpStatusChanged?.Invoke("Connecting to SFTP...");
                    await _sftpClient.ConnectAsync(cancellationToken);
                    SftpStatusChanged?.Invoke("SFTP available");
                }
                catch (SshException ex)
                {
                    SftpStatusChanged?.Invoke($"SFTP not available: {ex.Message}");
                }
                catch (Exception ex)
                {
                    SftpStatusChanged?.Invoke($"SFTP not available: {ex.Message}");
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ShellDataReceived?.Invoke("\r\n\u26A0 Connection attempt canceled.\r\n");
            throw;
        }
    }

    private void OnSshClientError(object? sender, ExceptionEventArgs e)
    {
    }

    private void OnSftpClientError(object? sender, ExceptionEventArgs e)
    {
    }

    private void OnShellStreamClosed(object? sender, EventArgs e)
    {
        Disconnect();
    }

    private void OnShellStreamDataRecived(object? sender, ShellDataEventArgs e)
    {
        var text = Encoding.UTF8.GetString(e.Data);
        
        lock (_outputLock)
        {
            _outputBuffer.Append(text);
            
            if (!_outputBatchTimer.Enabled)
            {
                _outputBatchTimer.Start();
            }
        }
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        if (KnownHostsService.IsHostKeyKnown(_session.Host, _session.Port, e.HostKey))
        {
            e.CanTrust = true;
            return;
        }

        if (HostKeyVerificationRequired != null)
        {
            var hostKeyInfo = new HostKeyInfo
            {
                Host = _session.Host,
                Port = _session.Port,
                KeyType = e.HostKeyName,
                Fingerprint = e.FingerPrintSHA256,
                FingerprintMD5 = e.FingerPrintMD5
            };

            var trusted = HostKeyVerificationRequired.Invoke(hostKeyInfo).GetAwaiter().GetResult();
            e.CanTrust = trusted;

            if (trusted)
            {
                KnownHostsService.AddHostKey(_session.Host, _session.Port, e.HostKeyName, e.HostKey);
            }
        }
        else
        {
            e.CanTrust = false;
        }
    }

    private void FlushOutputBuffer(object? sender, System.Timers.ElapsedEventArgs? e)
    {
        string buffered;
        lock (_outputLock)
        {
            if (_outputBuffer.Length == 0)
            {
                _outputBatchTimer.Stop();
                return;
            }
            
            buffered = _outputBuffer.ToString();
            _outputBuffer.Clear();
        }
        
        ShellDataReceived?.Invoke(buffered);
    }

    private async void OnShellStreamError(object? sender, ExceptionEventArgs e)
    {
        ReconnectAsync();
    }

    private async void ReconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken == default ? _cts.Token : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken).Token;
        
        if (_session == null) return;

        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_reconnectCts.Token, cancellationToken);

        StatusChanged?.Invoke(ConnectionStatus.Reconnecting);

        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = int.MaxValue,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.FromSeconds(3),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => 
                {
                    if (ex is SshAuthenticationException)
                        return false;
                    
                    if (ex is OperationCanceledException)
                        return false;
                    
                    if (ex is InvalidOperationException ioe && ioe.Message.Contains("Credentials are required"))
                        return false;
                    
                    return true;
                }),
                OnRetry = args =>
                {
                    if (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            ReconnectAttempt?.Invoke(args.AttemptNumber + 1);
                        }
                        catch { }
                    }
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

        try
        {
            await pipeline.ExecuteAsync(async cancellationToken =>
            {
                InitializeClients();
                await ConnectInternalAsync(cancellationToken);
            }, cts.Token);
        }
        catch (SshAuthenticationException)
        {
            StatusChanged?.Invoke(ConnectionStatus.Disconnected);
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke(ConnectionStatus.Disconnected);
        }
        catch
        {
            StatusChanged?.Invoke(ConnectionStatus.Disconnected);
        }
        finally
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        Disconnect();
    }

    public void SendInput(string input)
    {
        if (_shellStream == null || !IsConnected) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            _shellStream.Write(bytes, 0, bytes.Length);
            _shellStream.Flush();
        }
        catch { }
    }

    public void ResizeTerminal(uint columns, uint rows, uint pixelWidth, uint pixelHeight)
    {
        if (_shellStream == null) return;
        if (columns == _terminalColumns && rows == _terminalRows) return;

        _terminalColumns = columns;
        _terminalRows = rows;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;

        try
        {
            _shellStream.ChangeWindowSize(columns, rows, pixelWidth, pixelHeight);
        }
        catch { }
    }

    public async Task<List<RemoteFile>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken == default ? _cts.Token : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken).Token;
        cancellationToken.ThrowIfCancellationRequested();

        if (_sftpClient == null || !_sftpClient.IsConnected) return [];

        var items = _sftpClient.ListDirectoryAsync(path, cancellationToken);

        var result = new List<RemoteFile>();
        await foreach (var item in items.WithCancellation(cancellationToken))
        {
            if (item.Name != ".")
            {
                result.Add(new RemoteFile
                {
                    Name = item.Name,
                    FullPath = item.FullName,
                    IsDirectory = item.IsDirectory,
                    Size = item.Length,
                    Permissions = GetPermissionsString(item.Attributes),
                    LastModified = item.LastWriteTime
                });
            }
        }

        return [.. result
            .OrderByDescending(f => f.IsDirectory)
            .ThenBy(f => f.Name)];
    }

    public async Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken == default ? _cts.Token : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken).Token;
        cancellationToken.ThrowIfCancellationRequested();

        if (_sftpClient == null || !_sftpClient.IsConnected) return;

        var fileInfo = _sftpClient.Get(remotePath);
        var info = new TransferInfo
        {
            FileName = Path.GetFileName(remotePath),
            RemotePath = remotePath,
            LocalPath = localPath,
            Direction = TransferDirection.Download,
            TotalBytes = fileInfo.Length
        };

        await using var localFile = File.Create(localPath);

        await Task.Run(() =>
        {
            _sftpClient.DownloadFile(remotePath, localFile, downloaded =>
            {
                info.TransferredBytes = (long)downloaded;
                TransferProgressChanged?.Invoke(info);
            });
        }, cancellationToken);
    }

    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken == default ? _cts.Token : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken).Token;
        cancellationToken.ThrowIfCancellationRequested();

        if (_sftpClient == null || !_sftpClient.IsConnected) return;

        await using var localFile = File.OpenRead(localPath);
        var info = new TransferInfo
        {
            FileName = Path.GetFileName(localPath),
            RemotePath = remotePath,
            LocalPath = localPath,
            Direction = TransferDirection.Upload,
            TotalBytes = localFile.Length
        };

        await Task.Run(() =>
        {
            _sftpClient.UploadFile(localFile, remotePath, uploaded =>
            {
                info.TransferredBytes = (long)uploaded;
                TransferProgressChanged?.Invoke(info);
            });
        }, cancellationToken);
    }

    public async Task DeleteAsync(string path, bool isDirectory)
    {
        if (_sftpClient == null) return;
        await Task.Run(() =>
        {
            if (isDirectory) _sftpClient.DeleteDirectory(path);
            else _sftpClient.DeleteFile(path);
        });
    }

    public async Task ChangePermissionsAsync(string path, short permissions)
    {
        if (_sftpClient == null) return;
        await Task.Run(() =>
        {
            var attrs = _sftpClient.GetAttributes(path);
            attrs.SetPermissions(permissions);
            _sftpClient.SetAttributes(path, attrs);
        });
    }

    private static ConnectionInfo CreateConnectionInfo(SshSession session)
    {
        var username = string.IsNullOrWhiteSpace(session.Username) 
            ? "anonymous" 
            : session.Username;

        var authMethods = new List<AuthenticationMethod>();
        
        if (!string.IsNullOrEmpty(session.PrivateKeyPath) && File.Exists(session.PrivateKeyPath))
        {
            try
            {
                PrivateKeyFile keyFile;
                
                if (!string.IsNullOrEmpty(session.PrivateKeyPassword))
                {
                    keyFile = new PrivateKeyFile(session.PrivateKeyPath, session.PrivateKeyPassword);
                }
                else
                {
                    keyFile = new PrivateKeyFile(session.PrivateKeyPath);
                }
                
                authMethods.Add(new PrivateKeyAuthenticationMethod(username, keyFile));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load private key: {ex.Message}", ex);
            }
        }

        if (!string.IsNullOrEmpty(session.Password))
        {
            authMethods.Add(new PasswordAuthenticationMethod(username, session.Password));
        }

        authMethods.Add(new KeyboardInteractiveAuthenticationMethod(username));
        authMethods.Add(new NoneAuthenticationMethod(username));

        return new ConnectionInfo(session.Host, session.Port, username, [.. authMethods]);
    }

    private static string GetPermissionsString(SftpFileAttributes attrs)
    {
        int perm = 0;
        if (attrs.OwnerCanRead) perm |= 0b100_000_000;
        if (attrs.OwnerCanWrite) perm |= 0b010_000_000;
        if (attrs.OwnerCanExecute) perm |= 0b001_000_000;
        if (attrs.GroupCanRead) perm |= 0b000_100_000;
        if (attrs.GroupCanWrite) perm |= 0b000_010_000;
        if (attrs.GroupCanExecute) perm |= 0b000_001_000;
        if (attrs.OthersCanRead) perm |= 0b000_000_100;
        if (attrs.OthersCanWrite) perm |= 0b000_000_010;
        if (attrs.OthersCanExecute) perm |= 0b000_000_001;
        return Convert.ToString(perm, 8).PadLeft(3, '0');
    }

    public void Dispose()
    {
        _outputBatchTimer?.Stop();
        _outputBatchTimer?.Dispose();
        
        // Letzten Output noch senden
        FlushOutputBuffer(null, null!);
        
        _cts.Cancel();
        Disconnect();
        
        if (_sshClient != null)
        {
            _sshClient.ErrorOccurred -= OnSshClientError;
            _sshClient.Dispose();
        }

        if (_sftpClient != null)
        {
            _sftpClient.ErrorOccurred -= OnSftpClientError;
            _sftpClient.Dispose();
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}