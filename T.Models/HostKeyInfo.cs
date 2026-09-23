namespace T.Models;

/// <summary>
/// Result of looking up a server host key in known_hosts.
/// </summary>
public enum HostKeyStatus
{
    /// <summary>The key is recorded for this host.</summary>
    Known,
    /// <summary>No key of this type is recorded for this host (first connection).</summary>
    Unknown,
    /// <summary>A different key of the same type is recorded - possible man-in-the-middle attack.</summary>
    Changed,
    /// <summary>The key is explicitly marked as @revoked.</summary>
    Revoked
}

public class HostKeyInfo
{
    public required string Host { get; init; }
    public int Port { get; init; }
    public required string KeyType { get; init; }
    public required string Fingerprint { get; init; }
    public required string FingerprintMD5 { get; init; }

    /// <summary>Why the user has to decide about this key (never <see cref="HostKeyStatus.Known"/>).</summary>
    public HostKeyStatus Status { get; init; } = HostKeyStatus.Unknown;

    /// <summary>SHA256 fingerprints currently recorded for this host and key type (set when <see cref="Status"/> is Changed).</summary>
    public IReadOnlyList<string> KnownFingerprints { get; init; } = [];

    /// <summary>Display name of the saved session this host belongs to (e.g. a jump host).</summary>
    public string? SessionName { get; init; }
}
