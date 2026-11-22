using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using T.Abstractions;
using T.Models;

namespace T.Services;

/// <summary>
/// Reads and writes the OpenSSH known_hosts file. The file is shared with the
/// system OpenSSH client, so everything written here must stay OpenSSH compatible
/// (no BOM, LF line endings, key type taken from the key blob).
/// </summary>
public sealed class KnownHostsService : IKnownHostsService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _knownHostsPath;
    private readonly Lock _lock = new();

    // Parsed file cache, invalidated when the file changes on disk.
    private List<Entry>? _cache;
    private DateTime _cacheWriteTime;
    private long _cacheLength = -1;

    private sealed record Entry(string? Marker, string[] Patterns, string KeyType, string KeyBase64, string Comment);

    public KnownHostsService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts"))
    {
    }

    public KnownHostsService(string knownHostsPath)
    {
        _knownHostsPath = knownHostsPath;
    }

    public HostKeyStatus CheckHostKey(string host, int port, string keyType, byte[] hostKey, out IReadOnlyList<string> knownFingerprints)
    {
        knownFingerprints = [];
        var name = FormatHostName(host, port);
        var keyBase64 = Convert.ToBase64String(hostKey);
        var blobType = GetKeyTypeFromBlob(hostKey) ?? keyType;

        var found = false;
        var mismatches = new List<string>();

        foreach (var entry in GetEntries())
        {
            if (!MatchesHost(entry.Patterns, name))
                continue;

            if (entry.Marker == "@cert-authority")
                continue;

            var sameKey = entry.KeyBase64 == keyBase64;

            if (entry.Marker == "@revoked")
            {
                if (sameKey) return HostKeyStatus.Revoked;
                continue;
            }

            if (sameKey)
            {
                found = true;
            }
            else if (string.Equals(entry.KeyType, blobType, StringComparison.Ordinal))
            {
                var fingerprint = TryGetFingerprint(entry.KeyBase64);
                if (fingerprint != null) mismatches.Add(fingerprint);
            }
        }

        if (found) return HostKeyStatus.Known;
        if (mismatches.Count > 0)
        {
            knownFingerprints = mismatches;
            return HostKeyStatus.Changed;
        }
        return HostKeyStatus.Unknown;
    }

    public void TrustHostKey(string host, int port, string keyType, byte[] hostKey, bool replaceExisting)
    {
        var name = FormatHostName(host, port);
        var blobType = GetKeyTypeFromBlob(hostKey) ?? keyType;
        var newLine = $"{name} {blobType} {Convert.ToBase64String(hostKey)}";

        try
        {
            lock (_lock)
            {
                EnsureSshDirectory();

                if (replaceExisting && File.Exists(_knownHostsPath))
                {
                    var lines = File.ReadAllLines(_knownHostsPath, Utf8NoBom).ToList();
                    RemoveMatchingPatterns(lines, name, blobType);
                    lines.Add(newLine);
                    WriteAllLinesAtomic(lines);
                }
                else
                {
                    if (CheckHostKey(host, port, keyType, hostKey, out _) == HostKeyStatus.Known)
                        return;
                    AppendLine(newLine);
                }

                _cache = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save host key to known_hosts: {ex.Message}");
            throw new IOException($"The host key could not be saved to {_knownHostsPath}: {ex.Message}", ex);
        }
    }

    public void RemoveHost(string host, int port)
    {
        if (!File.Exists(_knownHostsPath))
            return;

        var name = FormatHostName(host, port);
        try
        {
            lock (_lock)
            {
                var lines = File.ReadAllLines(_knownHostsPath, Utf8NoBom).ToList();
                if (RemoveMatchingPatterns(lines, name, keyType: null))
                    WriteAllLinesAtomic(lines);
                _cache = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove host from known_hosts: {ex.Message}");
        }
    }

    // ── Parsing ──────────────────────────────────────────────────────────

    private List<Entry> GetEntries()
    {
        lock (_lock)
        {
            try
            {
                var info = new FileInfo(_knownHostsPath);
                if (!info.Exists)
                    return [];

                if (_cache != null && info.LastWriteTimeUtc == _cacheWriteTime && info.Length == _cacheLength)
                    return _cache;

                var entries = new List<Entry>();
                foreach (var line in File.ReadLines(_knownHostsPath, Utf8NoBom))
                {
                    var entry = ParseLine(line);
                    if (entry != null) entries.Add(entry);
                }

                _cache = entries;
                _cacheWriteTime = info.LastWriteTimeUtc;
                _cacheLength = info.Length;
                return entries;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to read known_hosts: {ex.Message}");
                return [];
            }
        }
    }

    private static Entry? ParseLine(string line)
    {
        var trimmed = line.Trim().TrimStart('\uFEFF');
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return null;

        var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        string? marker = null;

        if (parts.Length > 0 && parts[0].StartsWith('@'))
        {
            marker = parts[0];
            index = 1;
        }

        if (parts.Length < index + 3)
            return null;

        var comment = string.Join(' ', parts.Skip(index + 3));
        return new Entry(marker, parts[index].Split(','), parts[index + 1], parts[index + 2], comment);
    }

    private static string FormatHostName(string host, int port)
    {
        var normalized = host.Trim().ToLowerInvariant();
        return port == 22 ? normalized : $"[{normalized}]:{port}";
    }

    private static bool MatchesHost(string[] patterns, string name)
    {
        var matched = false;
        foreach (var raw in patterns)
        {
            var pattern = raw;
            var negate = pattern.StartsWith('!');
            if (negate) pattern = pattern[1..];

            if (!PatternMatches(pattern, name))
                continue;

            if (negate) return false;
            matched = true;
        }
        return matched;
    }

    private static bool PatternMatches(string pattern, string name) =>
        pattern.StartsWith("|1|", StringComparison.Ordinal)
            ? HashedPatternMatches(pattern, name)
            : GlobMatches(pattern.ToLowerInvariant(), name);

    /// <summary>HashKnownHosts format: |1|base64(salt)|base64(HMAC-SHA1(salt, hostname)).</summary>
    private static bool HashedPatternMatches(string pattern, string name)
    {
        var parts = pattern.Split('|');
        if (parts.Length != 4) return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
#pragma warning disable CA5350 // HMAC-SHA1 is mandated by the OpenSSH HashKnownHosts format (read-only matching).
            var actual = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(name));
#pragma warning restore CA5350
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>OpenSSH style wildcard match ('*' and '?').</summary>
    internal static bool GlobMatches(string pattern, string text)
    {
        int p = 0, t = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++; t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>Reads the algorithm name that prefixes every SSH public key blob.</summary>
    internal static string? GetKeyTypeFromBlob(byte[] blob)
    {
        if (blob.Length < 4) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (length == 0 || length > 64 || blob.Length < 4 + length) return null;
        return Encoding.ASCII.GetString(blob, 4, (int)length);
    }

    private static string? TryGetFingerprint(string keyBase64)
    {
        try
        {
            return Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(keyBase64))).TrimEnd('=');
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ── Writing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the host name from all lines that name it exactly (plain or hashed), like
    /// "ssh-keygen -R". Wildcard patterns are left alone because they cover other hosts
    /// too; lines that list several hosts keep their other hosts and their comment.
    /// Returns true when something was removed.
    /// </summary>
    private static bool RemoveMatchingPatterns(List<string> lines, string name, string? keyType)
    {
        var changed = false;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var entry = ParseLine(lines[i]);
            if (entry == null || entry.Marker != null)
                continue;
            if (keyType != null && !string.Equals(entry.KeyType, keyType, StringComparison.Ordinal))
                continue;

            var remaining = entry.Patterns.Where(p => !IsExactPattern(p, name)).ToArray();
            if (remaining.Length == entry.Patterns.Length)
                continue;

            changed = true;
            if (remaining.All(p => p.StartsWith('!')))
            {
                lines.RemoveAt(i);
            }
            else
            {
                var comment = entry.Comment.Length > 0 ? " " + entry.Comment : "";
                lines[i] = $"{string.Join(',', remaining)} {entry.KeyType} {entry.KeyBase64}{comment}";
            }
        }
        return changed;
    }

    private static bool IsExactPattern(string pattern, string name) =>
        pattern.StartsWith("|1|", StringComparison.Ordinal)
            ? HashedPatternMatches(pattern, name)
            : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

    private void AppendLine(string line)
    {
        var prefix = "";
        if (File.Exists(_knownHostsPath))
        {
            // Never glue the new entry onto a last line that has no line break.
            using var read = new FileStream(_knownHostsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (read.Length > 0)
            {
                read.Seek(-1, SeekOrigin.End);
                if (read.ReadByte() != '\n') prefix = "\n";
            }
        }

        using (var stream = new FileStream(_knownHostsPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var bytes = Utf8NoBom.GetBytes(prefix + line + "\n");
            stream.Write(bytes);
        }

        RestrictPermissions(_knownHostsPath);
    }

    private void WriteAllLinesAtomic(List<string> lines)
    {
        var tempPath = _knownHostsPath + ".tmp";
        var content = lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
        File.WriteAllText(tempPath, content, Utf8NoBom);
        RestrictPermissions(tempPath);
        File.Move(tempPath, _knownHostsPath, overwrite: true);
    }

    private void EnsureSshDirectory()
    {
        var sshDir = Path.GetDirectoryName(_knownHostsPath);
        if (string.IsNullOrEmpty(sshDir) || Directory.Exists(sshDir))
            return;

        Directory.CreateDirectory(sshDir);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(sshDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
