using System.Diagnostics;
using System.Text;
using T.Abstractions;

namespace T.Services;

public sealed class KnownHostsService : IKnownHostsService
{
    private readonly string _knownHostsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ssh",
        "known_hosts");

    private readonly Lock _lock = new();

    /// <summary>
    /// Überprüft, ob ein Host-Key bereits in known_hosts existiert
    /// </summary>
    public bool IsHostKeyKnown(string host, int port, byte[] hostKey)
    {
        if (!File.Exists(_knownHostsPath))
            return false;

        var hostKeyBase64 = Convert.ToBase64String(hostKey);
        var hostPattern = port == 22 ? host : $"[{host}]:{port}";

        try
        {
            var lines = File.ReadAllLines(_knownHostsPath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    continue;

                var knownHost = parts[0];
                var knownKey = parts[2];

                var hosts = knownHost.Split(',');
                if (hosts.Contains(hostPattern) && knownKey == hostKeyBase64)
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Fügt einen Host-Key zu known_hosts hinzu (nur wenn nicht bereits vorhanden)
    /// </summary>
    public void AddHostKey(string host, int port, string keyType, byte[] hostKey)
    {
        if (IsHostKeyKnown(host, port, hostKey))
            return;

        try
        {
            var sshDir = Path.GetDirectoryName(_knownHostsPath);
            if (!Directory.Exists(sshDir))
            {
                Directory.CreateDirectory(sshDir!);

                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(sshDir!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch { }
                }
            }

            var hostEntry = port == 22 ? host : $"[{host}]:{port}";
            var hostKeyBase64 = Convert.ToBase64String(hostKey);
            var line = $"{hostEntry} {keyType} {hostKeyBase64}";

            lock (_lock)
            {
                using var stream = new FileStream(_knownHostsPath, FileMode.Append, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(line);
            }

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(_knownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save host key to known_hosts: {ex.Message}");
        }
    }

    /// <summary>
    /// Entfernt einen Host aus known_hosts (z.B. wenn sich der Key geändert hat)
    /// </summary>
    public void RemoveHost(string host, int port)
    {
        if (!File.Exists(_knownHostsPath))
            return;

        var hostPattern = port == 22 ? host : $"[{host}]:{port}";

        try
        {
            lock (_lock)
            {
                var lines = File.ReadAllLines(_knownHostsPath);
                var filteredLines = lines.Where(line =>
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                        return true;

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3)
                        return true;

                    var knownHost = parts[0];
                    var hosts = knownHost.Split(',');

                    return !hosts.Contains(hostPattern);
                }).ToArray();

                File.WriteAllLines(_knownHostsPath, filteredLines, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove host from known_hosts: {ex.Message}");
        }
    }
}