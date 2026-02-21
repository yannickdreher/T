namespace T.Abstractions;

public interface IKnownHostsService
{
    bool IsHostKeyKnown(string host, int port, byte[] hostKey);
    void AddHostKey(string host, int port, string keyType, byte[] hostKey);
    void RemoveHost(string host, int port);
}