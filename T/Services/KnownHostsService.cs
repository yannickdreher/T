using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace T.Services;

public static class KnownHostsService
{
    private static readonly string KnownHostsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ssh",
        "known_hosts"
    );

    /// <summary>
    /// Überprüft, ob ein Host-Key bereits in known_hosts existiert
    /// </summary>
    public static bool IsHostKeyKnown(string host, int port, byte[] hostKey)
    {
        if (!File.Exists(KnownHostsPath))
            return false;

        var hostKeyBase64 = Convert.ToBase64String(hostKey);
        var hostPattern = port == 22 ? host : $"[{host}]:{port}";

        try
        {
            var lines = File.ReadAllLines(KnownHostsPath);
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
    public static void AddHostKey(string host, int port, string keyType, byte[] hostKey)
    {
        // Prüfe ob bereits vorhanden
        if (IsHostKeyKnown(host, port, hostKey))
            return;

        try
        {
            var sshDir = Path.GetDirectoryName(KnownHostsPath);
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

            lock (typeof(KnownHostsService))
            {
                var needsNewline = File.Exists(KnownHostsPath) && new FileInfo(KnownHostsPath).Length > 0;
                
                using var stream = new FileStream(KnownHostsPath, FileMode.Append, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, Encoding.UTF8);

                writer.WriteLine(line);
            }

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(KnownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save host key to known_hosts: {ex.Message}");
        }
    }

    /// <summary>
    /// Entfernt einen Host aus known_hosts (z.B. wenn sich der Key geändert hat)
    /// </summary>
    public static void RemoveHost(string host, int port)
    {
        if (!File.Exists(KnownHostsPath))
            return;

        var hostPattern = port == 22 ? host : $"[{host}]:{port}";

        try
        {
            lock (typeof(KnownHostsService))
            {
                var lines = File.ReadAllLines(KnownHostsPath);
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

                File.WriteAllLines(KnownHostsPath, filteredLines, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove host from known_hosts: {ex.Message}");
        }
    }
}