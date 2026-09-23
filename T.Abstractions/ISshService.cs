using T.Models;

namespace T.Abstractions;

public interface ISshService : IDisposable
{
    bool IsConnected { get; }
    bool IsSftpAvailable { get; }
    string CurrentDirectory { get; }

    /// <summary>Decoded shell output (UTF-8 sequences are never split across events).</summary>
    event Action<string>? ShellDataReceived;
    event Action<int>? ReconnectAttempt;

    /// <summary>
    /// Connection state changes. The string is an error description when the
    /// connection ended because of a failure, and <see langword="null"/> when it
    /// ended normally (user disconnect, shell exited, connect canceled).
    /// </summary>
    event Action<ConnectionStatus, string?>? StatusChanged;
    event Action<TransferInfo>? TransferProgressChanged;
    event Func<Task<SshCredentials?>>? CredentialsRequired;
    event Func<string, Task<SshCredentials?>>? AuthenticationFailed;
    event Func<HostKeyInfo, Task<bool>>? HostKeyVerificationRequired;
    event Func<AuthPromptRequest, Task<string?>>? AuthPromptRequired;

    /// <summary>SFTP availability changed (true = usable) with a human readable message.</summary>
    event Action<bool, string>? SftpStatusChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    void Disconnect();

    /// <summary>Aborts a running connect or reconnect attempt and disconnects.</summary>
    void CancelReconnect();
    void SendInput(string input);
    void ResizeTerminal(uint columns, uint rows, uint pixelWidth, uint pixelHeight);
    Task<List<RemoteFile>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default);
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file or a directory (directories are deleted recursively).</summary>
    Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken = default);
    Task ChangePermissionsAsync(string path, short permissions, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task RenameAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a file or directory on the server (SFTP has no copy operation, so this runs
    /// <c>cp -R -p</c> over SSH). Throws with the server's error message when it fails.
    /// </summary>
    Task CopyAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default);
    Task<string?> RunCommandAsync(string commandText, CancellationToken cancellationToken = default);
}
