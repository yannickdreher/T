using Velopack;

namespace T.Abstractions;

public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdatesAsync();
    Task DownloadAndInstallUpdatesAsync(UpdateInfo updateInfo);
}