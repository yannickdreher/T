using System.Globalization;
using Microsoft.Data.Sqlite;
using T.Abstractions;
using T.Models;

namespace T.Services;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The tunnel gate uses no wait handle and must stay usable by leases released during SSH shutdown.")]
public partial class SqliteSessionStorageService : ISessionStorageService, ITunnelStore
{
    private const int SchemaVersion = 9;
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
        AddColumnIfMissing(connection, "Sessions", "TunnelOwned", "INTEGER NOT NULL DEFAULT 0");

        // Previous versions re-created an empty "Hosts" table on every start.
        if (TableExists(connection, "Hosts") && Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM Hosts;"), CultureInfo.InvariantCulture) == 0)
            Execute(connection, "DROP TABLE Hosts;");

        if (GetUserVersion(connection) < 5)
            MigrateSecrets(connection);
        if (GetUserVersion(connection) < 8)
            MigrateTunnelsToProfiles(connection);
        InitializeTunnels(connection);
        if (GetUserVersion(connection) < 9)
            MigrateLocalTunnels(connection);

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
        cmd.CommandText = $"SELECT {SessionColumns} FROM Sessions WHERE TunnelOwned = 0;";

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
        await InsertSessionAsync(connection, null, session);
    }

    public async Task<IReadOnlyList<SshSession>> ImportSessionsAsync(IReadOnlyList<SshImportProfile> profiles, string? folderId)
    {
        if (profiles.Count == 0)
            return [];

        await using var connection = CreateConnection();
        await using var transaction = connection.BeginTransaction(deferred: false);

        if (folderId != null)
        {
            await using var folder = connection.CreateCommand();
            folder.Transaction = transaction;
            folder.CommandText = "SELECT 1 FROM Folders WHERE Id = @Id;";
            folder.Parameters.AddWithValue("@Id", folderId);
            if (await folder.ExecuteScalarAsync() is null)
                throw new InvalidOperationException("The destination folder no longer exists.");
        }

        var available = new List<SshSession>();
        var existingWithSecrets = new HashSet<string>(StringComparer.Ordinal);
        await using (var load = connection.CreateCommand())
        {
            load.Transaction = transaction;
            load.CommandText = """
                SELECT Id, Name, Host, Port, Username, PrivateKeyPath, FolderId, ProxyJumpSessionId,
                       StepProfileId, Password <> '' OR PrivateKeyPassword <> '' AS HasSecrets
                FROM Sessions WHERE TunnelOwned = 0;
                """;
            await using var reader = await load.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var saved = new SshSession
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Host = reader.GetString(2),
                    Port = reader.GetInt32(3),
                    Username = reader.GetString(4),
                    PrivateKeyPath = reader.GetString(5),
                    FolderId = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ProxyJumpSessionId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    StepProfileId = reader.IsDBNull(8) ? null : reader.GetString(8)
                };
                if (reader.GetBoolean(9))
                    existingWithSecrets.Add(saved.Id);
                available.Add(saved);
            }
        }

        var usedNames = available.Where(session => session.FolderId == folderId)
            .Select(session => session.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Session.Name) || !usedNames.Add(profile.Session.Name))
                throw new InvalidOperationException($"A session named '{profile.Session.Name}' already exists in this selection or folder.");
            if (profile.JumpHosts.Count > SshSession.MaxJumpHops)
                throw new InvalidOperationException($"A session has more than {SshSession.MaxJumpHops} jump hosts.");
        }

        var inserted = new List<SshSession>();
        foreach (var profile in profiles.OrderBy(profile => profile.JumpHosts.Count))
        {
            string? previousId = null;
            foreach (var hop in profile.JumpHosts)
            {
                var matching = available.FirstOrDefault(saved =>
                    !existingWithSecrets.Contains(saved.Id) && SameJumpConnection(saved, hop, previousId));
                if (matching is null)
                {
                    var name = hop.Name;
                    for (int suffix = 1; !usedNames.Add(name); suffix++)
                        name = $"{hop.Name} (jump {suffix})";

                    matching = CloneImportedSession(hop, name, folderId, previousId);
                    await InsertSessionAsync(connection, transaction, matching);
                    available.Add(matching);
                    inserted.Add(matching);
                }
                previousId = matching.Id;
            }

            var target = CloneImportedSession(profile.Session, profile.Session.Name, folderId, previousId);
            await InsertSessionAsync(connection, transaction, target);
            available.Add(target);
            inserted.Add(target);
        }

        await transaction.CommitAsync();
        return inserted;
    }

    private static SshSession CloneImportedSession(SshSession source, string name, string? folderId, string? jumpId) => new()
    {
        Name = name,
        Host = source.Host,
        Port = source.Port,
        Username = source.Username,
        PrivateKeyPath = source.PrivateKeyPath,
        FolderId = folderId,
        ProxyJumpSessionId = jumpId
    };

    private static bool SameJumpConnection(SshSession existing, SshSession imported, string? previousId) =>
        existing.Host.Equals(imported.Host, StringComparison.OrdinalIgnoreCase) &&
        existing.Port == imported.Port && existing.Username == imported.Username &&
        existing.ProxyJumpSessionId == previousId &&
        existing.PrivateKeyPath == imported.PrivateKeyPath &&
        existing.StepProfileId is null;

    private async Task InsertSessionAsync(SqliteConnection connection, SqliteTransaction? transaction, SshSession session)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
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

        await ExecuteAsync(connection, transaction, "UPDATE Sessions SET ProxyJumpSessionId = NULL WHERE ProxyJumpSessionId = @Id AND TunnelOwned = 0;", id);
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
            WHERE TunnelOwned = 0 AND ProxyJumpSessionId IN (SELECT Id FROM Sessions WHERE FolderId IN (SELECT Id FROM Tree));
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
