using Microsoft.Data.Sqlite;
using T.Abstractions;
using T.Models;
using T.Services;

namespace T.Tests;

public class SshImportStorageTests
{
    [Fact]
    public async Task Import_ReusesExactHopAndCreatesDistinctChainForDifferentPredecessor()
    {
        using var temp = new TempDirectory();
        var storage = new SqliteSessionStorageService(new EmptySecrets(), temp.File("T.db"));
        var existing = Session("saved", "same.example");
        await storage.AddSessionAsync(existing);

        var imported = await storage.ImportSessionsAsync(
        [
            new(Session("first", "first.example"), [Session("hop", "same.example")]),
            new(Session("second", "second.example"), [Session("outer", "outer.example"), Session("hop", "same.example")])
        ], null);

        var first = imported.Single(entry => entry.Name == "first");
        var second = imported.Single(entry => entry.Name == "second");
        var outer = imported.Single(entry => entry.Name == "outer");
        var nested = imported.Single(entry => entry.Host == "same.example");
        Assert.Equal(existing.Id, first.ProxyJumpSessionId);
        Assert.Equal(nested.Id, second.ProxyJumpSessionId);
        Assert.Equal(outer.Id, nested.ProxyJumpSessionId);
        Assert.Equal(5, (await storage.LoadSessionsAsync()).Count);
    }

    [Fact]
    public async Task Import_PreservesImportedAliasAsAnExistingHopForSelectedTarget()
    {
        using var temp = new TempDirectory();
        var storage = new SqliteSessionStorageService(new EmptySecrets(), temp.File("T.db"));

        var imported = await storage.ImportSessionsAsync(
        [
            new(Session("target", "target.example"), [Session("hop", "hop.example")]),
            new(Session("hop", "hop.example"), [])
        ], null);

        Assert.Equal(2, imported.Count);
        var hop = imported.Single(entry => entry.Name == "hop");
        Assert.Equal(hop.Id, imported.Single(entry => entry.Name == "target").ProxyJumpSessionId);
    }

    [Fact]
    public async Task Import_RollsBackAllProfilesAndHopsAfterAnInsertError()
    {
        using var temp = new TempDirectory();
        var path = temp.File("T.db");
        var storage = new SqliteSessionStorageService(new EmptySecrets(), path);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER RejectFailedImport BEFORE INSERT ON Sessions
                WHEN NEW.Name = 'fail'
                BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;
                """;
            await trigger.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<SqliteException>(() => storage.ImportSessionsAsync(
        [
            new(Session("ok", "ok.example"), []),
            new(Session("fail", "fail.example"), [Session("new hop", "hop.example")])
        ], null));

        Assert.Empty(await storage.LoadSessionsAsync());
    }

    [Fact]
    public async Task Import_RejectsConflictAndMissingDestinationWithoutChanges()
    {
        using var temp = new TempDirectory();
        var storage = new SqliteSessionStorageService(new EmptySecrets(), temp.File("T.db"));
        var original = Session("Existing", "old.example");
        await storage.AddSessionAsync(original);
        var duplicate = new SshImportProfile(Session("existing", "new.example"), []);
        var valid = new SshImportProfile(Session("another", "another.example"), []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ImportSessionsAsync([valid, duplicate], null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ImportSessionsAsync([valid], "deleted folder"));

        var remaining = Assert.Single(await storage.LoadSessionsAsync());
        Assert.Equal(original.Id, remaining.Id);
        Assert.Equal("old.example", remaining.Host);
    }

    private static SshSession Session(string name, string host) =>
        new() { Name = name, Host = host, Username = "imported" };

    private sealed class EmptySecrets : IEncryptionService
    {
        public string Encrypt(string plainText) => plainText.Length == 0 ? "" : throw new InvalidOperationException();
        public string Decrypt(string encryptedText) => Encrypt(encryptedText);
        public string EncryptBytes(ReadOnlySpan<byte> plainBytes, ReadOnlySpan<byte> associatedData = default) =>
            throw new NotSupportedException();
        public byte[]? DecryptBytes(string encryptedText, ReadOnlySpan<byte> associatedData = default) =>
            throw new NotSupportedException();
        public bool NeedsMigration(string storedValue) => false;
    }
}
