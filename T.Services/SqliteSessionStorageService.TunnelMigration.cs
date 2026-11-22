using System.Globalization;
using Microsoft.Data.Sqlite;

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

        // Databases older than version 6 have no tunnels yet.
        if (TableExists(connection, "LocalTunnels"))
            Execute(connection, "UPDATE LocalTunnels SET StartOnConnect = 0;");
    }
}
