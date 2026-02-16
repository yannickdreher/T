using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using T.Models;

namespace T.Views.Dialogs;

public partial class SessionEditorDialog : UserControl
{
    public SessionEditorDialog()
    {
        InitializeComponent();
    }

    private async void OnBrowsePrivateKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SshSession session) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;

        var options = new FilePickerOpenOptions
        {
            Title = "Select SSH Private Key",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new("SSH Private Keys")
                {
                    Patterns = ["id_rsa", "id_ecdsa", "id_ed25519", "*.pem", "*.key"]
                },
                new("All Files")
                {
                    Patterns = ["*"]
                }
            ]
        };

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(options);
        if (result.Count > 0)
        {
            session.PrivateKeyPath = result[0].Path.LocalPath;
        }
    }
}