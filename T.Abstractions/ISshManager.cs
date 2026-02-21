using T.Models;

namespace T.Abstractions;

public interface ISshManager
{
    ISshService Create(SshSession session, uint cols, uint rows, uint pixelWidth, uint pixelHeight);
    void Release(string sessionId);
}