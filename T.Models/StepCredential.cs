using System.Security.Cryptography;

namespace T.Models;

/// <summary>
/// A private key (PEM) with its step-ca SSH user certificate, decrypted for one connection
/// attempt. The key exists unencrypted only in memory; dispose the instance to wipe this copy.
/// </summary>
public sealed class StepCredential(byte[] privateKeyPem, string certificate, DateTimeOffset validBefore) : IDisposable
{
    public byte[] PrivateKeyPem { get; } = privateKeyPem;

    /// <summary>OpenSSH certificate line ("ecdsa-sha2-nistp256-cert-v01@openssh.com AAAA...").</summary>
    public string Certificate { get; } = certificate;

    public DateTimeOffset ValidBefore { get; } = validBefore;

    public void Dispose() => CryptographicOperations.ZeroMemory(PrivateKeyPem);
}
