using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using T.Models;
using T.Services;

namespace T.Tests;

public class EncryptionAndStorageTests
{
    /// <summary>Re-implements the previous (v1) format to verify that old data stays readable.</summary>
    private static string LegacyEncrypt(string appFolder, string plainText)
    {
        var salt = File.ReadAllBytes(Path.Combine(appFolder, ".salt"));
        var machine = Encoding.UTF8.GetBytes($"{Environment.MachineName}:{Environment.UserName}:T-SSH-2024");
        var password = salt.Concat(machine).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        var iv = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 16);

        using var aes = Aes.Create();
        aes.Key = key;
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plainText), iv, PaddingMode.PKCS7));
    }

    [Fact]
    public void Encrypt_RoundTrips_WithRandomNonce()
    {
        using var dir = new TempDirectory();
        var service = new EncryptionService(dir.Path);

        var a = service.Encrypt("s3cret-päss");
        var b = service.Encrypt("s3cret-päss");

        Assert.NotEqual(a, b);
        Assert.StartsWith("v2:", a);
        Assert.Equal("s3cret-päss", service.Decrypt(a));
        Assert.False(service.NeedsMigration(a));
        Assert.Equal("", service.Encrypt(""));
    }

    [Fact]
    public void MasterKey_IsReusedByNewInstances()
    {
        using var dir = new TempDirectory();
        var encrypted = new EncryptionService(dir.Path).Encrypt("value");
        Assert.Equal("value", new EncryptionService(dir.Path).Decrypt(encrypted));
    }

    [Fact]
    public void TamperedValue_DecryptsToEmpty_NotToCiphertext()
    {
        using var dir = new TempDirectory();
        var service = new EncryptionService(dir.Path);
        var encrypted = service.Encrypt("value");
        var bytes = Convert.FromBase64String(encrypted[3..]);
        bytes[^1] ^= 0xFF;

        Assert.Equal("", service.Decrypt("v2:" + Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void LegacyValues_AreDecrypted_AndPlainTextIsKept()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(Path.Combine(dir.Path, ".salt"), RandomNumberGenerator.GetBytes(32));
        var service = new EncryptionService(dir.Path);

        var legacy = LegacyEncrypt(dir.Path, "old-password");

        Assert.True(service.NeedsMigration(legacy));
        Assert.Equal("old-password", service.Decrypt(legacy));
        Assert.Equal("plain text!", service.Decrypt("plain text!"));
    }

    [Fact]
    public async Task Storage_MigratesLegacySecrets_AndDropsJunkHostsTable()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(Path.Combine(dir.Path, ".salt"), RandomNumberGenerator.GetBytes(32));
        var dbPath = dir.File("T.db");
        var legacyPassword = LegacyEncrypt(dir.Path, "pw");

        // Database as written by schema version 4, including the empty "Hosts" table
        // that older versions re-created on every start.
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE Folders (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, ParentId TEXT, IsExpanded INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE Sessions (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Host TEXT NOT NULL, Port INTEGER NOT NULL DEFAULT 22,
                    Username TEXT NOT NULL, Password TEXT NOT NULL, PrivateKeyPath TEXT NOT NULL, PrivateKeyPassword TEXT NOT NULL DEFAULT '',
                    FolderId TEXT, Description TEXT NOT NULL, ProxyJumpSessionId TEXT);
                CREATE TABLE Hosts (Id TEXT PRIMARY KEY);
                INSERT INTO Sessions VALUES ('s1', 'Server', 'host', 22, 'root', '{legacyPassword}', '', '', NULL, '', NULL);
                PRAGMA user_version = 4;
                """;
            cmd.ExecuteNonQuery();
        }

        var encryption = new EncryptionService(dir.Path);
        var storage = new SqliteSessionStorageService(encryption, dbPath);

        var session = Assert.Single(await storage.LoadSessionsAsync());
        Assert.Equal("pw", session.Password);

        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Password FROM Sessions; ";
            Assert.StartsWith("v2:", (string)cmd.ExecuteScalar()!);
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'Hosts';";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task DeleteFolder_DeletesContainedSessions_AndClearsJumpHostReferences()
    {
        using var dir = new TempDirectory();
        var storage = new SqliteSessionStorageService(new EncryptionService(dir.Path), dir.File("T.db"));

        var parent = new Folder { Name = "parent" };
        var child = new Folder { Name = "child", ParentId = parent.Id };
        await storage.AddFolderAsync(parent);
        await storage.AddFolderAsync(child);

        var jump = new SshSession { Name = "jump", Host = "j", Username = "u", FolderId = child.Id };
        var outside = new SshSession { Name = "target", Host = "t", Username = "u", ProxyJumpSessionId = jump.Id };
        await storage.AddSessionAsync(jump);
        await storage.AddSessionAsync(outside);

        await storage.DeleteFolderAsync(parent.Id);

        var sessions = await storage.LoadSessionsAsync();
        var remaining = Assert.Single(sessions);
        Assert.Equal(outside.Id, remaining.Id);
        Assert.Null(remaining.ProxyJumpSessionId);
        Assert.Empty(await storage.LoadFoldersAsync());
    }

    [Fact]
    public async Task DeleteSession_ClearsJumpHostReferences()
    {
        using var dir = new TempDirectory();
        var storage = new SqliteSessionStorageService(new EncryptionService(dir.Path), dir.File("T.db"));

        var jump = new SshSession { Name = "jump", Host = "j", Username = "u" };
        var target = new SshSession { Name = "target", Host = "t", Username = "u", ProxyJumpSessionId = jump.Id };
        await storage.AddSessionAsync(jump);
        await storage.AddSessionAsync(target);

        await storage.DeleteSessionAsync(jump.Id);

        Assert.Null((await storage.GetSessionByIdAsync(target.Id))!.ProxyJumpSessionId);
    }

    [Fact]
    public void Settings_AreSanitizedOnLoad()
    {
        using var dir = new TempDirectory();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{ "General": { "ConnectionTimeout": 0, "KeepAliveInterval": 100000 }, "Terminal": { "ScrollbackLines": 5 } }""");

        var settings = new SettingsService(path).Current;

        Assert.Equal(5, settings.General.ConnectionTimeout);
        Assert.Equal(120, settings.General.KeepAliveInterval);
        Assert.Equal(1000, settings.Terminal.ScrollbackLines);
    }
}
