using T.Models;

namespace T.Abstractions;

/// <summary>
/// Saved local port forwardings. Each rule refers to the terminal profile it connects through
/// (<see cref="LocalTunnelRule.SessionId"/>); deleting the profile deletes its rules.
/// </summary>
public interface ILocalTunnelStore
{
    Task<List<LocalTunnelRule>> LoadAllLocalTunnelsAsync();
    Task<List<LocalTunnelRule>> LoadLocalTunnelsAsync(string sessionId);
    Task SaveLocalTunnelAsync(LocalTunnelRule rule, bool creating);
    Task DeleteLocalTunnelAsync(LocalTunnelRule rule);

    /// <summary>Prevents changes to a saved rule until the listener has been released.</summary>
    Task<IDisposable> AcquireLocalTunnelAsync(LocalTunnelRule rule, CancellationToken cancellationToken);
}
