using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using T.Models;
using T.Services;

namespace T.Tests;

public class KnownHostsServiceTests
{
    private static byte[] CreateKeyBlob(string type = "ssh-ed25519")
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var key = RandomNumberGenerator.GetBytes(32);
        var blob = new byte[4 + typeBytes.Length + 4 + key.Length];
        BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)typeBytes.Length);
        typeBytes.CopyTo(blob, 4);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4 + typeBytes.Length), (uint)key.Length);
        key.CopyTo(blob, 8 + typeBytes.Length);
        return blob;
    }

    [Fact]
    public void UnknownHost_ThenTrusted_IsKnown_AndWrittenWithoutBom()
    {
        using var dir = new TempDirectory();
        var path = dir.File("known_hosts");
        var service = new KnownHostsService(path);
        var key = CreateKeyBlob();

        Assert.Equal(HostKeyStatus.Unknown, service.CheckHostKey("Example.COM", 22, "ssh-ed25519", key, out _));

        service.TrustHostKey("example.com", 22, "ssh-ed25519", key, replaceExisting: false);

        Assert.Equal(HostKeyStatus.Known, service.CheckHostKey("EXAMPLE.com", 22, "ssh-ed25519", key, out _));
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.StartsWith("example.com ssh-ed25519 ", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void DifferentKeyOfSameType_IsReportedAsChanged_AndCanBeReplaced()
    {
        using var dir = new TempDirectory();
        var path = dir.File("known_hosts");
        var service = new KnownHostsService(path);
        var oldKey = CreateKeyBlob();
        var newKey = CreateKeyBlob();

        service.TrustHostKey("host", 2222, "ssh-ed25519", oldKey, replaceExisting: false);

        Assert.Equal(HostKeyStatus.Changed, service.CheckHostKey("host", 2222, "ssh-ed25519", newKey, out var known));
        Assert.Single(known);

        service.TrustHostKey("host", 2222, "ssh-ed25519", newKey, replaceExisting: true);

        Assert.Equal(HostKeyStatus.Known, service.CheckHostKey("host", 2222, "ssh-ed25519", newKey, out _));
        Assert.Equal(HostKeyStatus.Changed, service.CheckHostKey("host", 2222, "ssh-ed25519", oldKey, out _));
        Assert.Single(File.ReadAllLines(path), l => l.Length > 0);
    }

    [Fact]
    public void KeyOfOtherType_IsUnknownNotChanged()
    {
        using var dir = new TempDirectory();
        var service = new KnownHostsService(dir.File("known_hosts"));
        service.TrustHostKey("host", 22, "ssh-ed25519", CreateKeyBlob(), replaceExisting: false);

        Assert.Equal(HostKeyStatus.Unknown, service.CheckHostKey("host", 22, "ecdsa-sha2-nistp256", CreateKeyBlob("ecdsa-sha2-nistp256"), out _));
    }

    [Fact]
    public void HashedEntries_AreMatched()
    {
        using var dir = new TempDirectory();
        var path = dir.File("known_hosts");
        var key = CreateKeyBlob();
        var salt = RandomNumberGenerator.GetBytes(20);
#pragma warning disable CA5350 // OpenSSH hashed known_hosts format
        var hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes("[10.0.0.5]:2200"));
#pragma warning restore CA5350
        File.WriteAllText(path, $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)} ssh-ed25519 {Convert.ToBase64String(key)}\n");

        var service = new KnownHostsService(path);

        Assert.Equal(HostKeyStatus.Known, service.CheckHostKey("10.0.0.5", 2200, "ssh-ed25519", key, out _));
        Assert.Equal(HostKeyStatus.Unknown, service.CheckHostKey("10.0.0.5", 22, "ssh-ed25519", key, out _));
    }

    [Fact]
    public void WildcardsNegationAndRevoked_AreHonored()
    {
        using var dir = new TempDirectory();
        var path = dir.File("known_hosts");
        var key = CreateKeyBlob();
        var revoked = CreateKeyBlob();
        File.WriteAllText(path,
            $"# comment\n*.example.com,!bad.example.com ssh-ed25519 {Convert.ToBase64String(key)}\n" +
            $"@revoked * ssh-ed25519 {Convert.ToBase64String(revoked)}");

        var service = new KnownHostsService(path);

        Assert.Equal(HostKeyStatus.Known, service.CheckHostKey("web.example.com", 22, "ssh-ed25519", key, out _));
        Assert.Equal(HostKeyStatus.Unknown, service.CheckHostKey("bad.example.com", 22, "ssh-ed25519", key, out _));
        Assert.Equal(HostKeyStatus.Revoked, service.CheckHostKey("any.host", 22, "ssh-ed25519", revoked, out _));
    }

    [Fact]
    public void Append_DoesNotGlueOntoLastLineWithoutNewline()
    {
        using var dir = new TempDirectory();
        var path = dir.File("known_hosts");
        var existing = CreateKeyBlob();
        File.WriteAllText(path, $"other ssh-ed25519 {Convert.ToBase64String(existing)}");

        var service = new KnownHostsService(path);
        service.TrustHostKey("host", 22, "ssh-ed25519", CreateKeyBlob(), replaceExisting: false);

        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(HostKeyStatus.Known, service.CheckHostKey("other", 22, "ssh-ed25519", existing, out _));
    }

    [Fact]
    public void ReplacingAKey_KeepsWildcardEntriesAndComments()
    {
        using var dir = new TempDirectory();
        var path = dir.File("known_hosts");
        var wildcardKey = CreateKeyBlob();
        var oldKey = CreateKeyBlob();
        var newKey = CreateKeyBlob();
        File.WriteAllText(path,
            $"*.corp.example ssh-ed25519 {Convert.ToBase64String(wildcardKey)} corp wildcard\n" +
            $"web.corp.example,db.corp.example ssh-ed25519 {Convert.ToBase64String(oldKey)} my comment\n");

        var service = new KnownHostsService(path);
        Assert.Equal(HostKeyStatus.Changed, service.CheckHostKey("web.corp.example", 22, "ssh-ed25519", newKey, out _));

        service.TrustHostKey("web.corp.example", 22, "ssh-ed25519", newKey, replaceExisting: true);

        Assert.Equal(HostKeyStatus.Known, service.CheckHostKey("web.corp.example", 22, "ssh-ed25519", newKey, out _));
        Assert.Equal(HostKeyStatus.Known, service.CheckHostKey("mail.corp.example", 22, "ssh-ed25519", wildcardKey, out _));
        Assert.Equal(HostKeyStatus.Known, service.CheckHostKey("db.corp.example", 22, "ssh-ed25519", oldKey, out _));
        var text = File.ReadAllText(path);
        Assert.Contains("corp wildcard", text);
        Assert.Contains($"db.corp.example ssh-ed25519 {Convert.ToBase64String(oldKey)} my comment", text);
    }

    [Fact]
    public void RemoveHost_KeepsOtherHostsOnSameLine()
    {
        using var dir = new TempDirectory();
        var path = dir.File("known_hosts");
        var key = CreateKeyBlob();
        File.WriteAllText(path, $"a,b ssh-ed25519 {Convert.ToBase64String(key)}\n");

        var service = new KnownHostsService(path);
        service.RemoveHost("a", 22);

        Assert.Equal(HostKeyStatus.Unknown, service.CheckHostKey("a", 22, "ssh-ed25519", key, out _));
        Assert.Equal(HostKeyStatus.Known, service.CheckHostKey("b", 22, "ssh-ed25519", key, out _));
    }
}
