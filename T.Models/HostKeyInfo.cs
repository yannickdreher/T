namespace T.Models;

public class HostKeyInfo
{
    public required string Host { get; init; }
    public int Port { get; init; }
    public required string KeyType { get; init; }
    public required string Fingerprint { get; init; }
    public required string FingerprintMD5 { get; init; }
}