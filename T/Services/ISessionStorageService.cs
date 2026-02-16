using System.Collections.Generic;
using System.Threading.Tasks;
using T.Models;

namespace T.Services;

public interface ISessionStorageService
{
    // Hosts
    Task<List<SshSession>> LoadSessionsAsync();
    Task<SshSession?> GetSessionByIdAsync(string id);
    Task AddSessionAsync(SshSession session);
    Task UpdateSessionAsync(SshSession session);
    Task DeleteSessionAsync(string id);

    // Folders
    Task<List<Folder>> LoadFoldersAsync();
    Task<Folder?> GetFolderByIdAsync(string id);
    Task AddFolderAsync(Folder folder);
    Task UpdateFolderAsync(Folder folder);
    Task DeleteFolderAsync(string id);
}