using T.Models;

namespace T.Abstractions;

public interface ILocalTunnelConnection
{
    event Action? LocalTunnelsChanged;
    LocalTunnelStatus GetLocalTunnelStatus(string ruleId);

    /// <summary>Byte and connection counters of a running tunnel (zero when it does not run).</summary>
    LocalTunnelStatistics GetLocalTunnelStatistics(string ruleId);

    Task StartLocalTunnelAsync(LocalTunnelRule rule);
    Task StopLocalTunnelAsync(string ruleId);
}
