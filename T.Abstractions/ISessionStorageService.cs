using T.Models;

namespace T.Abstractions;

public interface ISessionStorageService
{
    Task<List<SshSession>> LoadSessionsAsync();
    Task<SshSession?> GetSessionByIdAsync(string id);
    Task AddSessionAsync(SshSession session);
    Task UpdateSessionAsync(SshSession session);
    Task DeleteSessionAsync(string id);

    Task<List<Folder>> LoadFoldersAsync();
    Task<Folder?> GetFolderByIdAsync(string id);
    Task AddFolderAsync(Folder folder);
    Task UpdateFolderAsync(Folder folder);
    Task DeleteFolderAsync(string id);
}