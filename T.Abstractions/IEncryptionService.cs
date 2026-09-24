namespace T.Abstractions;

/// <summary>
/// Protects secrets (passwords, key passphrases) at rest.
/// </summary>
public interface IEncryptionService
{
    string Encrypt(string plainText);

    /// <summary>Decrypts a value. Returns an empty string when a current-format value cannot be decrypted.</summary>
    string Decrypt(string encryptedText);

    /// <summary>
    /// Encrypts binary secrets (e.g. private keys) without an intermediate string copy.
    /// <paramref name="associatedData"/> is authenticated but not stored: decryption only
    /// succeeds with the same value (binds the secret to its context).
    /// </summary>
    string EncryptBytes(ReadOnlySpan<byte> plainBytes, ReadOnlySpan<byte> associatedData = default);

    /// <summary>Decrypts a value written by <see cref="EncryptBytes"/>; null when it cannot be decrypted (tampered, other key or context).</summary>
    byte[]? DecryptBytes(string encryptedText, ReadOnlySpan<byte> associatedData = default);

    /// <summary>True when the value was not produced by the current encryption format and should be re-encrypted.</summary>
    bool NeedsMigration(string storedValue);
}
