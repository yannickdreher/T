using T.Models;

namespace T.Abstractions;

public interface ISessionService
{
    event Action<SshSession>? SessionCreated;
    event Action<SshSession>? SessionUpdated;
    event Action<string>? StatusChanged;

    Task<IEnumerable<SshSession>> GetAllAsync();
    Task<SshSession> CreateAsync(SshSession session);
    Task<SshSession> UpdateAsync(SshSession session);
    Task DeleteAsync(string sessionId);
}