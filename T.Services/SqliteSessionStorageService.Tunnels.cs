using Microsoft.Data.Sqlite;
using T.Models;

namespace T.Services;

public partial class SqliteSessionStorageService
{
    private readonly SemaphoreSlim _tunnelGate = new(1, 1);
    private readonly Dictionary<string, int> _tunnelLeases = [];

    private const string TunnelColumns = "Id, SessionId, Name, Kind, ListenPort, TargetHost, TargetPort, StartOnConnect";

    // Dynamic (SOCKS) rules have no fixed target: both target columns are NULL for them.
    private static void InitializeTunnels(SqliteConnection connection) =>
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Tunnels (
                Id             TEXT PRIMARY KEY,
                SessionId      TEXT NOT NULL,
                Name           TEXT NOT NULL,
                Kind           TEXT NOT NULL CHECK (Kind IN ('local', 'remote', 'dynamic')),
                ListenPort     INTEGER NOT NULL CHECK (ListenPort BETWEEN 1 AND 65535),
                TargetHost     TEXT,
                TargetPort     INTEGER CHECK (TargetPort BETWEEN 1 AND 65535),
                StartOnConnect INTEGER NOT NULL DEFAULT 0,
                CHECK ((Kind = 'dynamic' AND TargetHost IS NULL AND TargetPort IS NULL)
                    OR (Kind <> 'dynamic' AND TargetHost IS NOT NULL AND TargetPort IS NOT NULL)),
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS Tunnels_SessionId ON Tunnels(SessionId);
            """);

    /// <summary>
    /// Schema 9: rules have a type (local, remote, dynamic). Up to version 8 the table
    /// "LocalTunnels" held local forwardings only; its rules move to "Tunnels" as local rules.
    /// </summary>
    private static void MigrateLocalTunnels(SqliteConnection connection)
    {
        if (!TableExists(connection, "LocalTunnels")) return;
        Execute(connection, """
            INSERT OR IGNORE INTO Tunnels (Id, SessionId, Name, Kind, ListenPort, TargetHost, TargetPort, StartOnConnect)
            SELECT Id, SessionId, Name, 'local', LocalPort, TargetHost, TargetPort, StartOnConnect FROM LocalTunnels;
            DROP TABLE LocalTunnels;
            """);
    }

    private static string KindName(TunnelKind kind) => kind switch
    {
        TunnelKind.Local => "local",
        TunnelKind.Remote => "remote",
        TunnelKind.Dynamic => "dynamic",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown tunnel type.")
    };

    private static TunnelKind ParseKind(string name) => name switch
    {
        "local" => TunnelKind.Local,
        "remote" => TunnelKind.Remote,
        "dynamic" => TunnelKind.Dynamic,
        _ => throw new InvalidDataException($"Unknown tunnel type '{name}' in the database.")
    };

    private static async Task<List<TunnelRule>> ReadTunnelsAsync(SqliteCommand command)
    {
        var rules = new List<TunnelRule>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rules.Add(new TunnelRule(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                ParseKind(reader.GetString(3)), reader.GetInt32(4),
                reader.IsDBNull(5) ? "" : reader.GetString(5), reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                reader.GetBoolean(7)));
        }
        return rules;
    }

    /// <summary>All tunnel rules, each referring to the terminal profile it connects through.</summary>
    public async Task<List<TunnelRule>> LoadAllTunnelsAsync()
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TunnelColumns} FROM Tunnels ORDER BY Name COLLATE NOCASE, Id;";
        return await ReadTunnelsAsync(command);
    }

    public async Task<List<TunnelRule>> LoadTunnelsAsync(string sessionId)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TunnelColumns} FROM Tunnels WHERE SessionId = @SessionId ORDER BY Name COLLATE NOCASE, Id;";
        command.Parameters.AddWithValue("@SessionId", sessionId);
        return await ReadTunnelsAsync(command);
    }

    public async Task SaveTunnelAsync(TunnelRule rule, bool creating)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
            throw new ArgumentException("Enter a name for the tunnel.", nameof(rule));
        if (rule.EndpointError is { } error)
            throw new ArgumentException(error, nameof(rule));

        await _tunnelGate.WaitAsync();
        try
        {
            EnsureTunnelEditable(rule.Id);
            await using var connection = CreateConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = creating
                ? """
                    INSERT INTO Tunnels (Id, SessionId, Name, Kind, ListenPort, TargetHost, TargetPort, StartOnConnect)
                    VALUES (@Id, @SessionId, @Name, @Kind, @ListenPort, @TargetHost, @TargetPort, @StartOnConnect);
                    """
                : """
                    UPDATE Tunnels SET SessionId = @SessionId, Name = @Name, Kind = @Kind, ListenPort = @ListenPort,
                        TargetHost = @TargetHost, TargetPort = @TargetPort, StartOnConnect = @StartOnConnect
                    WHERE Id = @Id;
                    """;
            command.Parameters.AddWithValue("@Id", rule.Id);
            command.Parameters.AddWithValue("@SessionId", rule.SessionId);
            command.Parameters.AddWithValue("@Name", rule.Name);
            command.Parameters.AddWithValue("@Kind", KindName(rule.Kind));
            command.Parameters.AddWithValue("@ListenPort", rule.ListenPort);
            command.Parameters.AddWithValue("@TargetHost", rule.HasTarget ? rule.TargetHost : DBNull.Value);
            command.Parameters.AddWithValue("@TargetPort", rule.HasTarget ? rule.TargetPort : DBNull.Value);
            command.Parameters.AddWithValue("@StartOnConnect", rule.StartOnConnect);

            if (await command.ExecuteNonQueryAsync() != 1)
                throw new InvalidOperationException("This tunnel rule no longer exists.");
        }
        finally { _tunnelGate.Release(); }
    }

    public async Task DeleteTunnelAsync(TunnelRule rule)
    {
        await _tunnelGate.WaitAsync();
        try
        {
            EnsureTunnelEditable(rule.Id);
            await using var connection = CreateConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Tunnels WHERE Id = @Id;";
            command.Parameters.AddWithValue("@Id", rule.Id);
            await command.ExecuteNonQueryAsync();
        }
        finally { _tunnelGate.Release(); }
    }

    public async Task<IDisposable> AcquireTunnelAsync(TunnelRule rule, CancellationToken cancellationToken)
    {
        await _tunnelGate.WaitAsync(cancellationToken);
        try
        {
            var saved = (await LoadTunnelsAsync(rule.SessionId)).Find(candidate => candidate.Id == rule.Id);
            if (saved != rule)
                throw new InvalidOperationException("The saved tunnel rule changed. Refresh the tunnel list and try again.");

            _tunnelLeases[rule.Id] = _tunnelLeases.GetValueOrDefault(rule.Id) + 1;
            return new TunnelLease(this, rule.Id);
        }
        finally { _tunnelGate.Release(); }
    }

    private void EnsureTunnelEditable(string ruleId)
    {
        if (_tunnelLeases.ContainsKey(ruleId))
            throw new InvalidOperationException("Stop this tunnel in all open sessions before changing its rule.");
    }

    private void ReleaseTunnel(string ruleId)
    {
        _tunnelGate.Wait();
        try
        {
            var remaining = _tunnelLeases[ruleId] - 1;
            if (remaining == 0) _tunnelLeases.Remove(ruleId);
            else _tunnelLeases[ruleId] = remaining;
        }
        finally { _tunnelGate.Release(); }
    }

    private sealed class TunnelLease(SqliteSessionStorageService owner, string ruleId) : IDisposable
    {
        private SqliteSessionStorageService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseTunnel(ruleId);
    }
}
