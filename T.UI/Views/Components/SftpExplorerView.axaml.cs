using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using T.UI.ViewModels;

namespace T.UI.Views.Components;

public partial class SftpExplorerView : UserControl
{
    public SftpExplorerView()
    {
        InitializeComponent();
    }

    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not SessionViewModel vm)
            return;

        if (vm.NavigateToInputCommand.CanExecute(null))
            vm.NavigateToInputCommand.Execute(null);
        e.Handled = true;
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

    private async void OnNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionViewModel vm || !vm.IsConnected)
            return;

        var name = await PromptAsync("New folder", "Folder name", "");
        if (!string.IsNullOrWhiteSpace(name))
        {
            await vm.CreateDirectoryAsync(name);
        }
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionViewModel vm || vm.SelectedFile == null || vm.SelectedFile.Name == "..")
            return;

        var file = vm.SelectedFile;
        var newName = await PromptAsync("Rename", "New name", file.Name);
        if (!string.IsNullOrWhiteSpace(newName) && newName != file.Name)
        {
            await vm.RenameAsync(file, newName);
        }
    }

    private async System.Threading.Tasks.Task<string?> PromptAsync(string title, string watermark, string initialValue)
    {
        var textBox = new TextBox
        {
            Watermark = watermark,
            Text = initialValue,
            MinWidth = 320
        };

        var dialog = new FAContentDialog
        {
            Title = title,
            Content = textBox,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
            MinWidth = 0
        };

        textBox.AttachedToVisualTree += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        var result = await dialog.ShowAsync();
        return result == FAContentDialogResult.Primary ? textBox.Text?.Trim() : null;
    }
}
