using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
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

    public event Action? TunnelsChanged;

    public TunnelStatus GetTunnelStatus(string ruleId) =>
        _connection is { IsClosing: false } connection && connection.Tunnels.TryGetValue(ruleId, out var tunnel)
            ? tunnel.Status
            : default;

    public TunnelStatistics GetTunnelStatistics(string ruleId) =>
        _connection is { IsClosing: false } connection && connection.Tunnels.TryGetValue(ruleId, out var tunnel)
            ? tunnel.Statistics
            : default;

    public Task StartTunnelAsync(TunnelRule rule)
    {
        lock (_stateLock)
        {
            if (_disposed || _status != ConnectionStatus.Connected || _connection is not { } connection)
                throw new InvalidOperationException("Connect the SSH session before starting the tunnel.");

            var cleanup = _connectionCleanup;
            return Task.Run(() => StartTunnelCoreAsync(connection, cleanup, rule));
        }
    }

    public Task StopTunnelAsync(string ruleId)
    {
        var connection = _connection;
        return connection is null ? Task.CompletedTask : Task.Run(() => StopTunnelCoreAsync(connection, ruleId));
    }

    private async Task StartTunnelCoreAsync(Connection connection, Task cleanup, TunnelRule rule)
    {
        if (rule.SessionId != _session.Id || !rule.HasValidEndpoints)
            throw new ArgumentException("Invalid tunnel rule for this SSH session.", nameof(rule));

        await cleanup;
        await connection.TunnelGate.WaitAsync();
        try
        {
            if (!IsCurrentTunnelConnection(connection))
                throw new InvalidOperationException("The SSH connection was closed before the tunnel could start.");
            if (_sessionStorageService is not ITunnelStore store)
                throw new InvalidOperationException("Tunnels are not supported by the session storage.");
            if (connection.Tunnels.TryGetValue(rule.Id, out var existing) && existing.HasListener)
                return; // already listening

            var tunnel = new Tunnel();
            connection.Tunnels[rule.Id] = tunnel;
            NotifyTunnelsChanged(connection);

            try
            {
                tunnel.Lease = await store.AcquireTunnelAsync(rule, connection.Token);
                if (!IsCurrentTunnelConnection(connection))
                {
                    tunnel.Release(connection.Ssh);
                    return;
                }

                void ReportForwardingError(string message)
                {
                    if (tunnel.ReportForwardingError(message))
                        NotifyTunnelsChanged(connection);
                }

                ForwardedPort listener;
                if (rule.Kind == TunnelKind.Remote)
                {
                    // SSH.NET hands the connections from the server to the relay on an internal port.
                    // The relay connects to the target (resolving its name here) and counts the traffic.
                    var relay = new TunnelRelay(0, rule.TargetHost, rule.TargetPort);
                    tunnel.Relay = relay;
                    relay.TargetConnectFailed += ex => ReportForwardingError($"{rule.TargetEndpoint}: {ex.Message}");
                    relay.Start();
                    listener = new RemoteTunnelPort(rule, relay.Port);
                }
                else
                {
                    // The relay owns the user's port and counts the traffic; SSH.NET listens behind it.
                    int innerPort = TunnelRelay.FindFreeLoopbackPort();
                    var relay = new TunnelRelay(rule.ListenPort, innerPort);
                    tunnel.Relay = relay;
                    relay.Start();
                    listener = rule.Kind == TunnelKind.Dynamic
                        ? new DynamicTunnelPort(innerPort)
                        : new LocalTunnelPort(rule, innerPort);
                }

                tunnel.Listener = listener;
                tunnel.ErrorHandler = (_, e) => ReportForwardingError(e.Exception.Message);
                listener.Exception += tunnel.ErrorHandler;
                connection.Ssh.AddForwardedPort(listener);
                listener.Start();

                if (!IsCurrentTunnelConnection(connection))
                {
                    tunnel.Release(connection.Ssh);
                    return;
                }
                tunnel.SetStatus(new TunnelStatus(TunnelState.Active));
            }
            catch (Exception ex)
            {
                tunnel.Release(connection.Ssh);
                var message = DescribeTunnelStartError(rule, ex);
                tunnel.SetStatus(new TunnelStatus(TunnelState.Failed, message));
            }

            NotifyTunnelsChanged(connection);
        }
        finally { connection.TunnelGate.Release(); }
    }

    private async Task StopTunnelCoreAsync(Connection connection, string ruleId)
    {
        await connection.TunnelGate.WaitAsync();
        try
        {
            if (!connection.Tunnels.TryGetValue(ruleId, out var tunnel)) return;
            tunnel.SetStatus(new TunnelStatus(TunnelState.Stopping));
            NotifyTunnelsChanged(connection);
            tunnel.Release(connection.Ssh);
            connection.Tunnels.TryRemove(ruleId, out _);
            NotifyTunnelsChanged(connection);
        }
        finally { connection.TunnelGate.Release(); }
    }

    /// <summary>What the user sees when the listener of a tunnel could not be started.</summary>
    internal static string DescribeTunnelStartError(TunnelRule rule, Exception ex)
    {
        if (ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } && rule.Kind != TunnelKind.Remote)
            return $"Local port {rule.ListenEndpoint} is already in use.";

        // SSH.NET reports a rejected "tcpip-forward" request without the server's reason.
        if (rule.Kind == TunnelKind.Remote && ex.GetType() == typeof(SshException))
            return $"The server did not open port {rule.ListenEndpoint}: it may be in use there, or the server does not allow remote forwarding.";

        return ex.Message;
    }

    private bool IsCurrentTunnelConnection(Connection connection) =>
        !_disposed && !connection.IsClosing && ReferenceEquals(_connection, connection) && connection.Ssh.IsConnected;

    private void NotifyTunnelsChanged(Connection connection)
    {
        if (IsCurrentTunnelConnection(connection))
            TunnelsChanged?.Invoke();
    }

    private sealed partial class Connection
    {
        public SemaphoreSlim TunnelGate { get; } = new(1, 1);
        public ConcurrentDictionary<string, Tunnel> Tunnels { get; } = new();

        private void DisposeTunnels()
        {
            TunnelGate.Wait();
            try
            {
                foreach (var tunnel in Tunnels.Values)
                    tunnel.Release(Ssh);
                Tunnels.Clear();
            }
            finally { TunnelGate.Release(); }
        }
    }

    private sealed class Tunnel
    {
        private readonly Lock _statusLock = new();
        private TunnelStatus _status = new(TunnelState.Starting);

        public ForwardedPort? Listener { get; set; }
        public TunnelRelay? Relay { get; set; }
        public TunnelStatistics Statistics => Relay?.Statistics ?? default;
        public EventHandler<ExceptionEventArgs>? ErrorHandler { get; set; }
        public IDisposable? Lease { get; set; }
        public bool HasListener => Listener is not null;

        public TunnelStatus Status
        {
            get { lock (_statusLock) return _status; }
        }

        public void SetStatus(TunnelStatus status)
        {
            lock (_statusLock) _status = status;
        }

        public bool ReportForwardingError(string message)
        {
            lock (_statusLock)
            {
                if (_status.State != TunnelState.Active) return false;
                _status = new TunnelStatus(TunnelState.Active, $"Forwarding attempt failed: {message}");
                return true;
            }
        }

        public void Release(SshClient ssh)
        {
            // First the relay with its port and the open connections, then the SSH listener.
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
    private sealed class LocalTunnelPort(TunnelRule rule, int innerPort)
        : ForwardedPortLocal(TunnelRule.BindAddress, (uint)innerPort, rule.TargetHost, (uint)rule.TargetPort)
    {
        // ForwardedPort.Start registers an additional error handler before binding the socket.
        // StartPort registers the local listener's handlers after binding succeeds.
        public override void Start() => StartPort();
    }

    /// <summary>SSH.NET's SOCKS listener on an internal loopback port behind the <see cref="TunnelRelay"/>.</summary>
    private sealed class DynamicTunnelPort(int innerPort) : ForwardedPortDynamic(TunnelRule.BindAddress, (uint)innerPort)
    {
        // See LocalTunnelPort.
        public override void Start() => StartPort();
    }

    /// <summary>
    /// Asks the server to listen on its loopback address and delivers the connections to the
    /// <see cref="TunnelRelay"/> on <paramref name="relayPort"/>. SSH.NET takes the target as an IP
    /// address only; the relay behind it accepts host names.
    /// </summary>
    private sealed class RemoteTunnelPort(TunnelRule rule, int relayPort)
        : ForwardedPortRemote(IPAddress.Parse(TunnelRule.BindAddress), (uint)rule.ListenPort, IPAddress.Loopback, (uint)relayPort)
    {
        // See LocalTunnelPort: a rejected request must not leave a handler on the session.
        public override void Start() => StartPort();
    }
}
