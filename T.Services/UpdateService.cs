using System.Diagnostics;
using System.Runtime.InteropServices;
using T.Abstractions;
using Velopack;
using Velopack.Sources;

namespace T.Services;

public class UpdateService(ISettingsService settingsService) : IUpdateService
{
    private readonly ISettingsService _settingsService = settingsService;

    private static string GetFullChannel(string channelVariant)
    {
        string platform;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            platform = "win";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            platform = "linux";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            platform = "osx";
        else
            platform = "unknown";

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "unknown"
        };

        string variant = channelVariant.ToLowerInvariant();
        return $"{platform}-{arch}-{variant}";
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            var channel = GetFullChannel(_settingsService.Current.Update.UpdateChannel);
            var source = new GithubSource("https://github.com/yannickdreher/T", null, false);
            var updateManager = new UpdateManager(source, new UpdateOptions
            {
                ExplicitChannel = channel
            });

            if (!updateManager.IsInstalled)
            {
                Debug.WriteLine("[UpdateService] App is not installed via Velopack, skipping update check");
                return null;
            }

            var updateInfo = await updateManager.CheckForUpdatesAsync();
            return updateInfo;
        }
        catch
        {
            return null;
        }
    }

    public async Task DownloadAndInstallUpdatesAsync(UpdateInfo updateInfo)
    {
        try
        {
            var channel = GetFullChannel(_settingsService.Current.Update.UpdateChannel);
            var source = new GithubSource("https://github.com/yannickdreher/T", null, false);
            var updateManager = new UpdateManager(source, new UpdateOptions
            {
                ExplicitChannel = channel
            });

            await updateManager.DownloadUpdatesAsync(updateInfo);
            updateManager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch { }
    }
}