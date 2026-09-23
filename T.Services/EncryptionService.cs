using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using T.Abstractions;

namespace T.Services;

/// <summary>
/// Encrypts secrets at rest with AES-256-GCM (random nonce per value, authenticated).
/// The 256-bit master key is random and stored in the app data folder:
/// protected with DPAPI (current user) on Windows, and as a 0600 file elsewhere.
///
/// Values written by the previous implementation (AES-CBC with a key derived from
/// machine/user name and a fixed IV) are still readable so they can be migrated.
/// </summary>
public sealed class EncryptionService : IEncryptionService
{
    private const string Prefix = "v2:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private static readonly byte[] DpapiEntropy = "T-SSH master key v2"u8.ToArray();

    private readonly byte[] _masterKey;
    private readonly string _appFolder;
    private readonly Lazy<(byte[] Key, byte[] IV)?> _legacyKey;

    public EncryptionService()
        : this(AppPaths.DataDirectory)
    {
    }

    public EncryptionService(string appFolder)
    {
        _appFolder = appFolder;
        Directory.CreateDirectory(appFolder);
        _masterKey = LoadOrCreateMasterKey(Path.Combine(appFolder, "master.key"));
        _legacyKey = new Lazy<(byte[], byte[])?>(LoadLegacyKey);
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var output = new byte[NonceSize + plainBytes.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        var cipher = output.AsSpan(NonceSize, plainBytes.Length);
        var tag = output.AsSpan(NonceSize + plainBytes.Length, TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_masterKey, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        CryptographicOperations.ZeroMemory(plainBytes);

        return Prefix + Convert.ToBase64String(output);
    }

    public string Decrypt(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText))
            return string.Empty;

        if (!encryptedText.StartsWith(Prefix, StringComparison.Ordinal))
            return DecryptLegacy(encryptedText);

        try
        {
            var data = Convert.FromBase64String(encryptedText[Prefix.Length..]);
            if (data.Length < NonceSize + TagSize)
                return string.Empty;

            var cipherLength = data.Length - NonceSize - TagSize;
            var plain = new byte[cipherLength];
            using var aes = new AesGcm(_masterKey, TagSize);
            aes.Decrypt(
                data.AsSpan(0, NonceSize),
                data.AsSpan(NonceSize, cipherLength),
                data.AsSpan(NonceSize + cipherLength, TagSize),
                plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Never hand ciphertext to the SSH server as a password.
            Debug.WriteLine($"[EncryptionService] Could not decrypt stored secret: {ex.Message}");
            return string.Empty;
        }
    }

    public bool NeedsMigration(string storedValue) =>
        !string.IsNullOrEmpty(storedValue) && !storedValue.StartsWith(Prefix, StringComparison.Ordinal);

    // ── Master key ───────────────────────────────────────────────────────

    private static byte[] LoadOrCreateMasterKey(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var stored = File.ReadAllBytes(path);
                var key = OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(stored, DpapiEntropy, DataProtectionScope.CurrentUser)
                    : stored;
                if (key.Length == KeySize)
                    return key;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[EncryptionService] Master key unreadable: {ex.Message}");
            }

            // Keep the unreadable key for manual recovery instead of silently overwriting it.
            try { File.Move(path, $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}"); }
            catch (IOException) { }
        }

        var newKey = RandomNumberGenerator.GetBytes(KeySize);
        var toStore = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(newKey, DpapiEntropy, DataProtectionScope.CurrentUser)
            : newKey;

        var tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, toStore);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tempPath, path, overwrite: true);

        if (OperatingSystem.IsWindows())
        {
            try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
            catch (IOException) { }
        }

        return newKey;
    }

    // ── Legacy format (read-only, for migration) ─────────────────────────

    private (byte[] Key, byte[] IV)? LoadLegacyKey()
    {
        var saltPath = Path.Combine(_appFolder, ".salt");
        byte[] salt;
        try
        {
            salt = File.Exists(saltPath) ? File.ReadAllBytes(saltPath) : [];
        }
        catch (IOException)
        {
            salt = [];
        }

        // The old implementation fell back to this constant when the salt file was missing.
        if (salt.Length != 32)
            salt = Encoding.UTF8.GetBytes("5@ETrbDNarrdA&!56!nBHsKis9CLC9LSma&j5pNN");

        var machineBytes = Encoding.UTF8.GetBytes($"{Environment.MachineName}:{Environment.UserName}:T-SSH-2024");
        var password = new byte[salt.Length + machineBytes.Length];
        salt.CopyTo(password, 0);
        machineBytes.CopyTo(password, salt.Length);

        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        var iv = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 16);
        return (key, iv);
    }

    private string DecryptLegacy(string encryptedText)
    {
        var legacy = _legacyKey.Value;
        if (legacy is null)
            return encryptedText;

        try
        {
            using var aes = Aes.Create();
            aes.Key = legacy.Value.Key;
            var bytes = Convert.FromBase64String(encryptedText);
            var plain = aes.DecryptCbc(bytes, legacy.Value.IV, PaddingMode.PKCS7);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Values stored before encryption existed were plain text.
            return encryptedText;
        }
    }
}
