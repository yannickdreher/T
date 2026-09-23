using T.Models;

namespace T.Abstractions;

public interface ISshManager
{
    /// <summary>
    /// Creates a new, independent connection for <paramref name="session"/>. A saved session can be
    /// open several times at once (one connection per tab).
    /// </summary>
    ISshService Create(SshSession session, uint cols, uint rows, uint pixelWidth, uint pixelHeight);

    /// <summary>Disconnects and disposes a connection created by <see cref="Create"/>.</summary>
    void Release(ISshService service);
}
