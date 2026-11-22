using T.Models;

namespace T.Abstractions;

/// <summary>
/// Saved local port forwardings. Each rule refers to the terminal profile it connects through
/// (<see cref="TunnelRule.SessionId"/>); deleting the profile deletes its rules.
/// </summary>
public interface ITunnelStore
{
    Task<List<TunnelRule>> LoadAllTunnelsAsync();
    Task<List<TunnelRule>> LoadTunnelsAsync(string sessionId);
    Task SaveTunnelAsync(TunnelRule rule, bool creating);
    Task DeleteTunnelAsync(TunnelRule rule);

    /// <summary>Prevents changes to a saved rule until the listener has been released.</summary>
    Task<IDisposable> AcquireTunnelAsync(TunnelRule rule, CancellationToken cancellationToken);
}
