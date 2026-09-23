namespace T.Abstractions;

/// <summary>
/// Protects secrets (passwords, key passphrases) at rest.
/// </summary>
public interface IEncryptionService
{
    string Encrypt(string plainText);

    /// <summary>Decrypts a value. Returns an empty string when a current-format value cannot be decrypted.</summary>
    string Decrypt(string encryptedText);

    /// <summary>True when the value was not produced by the current encryption format and should be re-encrypted.</summary>
    bool NeedsMigration(string storedValue);
}
