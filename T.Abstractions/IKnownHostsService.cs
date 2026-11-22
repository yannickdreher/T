using T.Models;

namespace T.Abstractions;

/// <summary>
/// Access to the OpenSSH known_hosts file (~/.ssh/known_hosts), shared with the
/// system's OpenSSH client. Supports plain and hashed (HashKnownHosts) entries,
/// non-standard ports ([host]:port), wildcards and @revoked markers.
/// </summary>
public interface IKnownHostsService
{
    /// <summary>Checks a server host key against known_hosts.</summary>
    /// <param name="knownFingerprints">SHA256 fingerprints recorded for the same host and key type (useful for a "key changed" warning).</param>
    HostKeyStatus CheckHostKey(string host, int port, string keyType, byte[] hostKey, out IReadOnlyList<string> knownFingerprints);

    /// <summary>
    /// Trusts a host key. With <paramref name="replaceExisting"/> all entries of the same
    /// key type for this host are removed first (used after a confirmed key change).
    /// </summary>
    void TrustHostKey(string host, int port, string keyType, byte[] hostKey, bool replaceExisting);

    /// <summary>Removes all non-hashed and hashed entries for the host.</summary>
    void RemoveHost(string host, int port);
}
