using Microsoft.Data.Sqlite;
using T.Models;

namespace T.Services;

public partial class SqliteSessionStorageService
{
    private readonly SemaphoreSlim _localTunnelGate = new(1, 1);
    private readonly Dictionary<string, int> _localTunnelLeases = [];

    private static void InitializeLocalTunnels(SqliteConnection connection) =>
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS LocalTunnels (
                Id             TEXT PRIMARY KEY,
                SessionId      TEXT NOT NULL,
                Name           TEXT NOT NULL,
                LocalPort      INTEGER NOT NULL CHECK (LocalPort BETWEEN 1 AND 65535),
                TargetHost     TEXT NOT NULL,
                TargetPort     INTEGER NOT NULL CHECK (TargetPort BETWEEN 1 AND 65535),
                StartOnConnect INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS LocalTunnels_SessionId ON LocalTunnels(SessionId);
            """);

    public async Task<List<LocalTunnelRule>> LoadLocalTunnelsAsync(string sessionId)
    {
        var rules = new List<LocalTunnelRule>();
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, SessionId, Name, LocalPort, TargetHost, TargetPort, StartOnConnect
            FROM LocalTunnels WHERE SessionId = @SessionId ORDER BY Name COLLATE NOCASE, Id;
            """;
        command.Parameters.AddWithValue("@SessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rules.Add(new LocalTunnelRule(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5), reader.GetBoolean(6)));
        }
        return rules;
    }

    public async Task SaveLocalTunnelAsync(LocalTunnelRule rule, bool creating)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || !rule.HasValidEndpoints)
            throw new ArgumentException("Enter a name, a valid target host, and ports between 1 and 65535.", nameof(rule));

        await _localTunnelGate.WaitAsync();
        try
        {
            EnsureTunnelEditable(rule.Id);
            await using var connection = CreateConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = creating
                ? """
                    INSERT INTO LocalTunnels (Id, SessionId, Name, LocalPort, TargetHost, TargetPort, StartOnConnect)
                    VALUES (@Id, @SessionId, @Name, @LocalPort, @TargetHost, @TargetPort, @StartOnConnect);
                    """
                : """
                    UPDATE LocalTunnels SET SessionId = @SessionId, Name = @Name, LocalPort = @LocalPort,
                        TargetHost = @TargetHost, TargetPort = @TargetPort, StartOnConnect = @StartOnConnect
                    WHERE Id = @Id;
                    """;
            command.Parameters.AddWithValue("@Id", rule.Id);
            command.Parameters.AddWithValue("@SessionId", rule.SessionId);
            command.Parameters.AddWithValue("@Name", rule.Name);
            command.Parameters.AddWithValue("@LocalPort", rule.LocalPort);
            command.Parameters.AddWithValue("@TargetHost", rule.TargetHost);
            command.Parameters.AddWithValue("@TargetPort", rule.TargetPort);
            command.Parameters.AddWithValue("@StartOnConnect", rule.StartOnConnect);

            if (await command.ExecuteNonQueryAsync() != 1)
                throw new InvalidOperationException("This tunnel rule no longer exists.");
        }
        finally { _localTunnelGate.Release(); }
    }

    public async Task DeleteLocalTunnelAsync(LocalTunnelRule rule)
    {
        await _localTunnelGate.WaitAsync();
        try
        {
            EnsureTunnelEditable(rule.Id);
            await using var connection = CreateConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM LocalTunnels WHERE Id = @Id;";
            command.Parameters.AddWithValue("@Id", rule.Id);
            await command.ExecuteNonQueryAsync();
        }
        finally { _localTunnelGate.Release(); }
    }

    public async Task<IDisposable> AcquireLocalTunnelAsync(LocalTunnelRule rule, CancellationToken cancellationToken)
    {
        await _localTunnelGate.WaitAsync(cancellationToken);
        try
        {
            var saved = (await LoadLocalTunnelsAsync(rule.SessionId)).Find(candidate => candidate.Id == rule.Id);
            if (saved != rule)
                throw new InvalidOperationException("The saved tunnel rule changed. Refresh the tunnel list and try again.");

            _localTunnelLeases[rule.Id] = _localTunnelLeases.GetValueOrDefault(rule.Id) + 1;
            return new LocalTunnelLease(this, rule.Id);
        }
        finally { _localTunnelGate.Release(); }
    }

    private void EnsureTunnelEditable(string ruleId)
    {
        if (_localTunnelLeases.ContainsKey(ruleId))
            throw new InvalidOperationException("Stop this tunnel in all open sessions before changing its rule.");
    }

    private void ReleaseLocalTunnel(string ruleId)
    {
        _localTunnelGate.Wait();
        try
        {
            var remaining = _localTunnelLeases[ruleId] - 1;
            if (remaining == 0) _localTunnelLeases.Remove(ruleId);
            else _localTunnelLeases[ruleId] = remaining;
        }
        finally { _localTunnelGate.Release(); }
    }

    private sealed class LocalTunnelLease(SqliteSessionStorageService owner, string ruleId) : IDisposable
    {
        private SqliteSessionStorageService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLocalTunnel(ruleId);
    }
}
