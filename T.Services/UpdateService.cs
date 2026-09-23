using System.Diagnostics;
using System.Runtime.InteropServices;
using T.Abstractions;
using Velopack;
using Velopack.Sources;

namespace T.Services;

public class UpdateService(ISettingsService settingsService) : IUpdateService
{
    private const string RepositoryUrl = "https://github.com/yannickdreher/T";

    private readonly ISettingsService _settingsService = settingsService;

    private static string GetFullChannel(string channelVariant)
    {
        string platform =
            OperatingSystem.IsWindows() ? "win" :
            OperatingSystem.IsLinux() ? "linux" :
            OperatingSystem.IsMacOS() ? "osx" : "unknown";

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "unknown"
        };

        return $"{platform}-{arch}-{channelVariant.ToLowerInvariant()}";
    }

    private UpdateManager CreateManager() =>
        new(new GithubSource(RepositoryUrl, null, false), new UpdateOptions
        {
            ExplicitChannel = GetFullChannel(_settingsService.Current.Update.UpdateChannel)
        });

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            var updateManager = CreateManager();
            if (!updateManager.IsInstalled)
            {
                Debug.WriteLine("[UpdateService] App is not installed via Velopack, skipping update check");
                return null;
            }

            return await updateManager.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            // Update checks are best effort (offline, rate limited, ...).
            Debug.WriteLine($"[UpdateService] Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads and applies the update, then restarts. Throws when the update fails.</summary>
    public async Task DownloadAndInstallUpdatesAsync(UpdateInfo updateInfo)
    {
        var updateManager = CreateManager();
        await updateManager.DownloadUpdatesAsync(updateInfo);
        updateManager.ApplyUpdatesAndRestart(updateInfo);
    }
}
