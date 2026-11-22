using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using T.UI.ViewModels;

namespace T.UI.Views.Dialogs;

public partial class SettingsDialog : UserControl
{
    public SettingsDialog()
    {
        InitializeComponent();
    }

    private async void OnBrowseStepExecutable(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel vm) return;

        var filter = OperatingSystem.IsWindows()
            ? new FilePickerFileType("step CLI") { Patterns = ["step.exe"] }
            : new FilePickerFileType("step CLI") { Patterns = ["step"] };
        if (await PickFileAsync("Select the step CLI", filter) is { } path)
            vm.Step.ExecutablePath = path;
    }

    private async void OnBrowseRootCertificate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDialogViewModel { Step.SelectedProfile: { } profile }) return;

        var filter = new FilePickerFileType("Certificates") { Patterns = ["*.crt", "*.pem", "*.cer"] };
        if (await PickFileAsync("Select the root certificate of the CA", filter) is { } path)
            profile.RootCertificatePath = path;
    }

    private async Task<string?> PickFileAsync(string title, FilePickerFileType filter)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return null;

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [filter, new FilePickerFileType("All Files") { Patterns = ["*"] }]
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }
}
