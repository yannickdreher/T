using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace T.Tests;

/// <summary>
/// A directory for one test, deleted afterwards. On Windows only the current user may access it,
/// whatever the permissions of the temp folder are: T refuses data folders and executables that
/// other users could change, so tests would fail on machines with a shared temp folder.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "T.Tests", Guid.NewGuid().ToString("N"));

    public TempDirectory()
    {
        if (OperatingSystem.IsWindows())
            CreatePrivate(Path);
        else
            Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Creates the directory with full control for the current user only (inherited by its content).</summary>
    [SupportedOSPlatform("windows")]
    private static void CreatePrivate(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        new DirectoryInfo(path).Create(security);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
