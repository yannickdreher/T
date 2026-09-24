namespace T.Services;

/// <summary>The server host key was not trusted (unknown and rejected, changed or revoked).</summary>
public sealed class SshHostKeyException(string message) : Exception(message);

/// <summary>A private key could not be loaded (missing file, wrong or missing passphrase, unsupported format).</summary>
public sealed class SshPrivateKeyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// No usable step-ca certificate: invalid profile, step CLI missing or failed, rejected
/// certificate, or expired during an automatic reconnect. Retrying automatically cannot help.
/// The message is sanitized: it can contain values from settings.json, step or the CA and is
/// written to the terminal.
/// </summary>
public sealed class StepCertificateException(string message, Exception? inner = null) : Exception(StepCli.Sanitize(message, 2000), inner);

/// <summary>
/// Connecting through a jump host failed. <see cref="IsPermanent"/> is true when retrying
/// cannot help (configuration or authentication problem) and false for network errors.
/// </summary>
public sealed class SshJumpHostException(string message, bool isPermanent, Exception? inner = null) : Exception(message, inner)
{
    public bool IsPermanent { get; } = isPermanent;
}
