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
        var rule = new TunnelRule("rule", "session", "Example", TunnelKind.Local, localPort, host, targetPort, false);

        Assert.Equal(valid, rule.HasValidEndpoints);
        Assert.Equal($"127.0.0.1:{localPort}", rule.ListenEndpoint);
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

        var outside = new TunnelRule("a", "standalone", "Outside", TunnelKind.Local, 8080, "::1", 80, true);
        var inside = new TunnelRule("b", "nested", "Inside", TunnelKind.Local, 8081, "service.internal", 81, false);
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

        var rule = new TunnelRule("rule", session.Id, "Example", TunnelKind.Local, 8080, "service.internal", 80, false);
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

        // Version 7 kept a hidden copy of the SSH connection per tunnel, and its rules in "LocalTunnels".
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Sessions (Id, Name, Host, Port, Username, Password, PrivateKeyPath, FolderId, Description, TunnelOwned)
                VALUES ('tunnel:web', 'Web', 'gateway', 2222, 'user', 'test', '', NULL, '', 1);
                DROP TABLE Tunnels;
                CREATE TABLE LocalTunnels (Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, Name TEXT NOT NULL,
                    LocalPort INTEGER NOT NULL CHECK (LocalPort BETWEEN 1 AND 65535), TargetHost TEXT NOT NULL,
                    TargetPort INTEGER NOT NULL CHECK (TargetPort BETWEEN 1 AND 65535), StartOnConnect INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE);
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
        Assert.Equal((TunnelKind.Local, 8080, "localhost", 80), (rule.Kind, rule.ListenPort, rule.TargetHost, rule.TargetPort));
    }

    [Fact]
    public async Task MigratesLocalRulesFromVersionEight()
    {
        using var directory = new TempDirectory();
        var path = directory.File("T.db");

        // The schema of version 8: rules are local forwardings in "LocalTunnels", without a type.
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Folders (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, ParentId TEXT, IsExpanded INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (ParentId) REFERENCES Folders(Id) ON DELETE CASCADE);
                CREATE TABLE Sessions (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Host TEXT NOT NULL, Port INTEGER NOT NULL DEFAULT 22,
                    Username TEXT NOT NULL, Password TEXT NOT NULL, PrivateKeyPath TEXT NOT NULL, PrivateKeyPassword TEXT NOT NULL DEFAULT '',
                    FolderId TEXT, Description TEXT NOT NULL, ProxyJumpSessionId TEXT, StepProfileId TEXT,
                    TunnelOwned INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (FolderId) REFERENCES Folders(Id) ON DELETE SET NULL);
                CREATE TABLE LocalTunnels (Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, Name TEXT NOT NULL,
                    LocalPort INTEGER NOT NULL CHECK (LocalPort BETWEEN 1 AND 65535), TargetHost TEXT NOT NULL,
                    TargetPort INTEGER NOT NULL CHECK (TargetPort BETWEEN 1 AND 65535), StartOnConnect INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE);
                CREATE INDEX LocalTunnels_SessionId ON LocalTunnels(SessionId);
                INSERT INTO Sessions VALUES ('gateway', 'Gateway', 'host', 22, 'user', '', '', '', NULL, '', NULL, NULL, 0);
                INSERT INTO LocalTunnels VALUES ('db', 'gateway', 'Database', 5432, 'db.internal', 5433, 0);
                INSERT INTO LocalTunnels VALUES ('web', 'gateway', 'Web', 8080, '::1', 80, 0);
                PRAGMA user_version = 8;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var encryption = new PlaceholderEncryption();
        var storage = new SqliteSessionStorageService(encryption, path);

        // Existing rules are local ones with unchanged values.
        var rules = await storage.LoadAllTunnelsAsync();
        Assert.Equal(
            [
                new TunnelRule("db", "gateway", "Database", TunnelKind.Local, 5432, "db.internal", 5433, false),
                new TunnelRule("web", "gateway", "Web", TunnelKind.Local, 8080, "::1", 80, false)
            ],
            rules);

        // The old table is gone and the new types can be saved next to the migrated rules.
        var socks = new TunnelRule("socks", "gateway", "Proxy", TunnelKind.Dynamic, 1080, "", 0, false);
        await storage.SaveTunnelAsync(socks, creating: true);
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT COUNT(*) FROM sqlite_master WHERE name = 'LocalTunnels'), (SELECT user_version FROM pragma_user_version);";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal((0L, 9L), (reader.GetInt64(0), reader.GetInt64(1)));
        }

        // Opening the migrated database again changes nothing.
        storage = new SqliteSessionStorageService(encryption, path);
        Assert.Equal([rules[0], socks, rules[1]], await storage.LoadAllTunnelsAsync()); // sorted by name

        // Rules are still deleted with their profile.
        await storage.DeleteSessionAsync("gateway");
        Assert.Empty(await storage.LoadAllTunnelsAsync());
    }

    [Fact]
    public async Task StoresRemoteAndDynamicRules()
    {
        using var directory = new TempDirectory();
        var path = directory.File("T.db");
        var storage = new SqliteSessionStorageService(new PlaceholderEncryption(), path);
        var session = new SshSession { Name = "Server", Host = "host", Username = "user" };
        await storage.AddSessionAsync(session);

        var remote = new TunnelRule("remote", session.Id, "Dev server", TunnelKind.Remote, 8080, "localhost", 5173, false);
        var dynamic = new TunnelRule("socks", session.Id, "Proxy", TunnelKind.Dynamic, 1080, "", 0, false);
        await storage.SaveTunnelAsync(remote, creating: true);
        await storage.SaveTunnelAsync(dynamic, creating: true);
        Assert.Equal([remote, dynamic], await storage.LoadTunnelsAsync(session.Id));

        // A dynamic rule stores no target.
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Tunnels WHERE Kind = 'dynamic' AND TargetHost IS NULL AND TargetPort IS NULL;";
            Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.SaveTunnelAsync(dynamic with { TargetHost = "service.internal", TargetPort = 80 }, creating: false));

        // The type of a stopped rule can be changed; the saved rule is what a tunnel may start with.
        var changed = dynamic with { Kind = TunnelKind.Local, TargetHost = "service.internal", TargetPort = 80 };
        await storage.SaveTunnelAsync(changed, creating: false);
        Assert.Equal([remote, changed], await storage.LoadAllTunnelsAsync());
        var cancellation = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.AcquireTunnelAsync(dynamic, cancellation));
        using (await storage.AcquireTunnelAsync(remote, cancellation))
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.DeleteTunnelAsync(remote));
    }

    [Theory]
    [InlineData(TunnelKind.Remote, 8080, "localhost", 3000, null)]
    [InlineData(TunnelKind.Remote, 0, "localhost", 3000, "The server port must be between 1 and 65535.")]
    [InlineData(TunnelKind.Remote, 8080, "localhost", 0, "The target port must be between 1 and 65535.")]
    [InlineData(TunnelKind.Remote, 8080, "local host", 3000, "Enter the target as a host name or IP address, as seen from this computer.")]
    [InlineData(TunnelKind.Local, 70000, "localhost", 3000, "The local port must be between 1 and 65535.")]
    [InlineData(TunnelKind.Local, 8080, "", 3000, "Enter the target as a host name or IP address, as seen from the SSH server.")]
    [InlineData(TunnelKind.Dynamic, 1080, "", 0, null)]
    [InlineData(TunnelKind.Dynamic, 0, "", 0, "The local port must be between 1 and 65535.")]
    [InlineData(TunnelKind.Dynamic, 1080, "service.internal", 80, "A dynamic tunnel has no fixed target.")]
    [InlineData((TunnelKind)7, 1080, "service.internal", 80, "Choose the type of the tunnel.")]
    public void Validation_NamesTheInvalidFieldOfEachType(TunnelKind kind, int listenPort, string host, int targetPort, string? error)
    {
        var rule = new TunnelRule("rule", "session", "Example", kind, listenPort, host, targetPort, false);

        Assert.Equal(error, rule.EndpointError);
        Assert.Equal(error is null, rule.HasValidEndpoints);
    }

    [Fact]
    public void Route_SaysWhereTheListenerIsAndThatARemoteAddressIsOnlyRequested()
    {
        TunnelRule Rule(TunnelKind kind, int port, string host, int targetPort) =>
            new("rule", "session", "Example", kind, port, host, targetPort, false);

        Assert.Equal("127.0.0.1:8080 → db.internal:5432", Rule(TunnelKind.Local, 8080, "db.internal", 5432).Route);
        Assert.Equal("server 127.0.0.1:8080 (requested) → [::1]:3000", Rule(TunnelKind.Remote, 8080, "::1", 3000).Route);
        Assert.Equal("127.0.0.1:1080 → SOCKS proxy", Rule(TunnelKind.Dynamic, 1080, "", 0).Route);
    }

    [Fact]
    public void StartErrors_SayWhichSideRefusedThePort()
    {
        var local = new TunnelRule("rule", "session", "Example", TunnelKind.Local, 8080, "localhost", 80, false);
        var remote = local with { Kind = TunnelKind.Remote };
        var inUse = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AddressAlreadyInUse);
        var refused = new Renci.SshNet.Common.SshException("Port forwarding for '127.0.0.1' port '8080' failed to start.");
        var lost = new Renci.SshNet.Common.SshConnectionException("Client not connected.");

        Assert.Equal("Local port 127.0.0.1:8080 is already in use.", SshService.DescribeTunnelStartError(local, inUse));
        Assert.Equal(
            "The server did not open port 127.0.0.1:8080: it may be in use there, or the server does not allow remote forwarding.",
            SshService.DescribeTunnelStartError(remote, refused));
        Assert.Equal(refused.Message, SshService.DescribeTunnelStartError(local, refused));
        Assert.Equal(lost.Message, SshService.DescribeTunnelStartError(remote, lost));
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

        var rule = new TunnelRule("rule", first.Id, "Web", TunnelKind.Local, 8080, "service.internal", 80, false);
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
