using System.Threading.Tasks;
using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using T.Services;
using Velopack;

namespace T.Helpers;

public static class UpdateDialogHelper
{
    public static async Task ShowUpdateDialogAsync(Window owner, UpdateInfo updateInfo, string channelVariant)
    {
        var dialog = new ContentDialog
        {
            Title = "Update Available",
            Content = $"A new version {updateInfo.TargetFullRelease.Version} is available.\n\nWould you like to install it now?",
            PrimaryButtonText = "Install",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync(owner);

        if (result == ContentDialogResult.Primary)
        {
            var progressDialog = new ContentDialog
            {
                Title = "Installing Update",
                Content = "Downloading and installing update...\nThe application will restart automatically.",
                IsPrimaryButtonEnabled = false,
                IsSecondaryButtonEnabled = false
            };

            _ = progressDialog.ShowAsync(owner);
            await UpdateService.DownloadAndInstallUpdatesAsync(updateInfo, channelVariant);
        }
    }
}