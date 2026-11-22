using Microsoft.Data.Sqlite;
using T.Abstractions;
using T.Models;
using T.Services;

namespace T.Tests;

public class TunnelRuleTests
{
    [Theory]
    [InlineData("service.internal", 8080, 80, true)]
    [InlineData("127.0.0.1", 1, 65535, true)]
    [InlineData("::1", 8080, 80, true)]
    [InlineData("https://service.internal", 8080, 80, false)]
    [InlineData("service.internal:80", 8080, 80, false)]
    [InlineData("service internal", 8080, 80, false)]
    [InlineData("service.internal", 0, 80, false)]
    [InlineData("service.internal", 8080, 65536, false)]
    public void Validation_AcceptsHostAndPortPairs(string host, int localPort, int targetPort, bool valid)
    {
        var rule = new TunnelRule("rule", "session", "Example", localPort, host, targetPort, false);

        Assert.Equal(valid, rule.HasValidEndpoints);
        Assert.Equal($"127.0.0.1:{localPort}", rule.LocalEndpoint);
    }

    [Fact]
    public async Task OpensVersionFiveDatabase_AndCascadesTunnelDeletes()
    {
        using var directory = new TempDirectory();
        var dbPath = directory.File("T.db");
        using (var db = new SqliteConnection($"Data Source={dbPath}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE Folders (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, ParentId TEXT, IsExpanded INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (ParentId) REFERENCES Folders(Id) ON DELETE CASCADE);
                CREATE TABLE Sessions (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Host TEXT NOT NULL, Port INTEGER NOT NULL DEFAULT 22,
                    Username TEXT NOT NULL, Password TEXT NOT NULL, PrivateKeyPath TEXT NOT NULL, PrivateKeyPassword TEXT NOT NULL DEFAULT '',
                    FolderId TEXT, Description TEXT NOT NULL, ProxyJumpSessionId TEXT, StepProfileId TEXT,
                    FOREIGN KEY (FolderId) REFERENCES Folders(Id) ON DELETE SET NULL);
                INSERT INTO Folders VALUES ('parent', 'Parent', NULL, 0);
                INSERT INTO Folders VALUES ('child', 'Child', 'parent', 0);
                INSERT INTO Sessions VALUES ('standalone', 'Outside', 'host', 22, 'user', '', '', '', NULL, '', NULL, NULL);
                INSERT INTO Sessions VALUES ('nested', 'Inside', 'host', 22, 'user', '', '', '', 'child', '', NULL, NULL);
                PRAGMA user_version = 5;
                """;
            command.ExecuteNonQuery();
        }

        var encryption = new PlaceholderEncryption();
        var storage = new SqliteSessionStorageService(encryption, dbPath);
        Assert.Empty(await storage.LoadTunnelsAsync("standalone"));
        Assert.Empty(await storage.LoadTunnelsAsync("nested"));

        var outside = new TunnelRule("a", "standalone", "Outside", 8080, "::1", 80, true);
        var inside = new TunnelRule("b", "nested", "Inside", 8081, "service.internal", 81, false);
        await storage.SaveTunnelAsync(outside, creating: true);
        await storage.SaveTunnelAsync(inside, creating: true);

        storage = new SqliteSessionStorageService(encryption, dbPath);
        Assert.Equal(outside, Assert.Single(await storage.LoadTunnelsAsync("standalone")));
        Assert.Equal(inside, Assert.Single(await storage.LoadTunnelsAsync("nested")));

        await storage.DeleteFolderAsync("parent");
        Assert.Empty(await storage.LoadTunnelsAsync("nested"));
        Assert.Equal(outside, Assert.Single(await storage.LoadTunnelsAsync("standalone")));

        await storage.DeleteSessionAsync("standalone");
        Assert.Empty(await storage.LoadTunnelsAsync("standalone"));
    }

    [Fact]
    public async Task RuleCannotBeChangedWhileAnyInstanceUsesIt_AndStaleRuleCannotStart()
    {
        using var directory = new TempDirectory();
        var storage = new SqliteSessionStorageService(new PlaceholderEncryption(), directory.File("T.db"));
        var session = new SshSession { Name = "Server", Host = "host", Username = "user" };
        await storage.AddSessionAsync(session);

        var rule = new TunnelRule("rule", session.Id, "Example", 8080, "service.internal", 80, false);
        var updated = rule with { TargetPort = 81 };
        await storage.SaveTunnelAsync(rule, creating: true);

        var cancellation = TestContext.Current.CancellationToken;
        using (await storage.AcquireTunnelAsync(rule, cancellation))
        {
            using (await storage.AcquireTunnelAsync(rule, cancellation))
                await Assert.ThrowsAsync<InvalidOperationException>(() => storage.DeleteTunnelAsync(rule));

            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveTunnelAsync(updated, creating: false));
        }

        await storage.SaveTunnelAsync(updated, creating: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.AcquireTunnelAsync(rule, cancellation));
        await storage.DeleteTunnelAsync(updated);
        Assert.Empty(await storage.LoadTunnelsAsync(session.Id));
    }

    [Fact]
    public async Task MigratesTunnelCopiesFromVersionSevenIntoAProfileFolder()
    {
        using var directory = new TempDirectory();
        var path = directory.File("T.db");
        var encryption = new PlaceholderEncryption();
        _ = new SqliteSessionStorageService(encryption, path); // creates the current schema

        // Version 7 kept a hidden copy of the SSH connection per tunnel.
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Sessions (Id, Name, Host, Port, Username, Password, PrivateKeyPath, FolderId, Description, TunnelOwned)
                VALUES ('tunnel:web', 'Web', 'gateway', 2222, 'user', 'test', '', NULL, '', 1);
                INSERT INTO LocalTunnels (Id, SessionId, Name, LocalPort, TargetHost, TargetPort, StartOnConnect)
                VALUES ('web', 'tunnel:web', 'Web', 8080, 'localhost', 80, 1);
                PRAGMA user_version = 7;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var storage = new SqliteSessionStorageService(encryption, path);

        // The copy is a normal profile now (editable, with its password), in a "Tunnels" folder.
        var profile = Assert.Single(await storage.LoadSessionsAsync());
        Assert.Equal("tunnel:web", profile.Id);
        Assert.Equal(2222, profile.Port);
        Assert.Equal("test", profile.Password);
        var folder = Assert.Single(await storage.LoadFoldersAsync());
        Assert.Equal("Tunnels", folder.Name);
        Assert.Equal(folder.Id, profile.FolderId);

        var rule = Assert.Single(await storage.LoadAllTunnelsAsync());
        Assert.Equal(("web", "tunnel:web", false), (rule.Id, rule.SessionId, rule.StartOnConnect));
    }

    [Fact]
    public async Task TunnelUsesItsProfileByReference()
    {
        using var directory = new TempDirectory();
        var storage = new SqliteSessionStorageService(new PlaceholderEncryption(), directory.File("T.db"));
        var first = new SshSession { Name = "First", Host = "first", Username = "user" };
        var second = new SshSession { Name = "Second", Host = "second", Username = "user" };
        await storage.AddSessionAsync(first);
        await storage.AddSessionAsync(second);

        var rule = new TunnelRule("rule", first.Id, "Web", 8080, "service.internal", 80, false);
        await storage.SaveTunnelAsync(rule, creating: true);
        Assert.Equal(rule, Assert.Single(await storage.LoadAllTunnelsAsync()));

        // Switching the profile of a saved tunnel.
        var moved = rule with { SessionId = second.Id };
        await storage.SaveTunnelAsync(moved, creating: false);
        Assert.Equal(moved, Assert.Single(await storage.LoadAllTunnelsAsync()));
        Assert.Empty(await storage.LoadTunnelsAsync(first.Id));

        // Deleting the profile deletes the tunnels that connect through it.
        await storage.DeleteSessionAsync(second.Id);
        Assert.Empty(await storage.LoadAllTunnelsAsync());
    }

    private sealed class PlaceholderEncryption : IEncryptionService
    {
        public string Encrypt(string plainText) => plainText;
        public string Decrypt(string encryptedText) => encryptedText;
        public bool NeedsMigration(string storedValue) => false;
        public string EncryptBytes(ReadOnlySpan<byte> plainBytes, ReadOnlySpan<byte> associatedData = default) => throw new NotSupportedException();
        public byte[]? DecryptBytes(string encryptedText, ReadOnlySpan<byte> associatedData = default) => throw new NotSupportedException();
    }
}
