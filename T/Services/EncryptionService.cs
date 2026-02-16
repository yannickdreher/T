using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace T.Services;

/// <summary>
/// Cross-Platform AES-256 Verschlüsselung für sensible Daten
/// Funktioniert auf Windows, Linux und macOS
/// </summary>
public static class EncryptionService
{
    private static readonly string _saltFilePath;
    private static readonly byte[] _key;
    private static readonly byte[] _iv;

    static EncryptionService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "T");
        Directory.CreateDirectory(appFolder);
        _saltFilePath = Path.Combine(appFolder, ".salt");

        var salt = GetOrCreateSalt();
        (_key, _iv) = DeriveKeyAndIV(salt);
    }

    /// <summary>
    /// Verschlüsselt einen String mit AES-256
    /// </summary>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            return Convert.ToBase64String(encryptedBytes);
        }
        catch
        {
            return plainText;
        }
    }

    /// <summary>
    /// Entschlüsselt einen AES-256 verschlüsselten String
    /// </summary>
    public static string Decrypt(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText))
            return string.Empty;

        try
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var encryptedBytes = Convert.FromBase64String(encryptedText);
            var decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch
        {
            return encryptedText;
        }
    }

    /// <summary>
    /// Prüft ob ein String verschlüsselt aussieht (Base64-Format)
    /// </summary>
    public static bool IsEncrypted(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            var bytes = Convert.FromBase64String(text);
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Holt oder erstellt eine zufällige Salt-Datei
    /// </summary>
    private static byte[] GetOrCreateSalt()
    {
        try
        {
            if (File.Exists(_saltFilePath))
            {
                var salt = File.ReadAllBytes(_saltFilePath);
                if (salt.Length == 32)
                    return salt;
            }

            var newSalt = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(_saltFilePath, newSalt);

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(_saltFilePath, FileAttributes.Hidden);
                }
            }
            catch { }

            return newSalt;
        }
        catch
        {
            return Encoding.UTF8.GetBytes("5@ETrbDNarrdA&!56!nBHsKis9CLC9LSma&j5pNN");
        }
    }

    /// <summary>
    /// Leitet AES-Schlüssel und IV aus Salt + Systemdaten ab
    /// </summary>
    private static (byte[] key, byte[] iv) DeriveKeyAndIV(byte[] salt)
    {
        var machineData = $"{Environment.MachineName}:{Environment.UserName}:T-SSH-2024";
        var machineBytes = Encoding.UTF8.GetBytes(machineData);

        var combinedData = new byte[salt.Length + machineBytes.Length];
        Buffer.BlockCopy(salt, 0, combinedData, 0, salt.Length);
        Buffer.BlockCopy(machineBytes, 0, combinedData, salt.Length, machineBytes.Length);

        var key = Rfc2898DeriveBytes.Pbkdf2(
            password: combinedData,
            salt: salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32
        );
        var iv = Rfc2898DeriveBytes.Pbkdf2(
            password: combinedData,
            salt: salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 16
        );

        return (key, iv);
    }
}