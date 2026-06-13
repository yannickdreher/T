using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using T.Models;

namespace T.UI.Views.Dialogs;

public partial class SessionEditorDialog : UserControl
{
    /// <summary>
    /// Saved sessions that can be selected as the SSH jump host (ProxyJump) for
    /// the session currently being edited. Set by the caller before showing the
    /// dialog. A leading "None" entry (null) represents a direct connection.
    /// </summary>
    public List<SshSession> AvailableProxySessions { get; } = [];

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