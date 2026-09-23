namespace T.Services;

/// <summary>The server host key was not trusted (unknown and rejected, changed or revoked).</summary>
public sealed class SshHostKeyException(string message) : Exception(message);

/// <summary>A private key could not be loaded (missing file, wrong or missing passphrase, unsupported format).</summary>
public sealed class SshPrivateKeyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Connecting through a jump host failed. <see cref="IsPermanent"/> is true when retrying
/// cannot help (configuration or authentication problem) and false for network errors.
/// </summary>
public sealed class SshJumpHostException(string message, bool isPermanent, Exception? inner = null) : Exception(message, inner)
{
    public bool IsPermanent { get; } = isPermanent;
}
