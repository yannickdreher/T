using System.Threading.Tasks;
using T.Abstractions;
using Velopack;

namespace T.UI.ViewModels;

public class UpdateDialogViewModel(IUpdateService updateService, UpdateInfo updateInfo) : ViewModelBase
{
    private readonly IUpdateService _updateService = updateService;

    public UpdateInfo UpdateInfo { get; } = updateInfo;
    public string Version => UpdateInfo.TargetFullRelease.Version.ToString();
    public string Message => $"A new version {Version} is available.\n\nWould you like to install it now?";

    public Task InstallAsync() => _updateService.DownloadAndInstallUpdatesAsync(UpdateInfo);
}