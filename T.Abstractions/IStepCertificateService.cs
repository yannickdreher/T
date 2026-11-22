using T.Models;

namespace T.Abstractions;

/// <summary>
/// Short-lived SSH user certificates from a step-ca (see <see cref="StepCaProfile"/>).
/// The key pair is generated in-process and only its public key is handed to the step CLI
/// for signing; the private key is stored encrypted and never passed to another process.
/// </summary>
public interface IStepCertificateService
{
    /// <summary>
    /// Returns the key pair and certificate of the profile. When the stored certificate is
    /// missing or about to expire and <paramref name="allowRenewal"/> is true, a new key pair
    /// is signed by the CA (the step CLI may open the browser for an OIDC login). Concurrent
    /// callers share one renewal. Failures throw an exception with a displayable message.
    /// </summary>
    /// <param name="progress">Receives sanitized status lines (safe to write to a terminal).</param>
    Task<StepCredential> GetCredentialAsync(string profileId, bool allowRenewal, Action<string>? progress, CancellationToken cancellationToken);

    /// <summary>End of validity of the stored certificate, or null when none matches the profile.</summary>
    DateTimeOffset? GetValidUntil(StepCaProfile profile);

    /// <summary>Deletes the stored key pair and certificate of the profile.</summary>
    void Forget(string profileId);

    /// <summary>
    /// Resolves the step CLI: <paramref name="configuredPath"/> when set, otherwise the absolute
    /// directories in PATH. Returns null when it was not found or the path is not acceptable.
    /// </summary>
    string? FindExecutable(string? configuredPath);
}
