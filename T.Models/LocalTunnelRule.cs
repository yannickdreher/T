using System.Net;

namespace T.Models;

public sealed record LocalTunnelRule(
    string Id,
    string SessionId,
    string Name,
    int LocalPort,
    string TargetHost,
    int TargetPort,
    bool StartOnConnect)
{
    public const string BindAddress = "127.0.0.1";
    public string LocalEndpoint => $"{BindAddress}:{LocalPort}";
    public string TargetEndpoint => TargetHost.Contains(':') ? $"[{TargetHost}]:{TargetPort}" : $"{TargetHost}:{TargetPort}";

    public bool HasValidEndpoints =>
        IsValidPort(LocalPort) && IsValidPort(TargetPort) && IsValidTarget(TargetHost);

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    private static bool IsValidTarget(string host) =>
        !string.IsNullOrWhiteSpace(host)
        && !host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '[' or ']' or '%' or '/')
        && (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns);
}

public enum LocalTunnelState
{
    Inactive,
    Starting,
    Active,
    Stopping,
    Failed
}

public readonly record struct LocalTunnelStatus(LocalTunnelState State, string? Error = null);

/// <summary>
/// Traffic of a running tunnel since it was started. "Received" is data from the target to the
/// local client, "sent" from the local client to the target.
/// </summary>
public readonly record struct LocalTunnelStatistics(long BytesReceived, long BytesSent, int OpenConnections, long TotalConnections);
