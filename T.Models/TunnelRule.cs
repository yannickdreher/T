using System.Net;

namespace T.Models;

/// <summary>Direction of a port forwarding.</summary>
public enum TunnelKind
{
    /// <summary><c>ssh -L</c>: listener on this computer, target as seen from the SSH server.</summary>
    Local,

    /// <summary><c>ssh -R</c>: listener on the SSH server, target as seen from this computer.</summary>
    Remote,

    /// <summary><c>ssh -D</c>: SOCKS proxy on this computer; every connection names its own target.</summary>
    Dynamic
}

/// <summary>
/// A saved port forwarding. <paramref name="ListenPort"/> is the port of the listener: on this
/// computer for local and dynamic rules, on the SSH server for remote rules. Dynamic rules have no
/// fixed target (<paramref name="TargetHost"/> is empty and <paramref name="TargetPort"/> is 0).
/// </summary>
public sealed record TunnelRule(
    string Id,
    string SessionId,
    string Name,
    TunnelKind Kind,
    int ListenPort,
    string TargetHost,
    int TargetPort,
    bool StartOnConnect)
{
    /// <summary>
    /// Loopback: the address local listeners bind to, and the address remote rules request on the
    /// server. A server with <c>GatewayPorts yes</c> may bind a wildcard address instead.
    /// </summary>
    public const string BindAddress = "127.0.0.1";

    public string ListenEndpoint => $"{BindAddress}:{ListenPort}";
    public bool HasTarget => Kind != TunnelKind.Dynamic;

    /// <summary>"host:port" ("[::1]:80" for IPv6 addresses); empty for a rule without a target.</summary>
    public string TargetEndpoint =>
        !HasTarget ? "" : TargetHost.Contains(':') ? $"[{TargetHost}]:{TargetPort}" : $"{TargetHost}:{TargetPort}";

    /// <summary>
    /// Listener and target as shown in lists: "127.0.0.1:8080 → db.internal:5432",
    /// "server 127.0.0.1:8080 (requested) → localhost:3000" or "127.0.0.1:1080 → SOCKS proxy".
    /// </summary>
    public string Route => Kind switch
    {
        // The server decides what it binds; the client only knows what it asked for.
        TunnelKind.Remote => $"server {ListenEndpoint} (requested) → {TargetEndpoint}",
        TunnelKind.Dynamic => $"{ListenEndpoint} → SOCKS proxy",
        _ => $"{ListenEndpoint} → {TargetEndpoint}"
    };

    public bool HasValidEndpoints => EndpointError is null;

    /// <summary>A message for the first invalid listener or target value, or null.</summary>
    public string? EndpointError
    {
        get
        {
            if (!Enum.IsDefined(Kind))
                return "Choose the type of the tunnel.";
            if (!IsValidPort(ListenPort))
                return Kind == TunnelKind.Remote
                    ? "The server port must be between 1 and 65535."
                    : "The local port must be between 1 and 65535.";
            if (Kind == TunnelKind.Dynamic)
                return TargetHost.Length == 0 && TargetPort == 0 ? null : "A dynamic tunnel has no fixed target.";
            if (!IsValidPort(TargetPort))
                return "The target port must be between 1 and 65535.";
            if (!IsValidTarget(TargetHost))
                return Kind == TunnelKind.Remote
                    ? "Enter the target as a host name or IP address, as seen from this computer."
                    : "Enter the target as a host name or IP address, as seen from the SSH server.";
            return null;
        }
    }

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    private static bool IsValidTarget(string host) =>
        !string.IsNullOrWhiteSpace(host)
        && !host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '[' or ']' or '%' or '/')
        && (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns);
}

public enum TunnelState
{
    Inactive,
    Starting,
    Active,
    Stopping,
    Failed
}

public readonly record struct TunnelStatus(TunnelState State, string? Error = null);

/// <summary>
/// Traffic of a running tunnel since it was started. "Sent" is data from the side that opens the
/// connections (the client of the listener) to the target, "received" is the target's answer.
/// </summary>
public readonly record struct TunnelStatistics(long BytesReceived, long BytesSent, int OpenConnections, long TotalConnections);
