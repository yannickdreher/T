using T.Models;

namespace T.Abstractions;

public interface ITunnelConnection
{
    event Action? TunnelsChanged;
    TunnelStatus GetTunnelStatus(string ruleId);

    /// <summary>Byte and connection counters of a running tunnel (zero when it does not run).</summary>
    TunnelStatistics GetTunnelStatistics(string ruleId);

    Task StartTunnelAsync(TunnelRule rule);
    Task StopTunnelAsync(string ruleId);
}
