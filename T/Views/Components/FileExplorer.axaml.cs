using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using T.ViewModels;

namespace T.Views.Components;

public partial class FileExplorer : UserControl
{
    public FileExplorer()
    {
        InitializeComponent();
    }

    private async void OnFileDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionViewModel vm || vm.SelectedFile == null)
            return;

        if (vm.SelectedFile.IsDirectory)
        {
            await vm.NavigateToCommand.ExecuteAsync(vm.SelectedFile);
        }
        else
        {
            await vm.OpenFileCommand.ExecuteAsync(vm.SelectedFile);
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