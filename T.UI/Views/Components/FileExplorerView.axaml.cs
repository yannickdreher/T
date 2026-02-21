using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

public partial class FileExplorer : UserControl
{
    public FileExplorer()
    {
        InitializeComponent();
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SessionViewModel vm || vm.SelectedFile == null)
            return;

        if (vm.SelectedFile.IsDirectory)
        {
            _ = vm.NavigateToCommand.ExecuteAsync(vm.SelectedFile);
        }
        else
        {
            _ = vm.OpenFileCommand.ExecuteAsync(vm.SelectedFile);
        }
    }

    private async void OnUploadClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select File to Upload",
            AllowMultiple = false
        });

        if (files.Count > 0)
        {
            await vm.UploadFileCommand.ExecuteAsync(files[0].Path.LocalPath);
        }
    }
}