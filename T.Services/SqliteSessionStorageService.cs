using System.Globalization;
using Microsoft.Data.Sqlite;
using T.Abstractions;
using T.Models;

namespace T.Services;

public class SqliteSessionStorageService : ISessionStorageService
{
    private const int SchemaVersion = 5;
    private const string SessionColumns =
        "Id, Name, Host, Port, Username, Password, PrivateKeyPath, PrivateKeyPassword, FolderId, Description, ProxyJumpSessionId, StepProfileId";

    private readonly string _connectionString;
    private readonly IEncryptionService _encryptionService;

    public SqliteSessionStorageService(IEncryptionService encryptionService)
        : this(encryptionService, Path.Combine(AppPaths.DataDirectory, "T.db"))
    {
    }

    public SqliteSessionStorageService(IEncryptionService encryptionService, string dbPath)
    {
        _encryptionService = encryptionService;

        var folder = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            // The database holds (encrypted) credentials - keep it private to the user.
            try { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, ForeignKeys = true }.ToString();
        InitializeDatabase();
    }

    // ── Schema ───────────────────────────────────────────────────────────

    private void InitializeDatabase()
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        // Oldest schema used a "Hosts" table.
        if (TableExists(connection, "Hosts") && !TableExists(connection, "Sessions"))
            Execute(connection, "ALTER TABLE Hosts RENAME TO Sessions;");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Folders (
                Id         TEXT PRIMARY KEY,
                Name       TEXT NOT NULL,
                ParentId   TEXT,
                IsExpanded INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (ParentId) REFERENCES Folders(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Sessions (
                Id                 TEXT PRIMARY KEY,
                Name               TEXT NOT NULL,
                Host               TEXT NOT NULL,
                Port               INTEGER NOT NULL DEFAULT 22,
                Username           TEXT NOT NULL,
                Password           TEXT NOT NULL,
                PrivateKeyPath     TEXT NOT NULL,
                PrivateKeyPassword TEXT NOT NULL DEFAULT '',
                FolderId           TEXT,
                Description        TEXT NOT NULL,
                ProxyJumpSessionId TEXT,
                StepProfileId      TEXT,
                FOREIGN KEY (FolderId) REFERENCES Folders(Id) ON DELETE SET NULL
            );
            """);

        AddColumnIfMissing(connection, "Folders", "IsExpanded", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "Sessions", "PrivateKeyPassword", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "Sessions", "ProxyJumpSessionId", "TEXT");
        AddColumnIfMissing(connection, "Sessions", "StepProfileId", "TEXT");

        // Previous versions re-created an empty "Hosts" table on every start.
        if (TableExists(connection, "Hosts") && Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM Hosts;"), CultureInfo.InvariantCulture) == 0)
            Execute(connection, "DROP TABLE Hosts;");

        if (GetUserVersion(connection) < 5)
            MigrateSecrets(connection);

        Execute(connection, $"PRAGMA user_version = {SchemaVersion};");
        transaction.Commit();
    }

    /// <summary>Re-encrypts secrets that were stored with the legacy encryption format.</summary>
    private void MigrateSecrets(SqliteConnection connection)
    {
        var updates = new List<(string Id, string Password, string KeyPassword)>();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Password, PrivateKeyPassword FROM Sessions;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var password = reader.GetString(1);
                var keyPassword = reader.GetString(2);

                if (_encryptionService.NeedsMigration(password) || _encryptionService.NeedsMigration(keyPassword))
                {
                    updates.Add((id,
                        _encryptionService.Encrypt(_encryptionService.Decrypt(password)),
                        _encryptionService.Encrypt(_encryptionService.Decrypt(keyPassword))));
                }
            }
        }

        foreach (var (id, password, keyPassword) in updates)
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE Sessions SET Password = @Password, PrivateKeyPassword = @KeyPassword WHERE Id = @Id;";
            update.Parameters.AddWithValue("@Password", password);
            update.Parameters.AddWithValue("@KeyPassword", keyPassword);
            update.Parameters.AddWithValue("@Id", id);
            update.ExecuteNonQuery();
        }
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static int GetUserVersion(SqliteConnection connection) =>
        Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        if (!ColumnExists(connection, table, column))
            Execute(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    // ── Sessions ─────────────────────────────────────────────────────────

    public async Task<List<SshSession>> LoadSessionsAsync()
    {
        var sessions = new List<SshSession>();

        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SessionColumns} FROM Sessions;";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            sessions.Add(ReadSession(reader));

        return sessions;
    }

    public async Task<SshSession?> GetSessionByIdAsync(string id)
    {
        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SessionColumns} FROM Sessions WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSession(reader) : null;
    }

    public async Task AddSessionAsync(SshSession session)
    {
        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO Sessions ({SessionColumns})
            VALUES (@Id, @Name, @Host, @Port, @Username, @Password, @PrivateKeyPath, @PrivateKeyPassword, @FolderId, @Description, @ProxyJumpSessionId, @StepProfileId);
            """;
        AddSessionParameters(cmd, session);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateSessionAsync(SshSession session)
    {
        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Sessions
            SET Name = @Name, Host = @Host, Port = @Port, Username = @Username,
                Password = @Password, PrivateKeyPath = @PrivateKeyPath, PrivateKeyPassword = @PrivateKeyPassword,
                FolderId = @FolderId, Description = @Description, ProxyJumpSessionId = @ProxyJumpSessionId,
                StepProfileId = @StepProfileId
            WHERE Id = @Id;
            """;
        AddSessionParameters(cmd, session);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Deletes a session and clears jump host references to it.</summary>
    public async Task DeleteSessionAsync(string id)
    {
        await using var connection = CreateConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await ExecuteAsync(connection, transaction, "UPDATE Sessions SET ProxyJumpSessionId = NULL WHERE ProxyJumpSessionId = @Id;", id);
        await ExecuteAsync(connection, transaction, "DELETE FROM Sessions WHERE Id = @Id;", id);

        await transaction.CommitAsync();
    }

    // ── Folders ──────────────────────────────────────────────────────────

    public async Task<List<Folder>> LoadFoldersAsync()
    {
        var folders = new List<Folder>();

        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, ParentId, IsExpanded FROM Folders;";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            folders.Add(ReadFolder(reader));

        return folders;
    }

    public async Task<Folder?> GetFolderByIdAsync(string id)
    {
        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, ParentId, IsExpanded FROM Folders WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadFolder(reader) : null;
    }

    public async Task AddFolderAsync(Folder folder)
    {
        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Folders (Id, Name, ParentId, IsExpanded)
            VALUES (@Id, @Name, @ParentId, @IsExpanded);
            """;
        AddFolderParameters(cmd, folder);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateFolderAsync(Folder folder)
    {
        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE Folders
            SET Name = @Name, ParentId = @ParentId, IsExpanded = @IsExpanded
            WHERE Id = @Id;
            """;
        AddFolderParameters(cmd, folder);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes a folder together with all sub folders and the sessions they contain
    /// (matching the "delete folder and all its contents" confirmation), and clears
    /// jump host references to the deleted sessions.
    /// </summary>
    public async Task DeleteFolderAsync(string id)
    {
        await using var connection = CreateConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        const string subtree = """
            WITH RECURSIVE Tree(Id) AS (
                SELECT @Id
                UNION ALL
                SELECT f.Id FROM Folders f JOIN Tree t ON f.ParentId = t.Id
            )
            """;

        await ExecuteAsync(connection, transaction, $"""
            {subtree}
            UPDATE Sessions SET ProxyJumpSessionId = NULL
            WHERE ProxyJumpSessionId IN (SELECT Id FROM Sessions WHERE FolderId IN (SELECT Id FROM Tree));
            """, id);
        await ExecuteAsync(connection, transaction, $"{subtree} DELETE FROM Sessions WHERE FolderId IN (SELECT Id FROM Tree);", id);
        await ExecuteAsync(connection, transaction, "DELETE FROM Folders WHERE Id = @Id;", id);

        await transaction.CommitAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, string id)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private SshSession ReadSession(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Host = reader.GetString(2),
        Port = reader.GetInt32(3),
        Username = reader.GetString(4),
        Password = _encryptionService.Decrypt(reader.GetString(5)),
        PrivateKeyPath = reader.GetString(6),
        PrivateKeyPassword = _encryptionService.Decrypt(reader.GetString(7)),
        FolderId = reader.IsDBNull(8) ? null : reader.GetString(8),
        Description = reader.GetString(9),
        ProxyJumpSessionId = reader.IsDBNull(10) ? null : reader.GetString(10),
        StepProfileId = reader.IsDBNull(11) ? null : reader.GetString(11)
    };

    private static Folder ReadFolder(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        ParentId = reader.IsDBNull(2) ? null : reader.GetString(2),
        IsExpanded = !reader.IsDBNull(3) && reader.GetInt32(3) != 0
    };

    private void AddSessionParameters(SqliteCommand cmd, SshSession session)
    {
        cmd.Parameters.AddWithValue("@Id", session.Id);
        cmd.Parameters.AddWithValue("@Name", session.Name);
        cmd.Parameters.AddWithValue("@Host", session.Host.Trim());
        cmd.Parameters.AddWithValue("@Port", session.Port);
        cmd.Parameters.AddWithValue("@Username", session.Username.Trim());
        cmd.Parameters.AddWithValue("@Password", _encryptionService.Encrypt(session.Password));
        cmd.Parameters.AddWithValue("@PrivateKeyPath", session.PrivateKeyPath.Trim());
        cmd.Parameters.AddWithValue("@PrivateKeyPassword", _encryptionService.Encrypt(session.PrivateKeyPassword));
        cmd.Parameters.AddWithValue("@FolderId", (object?)session.FolderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Description", session.Description);
        cmd.Parameters.AddWithValue("@ProxyJumpSessionId", (object?)session.ProxyJumpSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StepProfileId", (object?)session.StepProfileId ?? DBNull.Value);
    }

    private static void AddFolderParameters(SqliteCommand cmd, Folder folder)
    {
        cmd.Parameters.AddWithValue("@Id", folder.Id);
        cmd.Parameters.AddWithValue("@Name", folder.Name);
        cmd.Parameters.AddWithValue("@ParentId", (object?)folder.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsExpanded", folder.IsExpanded ? 1 : 0);
    }
}
