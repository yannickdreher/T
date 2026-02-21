using T.Models;

namespace T.Abstractions;

public interface ISshService : IDisposable
{
    bool IsConnected { get; }
    bool IsSftpAvailable { get; }
    string CurrentDirectory { get; }

    event Action<string>? ShellDataReceived;
    event Action<int>? ReconnectAttempt;
    event Action<ConnectionStatus>? StatusChanged;
    event Action<TransferInfo>? TransferProgressChanged;
    event Func<Task<SshCredentials?>>? CredentialsRequired;
    event Func<string, Task<SshCredentials?>>? AuthenticationFailed;
    event Func<HostKeyInfo, Task<bool>>? HostKeyVerificationRequired;
    event Action<string>? SftpStatusChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    void Disconnect();
    void CancelReconnect();
    void SendInput(string input);
    void ResizeTerminal(uint columns, uint rows, uint pixelWidth, uint pixelHeight);
    Task<List<RemoteFile>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default);
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, bool isDirectory);
    Task ChangePermissionsAsync(string path, short permissions);
}