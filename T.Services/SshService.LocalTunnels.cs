using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using T.Abstractions;
using T.Models;

namespace T.Services;

public sealed partial class SshService
{
    // New auto-start listeners wait for the previous connection's bound ports to be released.
    private Task _connectionCleanup = Task.CompletedTask;

    public event Action? LocalTunnelsChanged;

    public LocalTunnelStatus GetLocalTunnelStatus(string ruleId) =>
        _connection is { IsClosing: false } connection && connection.LocalTunnels.TryGetValue(ruleId, out var tunnel)
            ? tunnel.Status
            : default;

    public LocalTunnelStatistics GetLocalTunnelStatistics(string ruleId) =>
        _connection is { IsClosing: false } connection && connection.LocalTunnels.TryGetValue(ruleId, out var tunnel)
            ? tunnel.Statistics
            : default;

    public Task StartLocalTunnelAsync(LocalTunnelRule rule)
    {
        lock (_stateLock)
        {
            if (_disposed || _status != ConnectionStatus.Connected || _connection is not { } connection)
                throw new InvalidOperationException("Connect the SSH session before starting the tunnel.");

            var cleanup = _connectionCleanup;
            return Task.Run(() => StartLocalTunnelCoreAsync(connection, cleanup, rule));
        }
    }

    public Task StopLocalTunnelAsync(string ruleId)
    {
        var connection = _connection;
        return connection is null ? Task.CompletedTask : Task.Run(() => StopLocalTunnelCoreAsync(connection, ruleId));
    }

    private async Task StartLocalTunnelCoreAsync(Connection connection, Task cleanup, LocalTunnelRule rule)
    {
        if (rule.SessionId != _session.Id || !rule.HasValidEndpoints)
            throw new ArgumentException("Invalid tunnel rule for this SSH session.", nameof(rule));

        await cleanup;
        await connection.LocalTunnelGate.WaitAsync();
        try
        {
            if (!IsCurrentTunnelConnection(connection))
                throw new InvalidOperationException("The SSH connection was closed before the tunnel could start.");
            if (_sessionStorageService is not ILocalTunnelStore store)
                throw new InvalidOperationException("Tunnels are not supported by the session storage.");
            if (connection.LocalTunnels.TryGetValue(rule.Id, out var existing) && existing.HasListener)
                return; // already listening

            var tunnel = new LocalTunnel();
            connection.LocalTunnels[rule.Id] = tunnel;
            NotifyLocalTunnelsChanged(connection);

            try
            {
                tunnel.Lease = await store.AcquireLocalTunnelAsync(rule, connection.Token);
                if (!IsCurrentTunnelConnection(connection))
                {
                    tunnel.Release(connection.Ssh);
                    return;
                }

                // The relay owns the user's port and counts the traffic; SSH.NET listens behind it.
                int innerPort = TunnelRelay.FindFreeLoopbackPort();
                var relay = new TunnelRelay(rule.LocalPort, innerPort);
                tunnel.Relay = relay;
                relay.Start();

                var listener = new LocalTunnelPort(rule, innerPort);
                tunnel.Listener = listener;
                tunnel.ErrorHandler = (_, e) =>
                {
                    if (tunnel.ReportForwardingError(e.Exception.Message))
                        NotifyLocalTunnelsChanged(connection);
                };
                listener.Exception += tunnel.ErrorHandler;
                connection.Ssh.AddForwardedPort(listener);
                listener.Start();

                if (!IsCurrentTunnelConnection(connection))
                {
                    tunnel.Release(connection.Ssh);
                    return;
                }
                tunnel.SetStatus(new LocalTunnelStatus(LocalTunnelState.Active));
            }
            catch (Exception ex)
            {
                tunnel.Release(connection.Ssh);
                var message = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                    ? $"Local port {rule.LocalEndpoint} is already in use."
                    : ex.Message;
                tunnel.SetStatus(new LocalTunnelStatus(LocalTunnelState.Failed, message));
            }

            NotifyLocalTunnelsChanged(connection);
        }
        finally { connection.LocalTunnelGate.Release(); }
    }

    private async Task StopLocalTunnelCoreAsync(Connection connection, string ruleId)
    {
        await connection.LocalTunnelGate.WaitAsync();
        try
        {
            if (!connection.LocalTunnels.TryGetValue(ruleId, out var tunnel)) return;
            tunnel.SetStatus(new LocalTunnelStatus(LocalTunnelState.Stopping));
            NotifyLocalTunnelsChanged(connection);
            tunnel.Release(connection.Ssh);
            connection.LocalTunnels.TryRemove(ruleId, out _);
            NotifyLocalTunnelsChanged(connection);
        }
        finally { connection.LocalTunnelGate.Release(); }
    }

    private bool IsCurrentTunnelConnection(Connection connection) =>
        !_disposed && !connection.IsClosing && ReferenceEquals(_connection, connection) && connection.Ssh.IsConnected;

    private void NotifyLocalTunnelsChanged(Connection connection)
    {
        if (IsCurrentTunnelConnection(connection))
            LocalTunnelsChanged?.Invoke();
    }

    private sealed partial class Connection
    {
        public SemaphoreSlim LocalTunnelGate { get; } = new(1, 1);
        public ConcurrentDictionary<string, LocalTunnel> LocalTunnels { get; } = new();

        private void DisposeLocalTunnels()
        {
            LocalTunnelGate.Wait();
            try
            {
                foreach (var tunnel in LocalTunnels.Values)
                    tunnel.Release(Ssh);
                LocalTunnels.Clear();
            }
            finally { LocalTunnelGate.Release(); }
        }
    }

    private sealed class LocalTunnel
    {
        private readonly Lock _statusLock = new();
        private LocalTunnelStatus _status = new(LocalTunnelState.Starting);

        public LocalTunnelPort? Listener { get; set; }
        public TunnelRelay? Relay { get; set; }
        public LocalTunnelStatistics Statistics => Relay?.Statistics ?? default;
        public EventHandler<ExceptionEventArgs>? ErrorHandler { get; set; }
        public IDisposable? Lease { get; set; }
        public bool HasListener => Listener is not null;

        public LocalTunnelStatus Status
        {
            get { lock (_statusLock) return _status; }
        }

        public void SetStatus(LocalTunnelStatus status)
        {
            lock (_statusLock) _status = status;
        }

        public bool ReportForwardingError(string message)
        {
            lock (_statusLock)
            {
                if (_status.State != LocalTunnelState.Active) return false;
                _status = new LocalTunnelStatus(LocalTunnelState.Active, $"Forwarding attempt failed: {message}");
                return true;
            }
        }

        public void Release(SshClient ssh)
        {
            // First the user's port and the open client connections, then the SSH listener behind them.
            if (Relay is { } relay)
            {
                Relay = null;
                relay.Dispose();
            }
            if (Listener is { } listener)
            {
                Listener = null;
                listener.Exception -= ErrorHandler;
                try { ssh.RemoveForwardedPort(listener); }
                catch (Exception ex) { Debug.WriteLine($"[SshService] Could not unregister tunnel: {ex.Message}"); }
                try { listener.Dispose(); }
                catch (Exception ex) { Debug.WriteLine($"[SshService] Could not dispose tunnel: {ex.Message}"); }
            }
            Lease?.Dispose();
            Lease = null;
        }
    }

    /// <summary>SSH.NET's forwarding listener on an internal loopback port behind the <see cref="TunnelRelay"/>.</summary>
    private sealed class LocalTunnelPort(LocalTunnelRule rule, int innerPort)
        : ForwardedPortLocal(LocalTunnelRule.BindAddress, (uint)innerPort, rule.TargetHost, (uint)rule.TargetPort)
    {
        // ForwardedPort.Start registers an additional error handler before binding the socket.
        // StartPort registers the local listener's handlers after binding succeeds.
        public override void Start() => StartPort();
    }
}
