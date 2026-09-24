using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Renci.SshNet;
using Renci.SshNet.Common;
using T.Abstractions;
using T.Models;

namespace T.Services;

/// <summary>
/// One SSH session (shell + SFTP + exec) for a saved <see cref="SshSession"/>, including
/// multi-hop jump hosts, host key verification, interactive authentication and
/// automatic reconnects.
///
/// Threading: events are raised on background threads; consumers marshal to the UI.
/// </summary>
public sealed class SshService : ISshService
{
    private const int MaxAuthAttempts = 3;
    private const int MaxHostKeyPrompts = 3;
    private const int MaxJumpHops = 8;
    private const int ShellBufferSize = 64 * 1024;
    private static readonly TimeSpan OutputBatchDelay = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SftpOperationTimeout = TimeSpan.FromMinutes(2);

    private readonly SshSession _session;
    private readonly ISettingsService _settingsService;
    private readonly IKnownHostsService _knownHostsService;
    private readonly ISessionStorageService _sessionStorageService;
    private readonly IStepCertificateService _stepCertificateService;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Lock _stateLock = new();
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    // Host keys accepted in this app run, in case persisting to known_hosts failed.
    private readonly HashSet<string> _acceptedHostKeys = [];

    // Credentials entered in the login dialog without "save" - never written to the shared session.
    private SshCredentials? _transientCredentials;
    private SshCredentials _credentials;

    private Connection? _connection;
    private CancellationTokenSource? _operationCts;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private bool _reconnecting;
    private volatile bool _disposed;

    // Terminal geometry
    private uint _terminalColumns;
    private uint _terminalRows;
    private uint _pixelWidth;
    private uint _pixelHeight;

    // Output batching: coalesces shell output into ~60 updates per second.
    private readonly StringBuilder _outputBuffer = new();
    private readonly Lock _outputLock = new();
    private readonly Timer _outputTimer;
    private bool _outputFlushScheduled;

    public event Action<string>? ShellDataReceived;
    public event Action<int>? ReconnectAttempt;
    public event Action<ConnectionStatus, string?>? StatusChanged;
    public event Action<TransferInfo>? TransferProgressChanged;
    public event Func<Task<SshCredentials?>>? CredentialsRequired;
    public event Func<string, Task<SshCredentials?>>? AuthenticationFailed;
    public event Func<HostKeyInfo, Task<bool>>? HostKeyVerificationRequired;
    public event Func<AuthPromptRequest, Task<string?>>? AuthPromptRequired;
    public event Action<bool, string>? SftpStatusChanged;

    public bool IsConnected => _connection?.Ssh.IsConnected ?? false;
    public bool IsSftpAvailable => _connection?.Sftp?.IsConnected ?? false;

    public string CurrentDirectory
    {
        get
        {
            try { return _connection?.Sftp is { IsConnected: true } sftp ? sftp.WorkingDirectory : "/"; }
            catch (SshException) { return "/"; }
        }
    }

    public SshService(
        SshSession session,
        ISettingsService settingsService,
        IKnownHostsService knownHostsService,
        ISessionStorageService sessionStorageService,
        IStepCertificateService stepCertificateService,
        uint columns = 120,
        uint rows = 30,
        uint pixelWidth = 960,
        uint pixelHeight = 480)
    {
        _session = session;
        _settingsService = settingsService;
        _knownHostsService = knownHostsService;
        _sessionStorageService = sessionStorageService;
        _stepCertificateService = stepCertificateService;
        _credentials = SshCredentials.FromSession(session);
        _terminalColumns = columns;
        _terminalRows = rows;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;

        _outputTimer = new Timer(_ => FlushOutput());
        _ = Task.Run(InputWriterLoopAsync);
    }

    // ── Connect ──────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cts = BeginOperation(cancellationToken);
        var token = cts.Token;
        try
        {
            TeardownConnection();
            SetStatus(ConnectionStatus.Connecting);
            _credentials = MergeCredentials(SshCredentials.FromSession(_session), _transientCredentials);

            if (string.IsNullOrWhiteSpace(_credentials.Username))
            {
                var entered = CredentialsRequired == null ? null : await CredentialsRequired.Invoke();
                if (entered == null || string.IsNullOrWhiteSpace(entered.Username))
                    throw new OperationCanceledException("Login canceled.");
                ApplyEnteredCredentials(entered);
            }

            Connection connection;
            for (int attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    connection = await EstablishAsync(interactive: true, token);
                    break;
                }
                catch (Exception ex) when (IsCredentialProblem(ex) && attempt < MaxAuthAttempts && AuthenticationFailed != null)
                {
                    var entered = await AuthenticationFailed.Invoke(DescribeError(ex));
                    if (entered == null)
                        throw;
                    ApplyEnteredCredentials(entered);
                }
            }

            // SSH.NET ignores the token during key exchange and authentication, so a cancel
            // can arrive after the connection was established - it must not be leaked.
            if (token.IsCancellationRequested)
            {
                connection.IsClosing = true;
                _ = Task.Run(connection.Dispose, CancellationToken.None);
                token.ThrowIfCancellationRequested();
            }

            Activate(connection);
            SetStatus(ConnectionStatus.Connected);
        }
        catch (OperationCanceledException)
        {
            TeardownConnection();
            SetStatus(ConnectionStatus.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            TeardownConnection();
            SetStatus(ConnectionStatus.Disconnected, DescribeError(ex));
            throw;
        }
        finally
        {
            EndOperation(cts);
        }
    }

    private static SshCredentials MergeCredentials(SshCredentials stored, SshCredentials? entered)
    {
        if (entered == null) return stored;
        return new SshCredentials
        {
            Username = string.IsNullOrWhiteSpace(entered.Username) ? stored.Username : entered.Username,
            Password = string.IsNullOrEmpty(entered.Password) ? stored.Password : entered.Password,
            PrivateKeyPath = string.IsNullOrWhiteSpace(entered.PrivateKeyPath) ? stored.PrivateKeyPath : entered.PrivateKeyPath,
            PrivateKeyPassword = string.IsNullOrEmpty(entered.PrivateKeyPassword) ? stored.PrivateKeyPassword : entered.PrivateKeyPassword,
            StepProfileId = stored.StepProfileId
        };
    }

    private void ApplyEnteredCredentials(SshCredentials entered)
    {
        _transientCredentials = MergeCredentials(_transientCredentials ?? new SshCredentials(), entered);
        _credentials = MergeCredentials(_credentials, entered);
    }

    /// <summary>
    /// Connects the jump host chain (if any) and the target SSH client.
    /// All created resources are owned by the returned connection.
    /// </summary>
    private async Task<Connection> EstablishAsync(bool interactive, CancellationToken token)
    {
        var resources = new List<IDisposable>();
        try
        {
            var chain = await ResolveJumpChainAsync();
            string? viaHost = null;
            var viaPort = 0;

            for (int i = 0; i < chain.Count; i++)
            {
                var hop = chain[i];
                var identity = new HostIdentity(hop.Host, hop.Port, hop.Name);
                EnqueueOutput($"\r\n→ Connecting through jump host {hop.Name} ({hop.Host}:{hop.Port})...\r\n");

                SshClient hopClient;
                try
                {
                    hopClient = await ConnectClientAsync(
                        SshCredentials.FromSession(hop), identity, viaHost ?? hop.Host, viaHost != null ? viaPort : hop.Port,
                        info => new SshClient(info), interactive, resources, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not SshHostKeyException)
                {
                    var permanent = IsCredentialProblem(ex) || ex is StepCertificateException;
                    throw new SshJumpHostException($"Jump host '{hop.Name}' ({hop.Host}:{hop.Port}): {DescribeError(ex)}", permanent, ex);
                }

                hopClient.KeepAliveInterval = KeepAliveInterval;

                var (nextHost, nextPort) = i + 1 < chain.Count
                    ? (chain[i + 1].Host, chain[i + 1].Port)
                    : (_session.Host, _session.Port);

                var forward = new ForwardedPortLocal("127.0.0.1", 0, nextHost, (uint)nextPort);
                hopClient.AddForwardedPort(forward);
                forward.Start();
                resources.Add(forward);

                viaHost = "127.0.0.1";
                viaPort = (int)forward.BoundPort;
            }

            var target = new HostIdentity(_session.Host, _session.Port, _session.Name);
            var host = viaHost ?? _session.Host;
            var port = viaHost != null ? viaPort : _session.Port;

            var ssh = await ConnectClientAsync(_credentials, target, host, port, info => new SshClient(info), interactive, resources, token);
            ssh.KeepAliveInterval = KeepAliveInterval;

            return new Connection(ssh, resources, target, host, port, interactive);
        }
        catch
        {
            SshAuthentication.DisposeAll(resources);
            throw;
        }
    }

    private TimeSpan ConnectTimeout => TimeSpan.FromSeconds(Math.Clamp(_settingsService.Current.General.ConnectionTimeout, 5, 120));
    private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);
    private TimeSpan KeepAliveInterval => TimeSpan.FromSeconds(Math.Clamp(_settingsService.Current.General.KeepAliveInterval, 10, 120));

    /// <summary>Resolves the jump host chain, outermost hop first.</summary>
    private async Task<List<SshSession>> ResolveJumpChainAsync()
    {
        var chain = new List<SshSession>();
        var visited = new HashSet<string> { _session.Id };
        var nextId = _session.ProxyJumpSessionId;

        while (!string.IsNullOrWhiteSpace(nextId))
        {
            if (!visited.Add(nextId))
                throw new SshJumpHostException("The jump host configuration contains a loop.", isPermanent: true);
            if (chain.Count >= MaxJumpHops)
                throw new SshJumpHostException($"Too many jump hosts (maximum {MaxJumpHops}).", isPermanent: true);

            var hop = await _sessionStorageService.GetSessionByIdAsync(nextId)
                ?? throw new SshJumpHostException("The configured jump host no longer exists. Please edit the session.", isPermanent: true);

            chain.Add(hop);
            nextId = hop.ProxyJumpSessionId;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Connects a client with host key verification. Unknown or changed keys are
    /// rejected during the key exchange and the user is asked afterwards - SSH.NET
    /// waits for the key exchange only for <see cref="ConnectionInfo.Timeout"/>, so a
    /// dialog must never block inside the HostKeyReceived callback.
    /// </summary>
    private async Task<T> ConnectClientAsync<T>(
        SshCredentials credentials,
        HostIdentity identity,
        string host,
        int port,
        Func<ConnectionInfo, T> factory,
        bool interactive,
        List<IDisposable> owner,
        CancellationToken token,
        string? purpose = null) where T : BaseClient
    {
        // SFTP (re)connects run in the background, e.g. hours later from the explorer: they
        // use the certificate of the shell connect but never start a (browser) login.
        using var stepCredential = await GetStepCredentialAsync(credentials, allowRenewal: interactive && purpose == null, token);

        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();

            // SSH.NET waits for keyboard-interactive answers (2FA codes) only for
            // ConnectionInfo.Timeout, so interactive connects get a generous value. The TCP
            // connect / protocol exchange still honor the configured timeout via the token.
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectTimeout.CancelAfter(ConnectTimeout);
            using var promptCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            var displayName = purpose == null ? identity.DisplayName : $"{identity.DisplayName} – {purpose}";
            var (info, authResources) = SshAuthentication.Create(
                host, port, displayName, credentials,
                interactive ? InteractiveAuthTimeout : ConnectTimeout,
                _settingsService.Current.General.UseDefaultIdentityFiles,
                stepCredential,
                interactive ? request => AskAuthPrompt(request, rememberPassword: ReferenceEquals(credentials, _credentials)) : null,
                promptCts.Token);

            var client = factory(info);
            HostKeyInfo? rejected = null;
            byte[]? rejectedKey = null;

            void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
            {
                if (IsHostKeyTrusted(identity, e, out var keyInfo))
                {
                    e.CanTrust = true;
                    return;
                }

                e.CanTrust = false;
                rejected = keyInfo;
                rejectedKey = e.HostKey;
            }

            client.HostKeyReceived += OnHostKeyReceived;
            try
            {
                await client.ConnectAsync(connectTimeout.Token);
                info.Timeout = ConnectTimeout; // later channel operations use the normal timeout
                owner.AddRange(authResources);
                owner.Add(client);
                return client;
            }
            catch (Exception) when (rejected != null && !token.IsCancellationRequested)
            {
                DisposeClient(client, authResources);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Our connect timeout, not a user cancel (which would close the tab).
                DisposeClient(client, authResources);
                throw new SshOperationTimeoutException($"Connecting to {host}:{port} timed out.");
            }
            catch
            {
                DisposeClient(client, authResources);
                throw;
            }
            finally
            {
                // Closes a prompt dialog that is still open when the attempt ends.
                TryCancel(promptCts);
            }

            await ConfirmHostKeyAsync(identity, rejected!, rejectedKey!, interactive, attempt);
        }
    }

    /// <summary>
    /// The step-ca certificate of the session (or hop), if it uses one. Only a user initiated
    /// connect may renew it: automatic reconnects must never open a browser login.
    /// </summary>
    private async Task<StepCredential?> GetStepCredentialAsync(SshCredentials credentials, bool allowRenewal, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(credentials.StepProfileId))
            return null;

        return await _stepCertificateService.GetCredentialAsync(
            credentials.StepProfileId,
            allowRenewal,
            line => EnqueueOutput($"\x1b[2m{line}\x1b[0m\r\n"),
            token);
    }

    private static void DisposeClient(BaseClient client, List<IDisposable> resources)
    {
        try { client.Dispose(); } catch (Exception) { /* best effort */ }
        SshAuthentication.DisposeAll(resources);
    }

    private bool IsHostKeyTrusted(HostIdentity identity, HostKeyEventArgs e, out HostKeyInfo? info)
    {
        info = null;
        lock (_acceptedHostKeys)
        {
            if (_acceptedHostKeys.Contains(identity.KeyId(e.HostKey)))
                return true;
        }

        var status = _knownHostsService.CheckHostKey(identity.Host, identity.Port, e.HostKeyName, e.HostKey, out var knownFingerprints);
        if (status == HostKeyStatus.Known)
            return true;

        info = new HostKeyInfo
        {
            Host = identity.Host,
            Port = identity.Port,
            KeyType = KnownHostsService.GetKeyTypeFromBlob(e.HostKey) ?? e.HostKeyName,
            Fingerprint = e.FingerPrintSHA256,
            FingerprintMD5 = e.FingerPrintMD5,
            Status = status,
            KnownFingerprints = knownFingerprints,
            SessionName = identity.Name
        };
        return false;
    }

    private async Task ConfirmHostKeyAsync(HostIdentity identity, HostKeyInfo info, byte[] key, bool interactive, int attempt)
    {
        var target = $"{identity.Host}:{identity.Port}";

        if (info.Status == HostKeyStatus.Revoked)
            throw new SshHostKeyException($"The host key of {target} is marked as @revoked in known_hosts. Connection refused.");

        if (!interactive || HostKeyVerificationRequired == null)
        {
            throw new SshHostKeyException(info.Status == HostKeyStatus.Changed
                ? $"WARNING: the host key of {target} has CHANGED. Automatic reconnect stopped - connect manually to review the new key."
                : $"The host key of {target} is not trusted.");
        }

        if (attempt >= MaxHostKeyPrompts)
            throw new SshHostKeyException($"The host key of {target} keeps changing. Connection refused.");

        if (!await HostKeyVerificationRequired.Invoke(info))
            throw new SshHostKeyException($"The host key of {target} was rejected.");

        lock (_acceptedHostKeys)
            _acceptedHostKeys.Add(identity.KeyId(key));

        try
        {
            _knownHostsService.TrustHostKey(identity.Host, identity.Port, info.KeyType, key, replaceExisting: info.Status == HostKeyStatus.Changed);
        }
        catch (IOException ex)
        {
            EnqueueOutput($"\r\n\x1b[33m⚠ {ex.Message}\x1b[0m\r\n");
        }
    }

    /// <summary>Runs on an SSH.NET worker thread (not the UI thread), so blocking is safe here.</summary>
    private string? AskAuthPrompt(AuthPromptRequest request, bool rememberPassword)
    {
        var handler = AuthPromptRequired;
        if (handler == null) return null;

        string? answer;
        try { answer = handler.Invoke(request).GetAwaiter().GetResult(); }
        catch (Exception) { return null; }

        // A password typed into a PAM prompt is kept for this session (not persisted), so the
        // SFTP connection and automatic reconnects do not have to ask again.
        if (rememberPassword && answer != null && !request.IsEchoed && SshAuthentication.IsPasswordPrompt(request.Prompt))
            ApplyEnteredCredentials(new SshCredentials { Password = answer });

        return answer;
    }

    /// <summary>
    /// Opens the shell on an established connection and makes it the active one.
    /// On failure the connection is disposed (callers only tear down <see cref="_connection"/>).
    /// </summary>
    private void Activate(Connection connection)
    {
        try
        {
            var modes = new Dictionary<TerminalModes, uint> { [TerminalModes.IUTF8] = 1 };
            connection.Shell = connection.Ssh.CreateShellStream(
                "xterm-256color", _terminalColumns, _terminalRows, _pixelWidth, _pixelHeight, ShellBufferSize, modes);
        }
        catch
        {
            connection.IsClosing = true;
            _ = Task.Run(connection.Dispose);
            throw;
        }

        lock (_stateLock)
        {
            if (_disposed)
            {
                connection.IsClosing = true;
                _ = Task.Run(connection.Dispose);
                throw new ObjectDisposedException(nameof(SshService));
            }
            _connection = connection;
        }

        connection.Ssh.ErrorOccurred += (_, e) => HandleConnectionLost(connection, e.Exception);

        // A dedicated reader drains the ShellStream. Only subscribing to DataReceived would
        // let the stream's internal read buffer grow for the whole lifetime of the session.
        var reader = new Thread(() => ShellReadLoop(connection))
        {
            IsBackground = true,
            Name = $"SSH shell reader ({_session.Host})"
        };
        reader.Start();

        _ = ConnectSftpSafeAsync(connection);
    }

    // ── Shell I/O ────────────────────────────────────────────────────────

    private void ShellReadLoop(Connection connection)
    {
        var shell = connection.Shell!;
        var decoder = new UTF8Encoding(false).GetDecoder(); // keeps partial multi-byte sequences between reads
        var bytes = new byte[32 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];

        try
        {
            while (true)
            {
                var read = shell.Read(bytes, 0, bytes.Length);
                if (read == 0) break;

                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count > 0)
                    EnqueueOutput(chars.AsSpan(0, count));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException)
        {
            // Stream closed.
        }

        OnShellEnded(connection);
    }

    private void OnShellEnded(Connection connection)
    {
        if (_disposed || connection.IsClosing || !ReferenceEquals(connection, _connection))
            return;

        // The shell channel closes both when the remote shell exits and when the
        // connection breaks. Give SSH.NET a moment to update the session state.
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            if (connection.IsClosing || !ReferenceEquals(connection, _connection))
                return;

            if (connection.Ssh.IsConnected)
                Disconnect(); // "exit" / "logout"
            else
                HandleConnectionLost(connection, null);
        });
    }

    private void EnqueueOutput(string text) => EnqueueOutput(text.AsSpan());

    private void EnqueueOutput(ReadOnlySpan<char> text)
    {
        lock (_outputLock)
        {
            _outputBuffer.Append(text);
            if (_outputFlushScheduled) return;
            _outputFlushScheduled = true;
        }

        try { _outputTimer.Change(OutputBatchDelay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void FlushOutput()
    {
        string text;
        lock (_outputLock)
        {
            _outputFlushScheduled = false;
            if (_outputBuffer.Length == 0) return;
            text = _outputBuffer.ToString();
            _outputBuffer.Clear();
        }

        ShellDataReceived?.Invoke(text);
    }

    /// <summary>Queues input; a background writer sends it so a slow server never blocks the UI thread.</summary>
    public void SendInput(string input)
    {
        if (!string.IsNullOrEmpty(input) && !_disposed)
            _input.Writer.TryWrite(input);
    }

    private async Task InputWriterLoopAsync()
    {
        var reader = _input.Reader;
        var batch = new StringBuilder();
        try
        {
            while (await reader.WaitToReadAsync(_lifetimeCts.Token))
            {
                batch.Clear();
                while (reader.TryRead(out var text))
                    batch.Append(text);

                var shell = _connection?.Shell;
                if (shell == null) continue;

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(batch.ToString());
                    shell.Write(bytes, 0, bytes.Length);
                    shell.Flush();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException or InvalidOperationException)
                {
                    // Connection is going away; the reconnect logic takes over.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void ResizeTerminal(uint columns, uint rows, uint pixelWidth, uint pixelHeight)
    {
        if (columns == _terminalColumns && rows == _terminalRows && pixelWidth == _pixelWidth && pixelHeight == _pixelHeight)
            return;

        _terminalColumns = columns;
        _terminalRows = rows;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;

        try { _connection?.Shell?.ChangeWindowSize(columns, rows, pixelWidth, pixelHeight); }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or InvalidOperationException) { }
    }

    // ── Disconnect / reconnect ───────────────────────────────────────────

    public void Disconnect()
    {
        if (_disposed) return;

        CancelOperation();
        if (TeardownConnection())
            SetStatus(ConnectionStatus.Disconnecting);
        SetStatus(ConnectionStatus.Disconnected);
    }

    public void CancelReconnect() => Disconnect();

    private void HandleConnectionLost(Connection connection, Exception? error)
    {
        lock (_stateLock)
        {
            if (_disposed || _reconnecting || connection.IsClosing || !ReferenceEquals(connection, _connection))
                return;
            _reconnecting = true;
        }

        Debug.WriteLine($"[SshService] Connection to {_session.Host} lost: {error?.Message}");
        _ = ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        var cts = BeginOperation(CancellationToken.None);
        var token = cts.Token;
        try
        {
            TeardownConnection();
            SetStatus(ConnectionStatus.Reconnecting);
            SftpStatusChanged?.Invoke(false, "Reconnecting...");
            EnqueueOutput("\r\n\x1b[33m⚠ Connection lost - reconnecting...\x1b[0m\r\n");

            for (int attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                ReconnectAttempt?.Invoke(attempt);

                try
                {
                    var connection = await EstablishAsync(interactive: false, token);
                    Activate(connection);
                    EnqueueOutput("\r\n\x1b[32m✓ Reconnected\x1b[0m\r\n");
                    SetStatus(ConnectionStatus.Connected);
                    return;
                }
                catch (Exception ex) when (IsPermanentFailure(ex))
                {
                    SetStatus(ConnectionStatus.Disconnected, DescribeError(ex));
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[SshService] Reconnect attempt {attempt} failed: {ex.Message}");
                }

                // Exponential backoff with jitter: 1s, 2s, 4s ... capped at 30s.
                var delay = TimeSpan.FromSeconds(Math.Min(MaxReconnectDelay.TotalSeconds, Math.Pow(2, Math.Min(attempt - 1, 5))));
                delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
                await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(ConnectionStatus.Disconnected);
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            lock (_stateLock) _reconnecting = false;
            EndOperation(cts);
        }
    }

    private static bool IsCredentialProblem(Exception ex) =>
        ex is SshAuthenticationException or SshPrivateKeyException;

    private static bool IsPermanentFailure(Exception ex) =>
        IsCredentialProblem(ex) || ex is SshHostKeyException or StepCertificateException || ex is SshJumpHostException { IsPermanent: true };

    /// <summary>Detaches and disposes the active connection. Returns true when there was one.</summary>
    private bool TeardownConnection()
    {
        Connection? connection;
        lock (_stateLock)
        {
            connection = _connection;
            _connection = null;
        }

        if (connection == null)
            return false;

        connection.IsClosing = true;
        // Disconnecting sends messages and waits for channel close - keep that off the caller (often the UI thread).
        _ = Task.Run(connection.Dispose);
        return true;
    }

    private CancellationTokenSource BeginOperation(CancellationToken external)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, external);
        CancellationTokenSource? previous;
        lock (_stateLock)
        {
            previous = _operationCts;
            _operationCts = cts;
        }
        TryCancel(previous);
        return cts;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_operationCts, cts))
                _operationCts = null;
        }
        cts.Dispose();
    }

    private void CancelOperation()
    {
        CancellationTokenSource? cts;
        lock (_stateLock) cts = _operationCts;
        TryCancel(cts);
    }

    private static void TryCancel(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void SetStatus(ConnectionStatus status, string? error = null)
    {
        lock (_stateLock)
        {
            if (_status == status) return;
            _status = status;
        }
        StatusChanged?.Invoke(status, error);
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        SshAuthenticationException => $"Authentication failed: {ex.Message}",
        // The message can contain values from settings.json or the CA and is written to the terminal.
        StepCertificateException => $"step-ca: {StepCli.Sanitize(ex.Message)}",
        SshOperationTimeoutException => "The connection timed out.",
        SocketException socket => $"Network error: {socket.Message}",
        SshConnectionException { InnerException: SocketException socket } => $"Network error: {socket.Message}",
        _ => ex.Message
    };

    // ── SFTP ─────────────────────────────────────────────────────────────

    private async Task ConnectSftpSafeAsync(Connection connection)
    {
        try { await ConnectSftpAsync(connection); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task ConnectSftpAsync(Connection connection)
    {
        await connection.SftpLock.WaitAsync(connection.Token);
        try
        {
            if (connection.Sftp?.IsConnected == true)
                return;

            connection.DisposeSftp();
            ReportSftp(connection, false, "Connecting to SFTP...");

            var resources = new List<IDisposable>();
            try
            {
                var sftp = await ConnectClientAsync(
                    _credentials, connection.Identity, connection.Host, connection.Port,
                    info => new SftpClient(info) { OperationTimeout = SftpOperationTimeout, KeepAliveInterval = KeepAliveInterval },
                    connection.Interactive, resources, connection.Token, purpose: "SFTP");

                // The tab may have been closed (or the connection lost) during the handshake.
                if (!connection.TrySetSftp(sftp, resources))
                {
                    SshAuthentication.DisposeAll(resources);
                    return;
                }
                ReportSftp(connection, true, "SFTP available");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SshAuthentication.DisposeAll(resources);
                ReportSftp(connection, false, $"SFTP not available: {DescribeError(ex)}");
            }
        }
        finally
        {
            connection.SftpLock.Release();
        }
    }

    private void ReportSftp(Connection connection, bool available, string message)
    {
        if (!connection.IsClosing && ReferenceEquals(connection, _connection))
            SftpStatusChanged?.Invoke(available, message);
    }

    /// <summary>Returns the connected SFTP client, reconnecting SFTP once if its connection dropped.</summary>
    private async Task<SftpClient> GetSftpAsync()
    {
        var connection = _connection ?? throw new InvalidOperationException("Not connected.");
        if (connection.Sftp is { IsConnected: true } sftp)
            return sftp;

        await ConnectSftpAsync(connection);
        return connection.Sftp is { IsConnected: true } reconnected
            ? reconnected
            : throw new InvalidOperationException("SFTP is not available on this server.");
    }

    private CancellationTokenSource Link(CancellationToken token) =>
        CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, token);

    public async Task<List<RemoteFile>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        using var cts = Link(cancellationToken);
        var sftp = await GetSftpAsync();

        var result = new List<RemoteFile>();
        await foreach (var item in sftp.ListDirectoryAsync(path, cts.Token))
        {
            if (item.Name == ".") continue;
            result.Add(new RemoteFile
            {
                Name = item.Name,
                FullPath = item.FullName,
                IsDirectory = item.IsDirectory,
                IsSymbolicLink = item.IsSymbolicLink,
                Size = item.Length,
                Permissions = GetPermissionsString(item),
                LastModified = item.LastWriteTime
            });
        }

        return result;
    }

    public async Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        using var cts = Link(cancellationToken);
        var sftp = await GetSftpAsync();
        var remote = await sftp.GetAsync(remotePath, cts.Token);

        var info = new TransferInfo
        {
            FileName = Path.GetFileName(localPath),
            RemotePath = remotePath,
            LocalPath = localPath,
            Direction = TransferDirection.Download,
            TotalBytes = remote.Length
        };

        // Download to a temporary file so a failed or canceled transfer never leaves a truncated file.
        var partPath = localPath + ".part";
        try
        {
            await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var progress = new ThrottledProgress<DownloadFileProgressReport>(info, r => (long)r.TotalBytesDownloaded, ReportTransfer);
                await sftp.DownloadFileAsync(remotePath, file, progress, cts.Token);
            }

            File.Move(partPath, localPath, overwrite: true);
            info.TransferredBytes = info.TotalBytes;
            ReportTransfer(info);
        }
        catch
        {
            TryDeleteFile(partPath);
            throw;
        }
    }

    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        using var cts = Link(cancellationToken);
        var sftp = await GetSftpAsync();

        // FileShare.ReadWrite: editors often keep the file open while we upload it.
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        var info = new TransferInfo
        {
            FileName = Path.GetFileName(localPath),
            RemotePath = remotePath,
            LocalPath = localPath,
            Direction = TransferDirection.Upload,
            TotalBytes = file.Length
        };

        var progress = new ThrottledProgress<UploadFileProgressReport>(info, r => (long)r.TotalBytesUploaded, ReportTransfer);
        await sftp.UploadFileAsync(file, remotePath, canOverride: true, progress, cts.Token);
        info.TransferredBytes = info.TotalBytes;
        ReportTransfer(info);
    }

    private void ReportTransfer(TransferInfo info) => TransferProgressChanged?.Invoke(info);

    public async Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
    {
        using var cts = Link(cancellationToken);
        var sftp = await GetSftpAsync();

        if (isDirectory)
            await DeleteDirectoryRecursiveAsync(sftp, path, cts.Token);
        else
            await sftp.DeleteFileAsync(path, cts.Token);
    }

    private static async Task DeleteDirectoryRecursiveAsync(SftpClient sftp, string path, CancellationToken token)
    {
        // Materialize first: deleting while the server enumerates can skip entries.
        var entries = new List<Renci.SshNet.Sftp.ISftpFile>();
        await foreach (var entry in sftp.ListDirectoryAsync(path, token))
        {
            if (entry.Name is not ("." or ".."))
                entries.Add(entry);
        }

        foreach (var entry in entries)
        {
            // Symbolic links are removed, never followed.
            if (entry.IsDirectory && !entry.IsSymbolicLink)
                await DeleteDirectoryRecursiveAsync(sftp, entry.FullName, token);
            else
                await sftp.DeleteFileAsync(entry.FullName, token);
        }

        await sftp.DeleteDirectoryAsync(path, token);
    }

    /// <param name="permissions">rwx digits as decimal-coded octal (e.g. 755).</param>
    public async Task ChangePermissionsAsync(string path, short permissions, CancellationToken cancellationToken = default)
    {
        using var cts = Link(cancellationToken);
        var sftp = await GetSftpAsync();
        var attributes = await sftp.GetAttributesAsync(path, cts.Token);

        // SSH.NET clears setuid/setgid/sticky for 3-digit modes - keep the existing special bits.
        var special = (attributes.IsUIDBitSet ? 4 : 0) | (attributes.IsGroupIDBitSet ? 2 : 0) | (attributes.IsStickyBitSet ? 1 : 0);
        attributes.SetPermissions((short)(special * 1000 + permissions));

        await Task.Run(() => sftp.SetAttributes(path, attributes), cts.Token);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        using var cts = Link(cancellationToken);
        var sftp = await GetSftpAsync();
        await sftp.CreateDirectoryAsync(path, cts.Token);
    }

    public async Task RenameAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        using var cts = Link(cancellationToken);
        var sftp = await GetSftpAsync();
        await sftp.RenameFileAsync(oldPath, newPath, cts.Token);
    }

    public async Task CopyAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default)
    {
        if (_connection?.Ssh is not { IsConnected: true } ssh)
            throw new InvalidOperationException("Not connected.");

        // No command timeout: copying a large directory may take a while (the token cancels it).
        using var cts = Link(cancellationToken);
        using var command = ssh.CreateCommand($"cp -R -p -- {ShellQuote(sourcePath)} {ShellQuote(targetPath)}");
        await command.ExecuteAsync(cts.Token);

        if (command.ExitStatus != 0)
        {
            var error = command.Error?.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(error) ? $"cp failed (exit code {command.ExitStatus})." : error);
        }
    }

    /// <summary>Quotes an argument for a POSIX shell: 'it'\''s' - nothing inside is interpreted.</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public async Task<string?> RunCommandAsync(string commandText, CancellationToken cancellationToken = default)
    {
        if (_connection?.Ssh is not { IsConnected: true } ssh)
            return null;

        using var cts = Link(cancellationToken);
        cts.CancelAfter(CommandTimeout);
        try
        {
            using var command = ssh.CreateCommand(commandText);
            await command.ExecuteAsync(cts.Token);
            return command.ExitStatus == 0 ? command.Result : null;
        }
        catch (Exception ex) when (ex is SshException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string GetPermissionsString(Renci.SshNet.Sftp.ISftpFile file)
    {
        int perm = 0;
        if (file.OwnerCanRead) perm |= 0b100_000_000;
        if (file.OwnerCanWrite) perm |= 0b010_000_000;
        if (file.OwnerCanExecute) perm |= 0b001_000_000;
        if (file.GroupCanRead) perm |= 0b000_100_000;
        if (file.GroupCanWrite) perm |= 0b000_010_000;
        if (file.GroupCanExecute) perm |= 0b000_001_000;
        if (file.OthersCanRead) perm |= 0b000_000_100;
        if (file.OthersCanWrite) perm |= 0b000_000_010;
        if (file.OthersCanExecute) perm |= 0b000_000_001;
        return Convert.ToString(perm, 8).PadLeft(3, '0');
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ── Dispose ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        TryCancel(_lifetimeCts);
        CancelOperation();
        _input.Writer.TryComplete();

        Connection? connection;
        lock (_stateLock)
        {
            connection = _connection;
            _connection = null;
        }
        if (connection != null)
        {
            connection.IsClosing = true;
            _ = Task.Run(connection.Dispose);
        }

        _outputTimer.Dispose();
    }

    // ── Types ────────────────────────────────────────────────────────────

    private sealed record HostIdentity(string Host, int Port, string Name)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : $"{Name} ({Host})";
        public string KeyId(byte[] key) => $"{Host.ToLowerInvariant()}:{Port}:{Convert.ToBase64String(key)}";
    }

    /// <summary>Everything that belongs to one established connection.</summary>
    private sealed class Connection(SshClient ssh, List<IDisposable> resources, HostIdentity identity, string host, int port, bool interactive) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Lock _sftpSync = new();
        private List<IDisposable> _sftpResources = [];

        public SshClient Ssh { get; } = ssh;
        public HostIdentity Identity { get; } = identity;

        /// <summary>Endpoint used for the target (127.0.0.1:port when tunneled through jump hosts).</summary>
        public string Host { get; } = host;
        public int Port { get; } = port;
        public bool Interactive { get; } = interactive;
        public ShellStream? Shell { get; set; }
        public SftpClient? Sftp { get; private set; }
        public SemaphoreSlim SftpLock { get; } = new(1, 1);
        public CancellationToken Token => _cts.Token;
        public volatile bool IsClosing;

        /// <summary>Attaches the SFTP client unless the connection is already being closed.</summary>
        public bool TrySetSftp(SftpClient sftp, List<IDisposable> sftpResources)
        {
            lock (_sftpSync)
            {
                if (IsClosing) return false;
                Sftp = sftp;
                _sftpResources = sftpResources;
                return true;
            }
        }

        public void DisposeSftp()
        {
            SftpClient? sftp;
            List<IDisposable> resources;
            lock (_sftpSync)
            {
                sftp = Sftp;
                resources = _sftpResources;
                Sftp = null;
                _sftpResources = [];
            }

            if (sftp != null)
            {
                try { if (sftp.IsConnected) sftp.Disconnect(); } catch (Exception) { }
            }
            SshAuthentication.DisposeAll(resources);
        }

        public void Dispose()
        {
            lock (_sftpSync) IsClosing = true;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }

            try { Shell?.Dispose(); } catch (Exception) { }
            DisposeSftp();
            try { if (Ssh.IsConnected) Ssh.Disconnect(); } catch (Exception) { }
            SshAuthentication.DisposeAll(resources);
        }
    }

    /// <summary>Forwards transfer progress at most every <see cref="ProgressReportInterval"/> (without capturing a sync context).</summary>
    private sealed class ThrottledProgress<TReport>(TransferInfo info, Func<TReport, long> bytes, Action<TransferInfo> report) : IProgress<TReport>
    {
        private long _lastReport;

        public void Report(TReport value)
        {
            info.TransferredBytes = bytes(value);
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastReport, now) < ProgressReportInterval)
                return;
            _lastReport = now;
            report(info);
        }
    }
}
