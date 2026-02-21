using Microsoft.Data.Sqlite;
using T.Abstractions;
using T.Models;

namespace T.Services;

public class SqliteSessionStorageService : ISessionStorageService
{
    private const int SCHEMA_VERSION = 3;
    private readonly string _connectionString;
    private readonly IEncryptionService _encryptionService;

    public SqliteSessionStorageService(IEncryptionService encryptionService)
    {
        _encryptionService = encryptionService;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appData, "T");
        Directory.CreateDirectory(appFolder);
        var dbPath = Path.Combine(appFolder, "T.db");
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = CreateConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Folders (
                Id       TEXT PRIMARY KEY,
                Name     TEXT NOT NULL,
                ParentId TEXT,
                IsExpanded INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (ParentId) REFERENCES Folders(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Hosts (
                Id                TEXT PRIMARY KEY,
                Name              TEXT NOT NULL,
                Host              TEXT NOT NULL,
                Port              INTEGER NOT NULL DEFAULT 22,
                Username          TEXT NOT NULL,
                Password          TEXT NOT NULL,
                PrivateKeyPath    TEXT NOT NULL,
                PrivateKeyPassword TEXT NOT NULL DEFAULT '',
                FolderId          TEXT,
                Description       TEXT NOT NULL,
                FOREIGN KEY (FolderId) REFERENCES Folders(Id) ON DELETE SET NULL
            );
            """;
        cmd.ExecuteNonQuery();

        ApplyMigrations(connection);
    }

    private static void ApplyMigrations(SqliteConnection connection)
    {
        var currentVersion = GetUserVersion(connection);

        if (currentVersion < 1)
        {
            if (!ColumnExists(connection, "Folders", "IsExpanded"))
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE Folders ADD COLUMN IsExpanded INTEGER NOT NULL DEFAULT 0;";
                alter.ExecuteNonQuery();
            }

            SetUserVersion(connection, 1);
        }

        if (currentVersion < 2)
        {
            // Migration: Hosts -> Sessions umbenennen
            if (TableExists(connection, "Hosts") && !TableExists(connection, "Sessions"))
            {
                using var rename = connection.CreateCommand();
                rename.CommandText = "ALTER TABLE Hosts RENAME TO Sessions;";
                rename.ExecuteNonQuery();
            }

            SetUserVersion(connection, 2);
        }

        if (currentVersion < 3)
        {
            // Migration: PrivateKeyPassword Spalte hinzufügen
            if (!ColumnExists(connection, "Sessions", "PrivateKeyPassword"))
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE Sessions ADD COLUMN PrivateKeyPassword TEXT NOT NULL DEFAULT '';";

                alter.ExecuteNonQuery();
            }

            SetUserVersion(connection, 3);
        }

        if (currentVersion < SCHEMA_VERSION)
            SetUserVersion(connection, SCHEMA_VERSION);
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
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
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    // ── Sessions ─────────────────────────────────────────────────────────

    public async Task<List<SshSession>> LoadSessionsAsync()
    {
        var sessions = new List<SshSession>();

        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Host, Port, Username, Password, PrivateKeyPath, PrivateKeyPassword, FolderId, Description FROM Sessions;";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public async Task<SshSession?> GetSessionByIdAsync(string id)
    {
        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Host, Port, Username, Password, PrivateKeyPath, PrivateKeyPassword, FolderId, Description FROM Sessions WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadSession(reader) : null;
    }

    public async Task AddSessionAsync(SshSession session)
    {
        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Sessions (Id, Name, Host, Port, Username, Password, PrivateKeyPath, PrivateKeyPassword, FolderId, Description)
            VALUES (@Id, @Name, @Host, @Port, @Username, @Password, @PrivateKeyPath, @PrivateKeyPassword, @FolderId, @Description);
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
                FolderId = @FolderId, Description = @Description
            WHERE Id = @Id;
            """;
        AddSessionParameters(cmd, session);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteSessionAsync(string id)
    {
        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Sessions WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync();
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
        {
            folders.Add(ReadFolder(reader));
        }

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

    public async Task DeleteFolderAsync(string id)
    {
        await using var connection = CreateConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Folders WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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
        Description = reader.GetString(9)
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
        cmd.Parameters.AddWithValue("@Host", session.Host);
        cmd.Parameters.AddWithValue("@Port", session.Port);
        cmd.Parameters.AddWithValue("@Username", session.Username);
        cmd.Parameters.AddWithValue("@Password", _encryptionService.Encrypt(session.Password));
        cmd.Parameters.AddWithValue("@PrivateKeyPath", session.PrivateKeyPath);
        cmd.Parameters.AddWithValue("@PrivateKeyPassword", _encryptionService.Encrypt(session.PrivateKeyPassword));
        cmd.Parameters.AddWithValue("@FolderId", (object?)session.FolderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Description", session.Description);
    }

    private static void AddFolderParameters(SqliteCommand cmd, Folder folder)
    {
        cmd.Parameters.AddWithValue("@Id", folder.Id);
        cmd.Parameters.AddWithValue("@Name", folder.Name);
        cmd.Parameters.AddWithValue("@ParentId", (object?)folder.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsExpanded", folder.IsExpanded ? 1 : 0);
    }
}