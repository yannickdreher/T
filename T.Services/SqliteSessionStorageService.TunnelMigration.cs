using System.Globalization;
using Microsoft.Data.Sqlite;
using T.Models;

namespace T.Services;

public partial class SqliteSessionStorageService
{
    /// <summary>Name of the folder that receives the SSH connections of tunnels created by version 7.</summary>
    internal const string TunnelFolderName = "Tunnels";

    /// <summary>
    /// Schema 8: tunnels use a terminal profile by reference. Version 7 kept a hidden copy of the
    /// profile per tunnel ("tunnel-owned" session); these copies become normal profiles in a
    /// "Tunnels" folder, so their passwords, keys and step-ca settings stay editable in one place.
    /// Automatic starts with a terminal connection (version 6) are not supported anymore.
    /// </summary>
    private static void MigrateTunnelsToProfiles(SqliteConnection connection)
    {
        var copies = Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM Sessions WHERE TunnelOwned = 1;"), CultureInfo.InvariantCulture);
        if (copies > 0)
        {
            var folderId = Guid.NewGuid().ToString();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Folders (Id, Name, ParentId, IsExpanded) VALUES (@FolderId, @Name, NULL, 1);
                UPDATE Sessions SET TunnelOwned = 0, FolderId = @FolderId WHERE TunnelOwned = 1;
                """;
            command.Parameters.AddWithValue("@FolderId", folderId);
            command.Parameters.AddWithValue("@Name", TunnelFolderName);
            command.ExecuteNonQuery();
        }

        Execute(connection, "UPDATE LocalTunnels SET StartOnConnect = 0;");
    }

    /// <summary>All tunnel rules, each referring to the terminal profile it connects through.</summary>
    public async Task<List<TunnelRule>> LoadAllTunnelsAsync()
    {
        var rules = new List<TunnelRule>();
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, SessionId, Name, LocalPort, TargetHost, TargetPort, StartOnConnect
            FROM LocalTunnels ORDER BY Name COLLATE NOCASE, Id;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rules.Add(new TunnelRule(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5), reader.GetBoolean(6)));
        }
        return rules;
    }
}
